using System.Text;
using System.Threading.Channels;

namespace Civil3DMcpPlugin;

/// <summary>
/// Owns the log file: the queue in front of it, the single thread that writes
/// it, and its rotation. <see cref="PluginLog"/> decides what a line says and
/// whether its level passes; everything about getting that line onto disk is
/// here.
///
/// The separation is what makes the write asynchronous. A caller hands over a
/// finished line and returns immediately, so code running on Civil 3D's user
/// interface thread - inside an <c>ExecuteInCommandContextAsync</c> callback,
/// for instance - can log per step without doing disk work on the thread the
/// operation it is measuring needs.
///
/// One consumer, so lines reach the file in the order they were written, and a
/// batch of them costs one open, append and close rather than one each.
/// </summary>
internal sealed class LogFileWriter
{
  /// <summary>
  /// How many lines may wait to be written. Large enough that no ordinary burst
  /// reaches it, and bounded rather than unbounded because a log that cannot be
  /// written should cost memory that stops growing, not memory that does not.
  /// </summary>
  private const int QueueCapacity = 4096;

  private readonly object _fileSync = new();
  private readonly string _path;
  private readonly Func<int, string> _describeDroppedLines;
  private readonly long _maxBytes;
  private readonly int _backupsKept;
  private readonly Channel<string> _pending;
  private readonly Lazy<Task> _consumer;

  private int _droppedLines;
  private string? _lastError;

  /// <param name="path">The file to append to. Its directory must already exist.</param>
  /// <param name="describeDroppedLines">
  /// Turns a number of lost lines into the line that reports them. Supplied by
  /// the caller so the report is formatted exactly like every other entry,
  /// rather than this class imitating a format it does not own.
  /// </param>
  public LogFileWriter(
    string path,
    Func<int, string> describeDroppedLines,
    long maxBytes = 5L * 1024 * 1024,
    int backupsKept = 3)
  {
    _path = path;
    _describeDroppedLines = describeDroppedLines;
    _maxBytes = maxBytes;
    _backupsKept = backupsKept;

    // Dropping the oldest rather than refusing the newest, and counting what
    // was dropped: a stalled writer should cost the beginning of the backlog
    // and never block the thread that is trying to log.
    _pending = Channel.CreateBounded<string>(
      new BoundedChannelOptions(QueueCapacity)
      {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
      },
      _ => Interlocked.Increment(ref _droppedLines));

    _consumer = new Lazy<Task>(() =>
      Task.Factory.StartNew(ConsumeAsync, TaskCreationOptions.LongRunning).Unwrap());
  }

  /// <summary>
  /// The last failure to write, or null if the most recent write succeeded. It
  /// reports the consumer's state, so it lags the call that caused it by however
  /// long that line waited in the queue.
  /// </summary>
  public string? LastError => Volatile.Read(ref _lastError);

  public void Write(string line)
  {
    if (_pending.Writer.TryWrite(line))
    {
      // Started here rather than in the constructor, so a process that never
      // logs never starts a thread. Creating it is thread safe and happens once.
      _ = _consumer.Value;
      return;
    }

    // The queue refuses a line only once Shutdown has closed it, which is the
    // one moment at which losing one would matter most - it is the record of
    // what happened during shutdown. So this falls back to writing it here.
    AppendBatch([line]);
  }

  /// <summary>
  /// Stops accepting lines, waits for what is already queued to reach the file,
  /// and gives up after <paramref name="timeout"/> rather than holding up an
  /// unloading plugin. A line written after this still reaches the file, by the
  /// caller's own thread.
  /// </summary>
  public void Shutdown(TimeSpan timeout)
  {
    _pending.Writer.TryComplete();

    if (!_consumer.IsValueCreated)
    {
      return;
    }

    try
    {
      if (!_consumer.Value.Wait(timeout))
      {
        Report("the log queue did not drain before shutdown timed out");
      }
    }
    catch (Exception ex)
    {
      Report($"{ex.GetType().Name}: {ex.Message}");
    }
  }

  private async Task ConsumeAsync()
  {
    var batch = new List<string>();
    var reader = _pending.Reader;

    // Returns false only once the channel is both completed and empty, so this
    // drains what Shutdown left behind before the task finishes.
    while (await reader.WaitToReadAsync().ConfigureAwait(false))
    {
      batch.Clear();
      while (reader.TryRead(out var line))
      {
        batch.Add(line);
      }

      AppendBatch(batch);
    }
  }

  /// <summary>
  /// Locked because the fallback in <see cref="Write"/> can run on another
  /// thread while the consumer is still draining.
  /// </summary>
  private void AppendBatch(IReadOnlyList<string> lines)
  {
    if (lines.Count == 0)
    {
      return;
    }

    try
    {
      lock (_fileSync)
      {
        RotateIfNeeded();

        using var writer = new StreamWriter(_path, append: true, Encoding.UTF8);

        var dropped = Interlocked.Exchange(ref _droppedLines, 0);
        if (dropped > 0)
        {
          writer.WriteLine(_describeDroppedLines(dropped));
        }

        foreach (var line in lines)
        {
          writer.WriteLine(line);
        }
      }

      Volatile.Write(ref _lastError, null);
    }
    catch (Exception ex)
    {
      // Logging itself must never throw into plugin code paths, but a log that
      // is not being written has to say so somewhere.
      Report($"{ex.GetType().Name}: {ex.Message}");
    }
  }

  private void Report(string error)
  {
    Volatile.Write(ref _lastError, error);
    System.Diagnostics.Debug.WriteLine($"Civil3D MCP file logging failed: {error}");
  }

  private void RotateIfNeeded()
  {
    try
    {
      var info = new FileInfo(_path);
      if (!info.Exists || info.Length < _maxBytes)
      {
        return;
      }

      for (var index = _backupsKept; index >= 1; index--)
      {
        var older = $"{_path}.{index}";
        var newer = index == 1 ? _path : $"{_path}.{index - 1}";

        if (File.Exists(older))
        {
          File.Delete(older);
        }

        if (File.Exists(newer))
        {
          File.Move(newer, older);
        }
      }
    }
    catch
    {
      // Best-effort rotation; truncating failures are not worth escalating.
    }
  }
}
