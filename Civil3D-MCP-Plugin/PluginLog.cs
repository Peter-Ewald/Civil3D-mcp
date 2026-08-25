using System.Text;

namespace Civil3DMcpPlugin;

/// <summary>
/// Lightweight plugin-side logger that writes timestamped entries to a rotating
/// log file, by default under <c>%LOCALAPPDATA%\Civil3DMcpPlugin\plugin.log</c>,
/// without calling host APIs from worker threads.
///
/// This decides what an entry says and whether its level passes, and hands the
/// finished line to <see cref="LogFileWriter"/>, which queues it and writes it
/// on a thread of its own. So a caller returns without touching the disk, and
/// code running on Civil 3D's UI thread - inside an
/// <c>ExecuteInCommandContextAsync</c> callback, for instance - can log per step
/// without slowing the operation it is measuring.
///
/// All methods are safe to call before <c>PluginEntry.Initialize</c> runs.
/// <see cref="Shutdown"/> is the one thing a host owes this logger: without it,
/// whatever is still queued when the process ends never reaches the file. A host
/// that would rather the log lived somewhere its own product names calls
/// <see cref="UseLogFile"/>; nothing here does.
/// </summary>
public static class PluginLog
{
  public enum Level
  {
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
  }

  private const string Component = "PluginLog";

  /// <summary>
  /// Names the directory the log is written in, whoever named the file. An
  /// operator who sets this means it, so it outranks a host's choice of
  /// directory in <see cref="UseLogFile"/>.
  /// </summary>
  private const string DirectoryVariable = "CIVIL3D_MCP_LOG_DIR";

  private const string DefaultDirectoryName = "Civil3DMcpPlugin";
  private const string DefaultFileName = "plugin.log";

  /// <summary>
  /// How long a destination being left waits for its queue to drain before the
  /// new one takes over. Short: a handover happens during plugin load, and an
  /// entry that cannot be written in this long is not going to be.
  /// </summary>
  private static readonly TimeSpan HandoverWait = TimeSpan.FromSeconds(2);

  /// <summary>
  /// Guards the destination and the writer that belongs to it, so an entry can
  /// never be written to a file that is in the middle of being handed over.
  /// </summary>
  private static readonly object DestinationSync = new();

  private static string? _logFilePath;
  private static LogFileWriter? _fileWriter;
  private static Level _minimumLevel = ReadLevelFromEnvironment();

  public static string LogFilePath
  {
    get
    {
      lock (DestinationSync)
      {
        return _logFilePath ??= BuildDefaultLogFilePath();
      }
    }
  }

  /// <summary>
  /// The last failure to write to the file, or null if the most recent write
  /// succeeded. It reports the writing thread's state, so it lags the call that
  /// caused it by however long that entry waited to be written. Null as well
  /// when nothing has been logged yet, since there is no file to have failed.
  /// </summary>
  public static string? LastFileError => Volatile.Read(ref _fileWriter)?.LastError;

  public static bool IsFileLoggingHealthy => LastFileError == null && File.Exists(LogFilePath);

  public static Level MinimumLevel
  {
    get => _minimumLevel;
    set => _minimumLevel = value;
  }

  public static void Debug(string component, string message, Exception? exception = null)
    => Write(Level.Debug, component, message, exception);

  public static void Info(string component, string message, Exception? exception = null)
    => Write(Level.Info, component, message, exception);

  public static void Warn(string component, string message, Exception? exception = null)
    => Write(Level.Warn, component, message, exception);

  public static void Error(string component, string message, Exception? exception = null)
    => Write(Level.Error, component, message, exception);

  /// <summary>
  /// Convenience for <c>try { ... } catch { /* log and swallow */ }</c> blocks.
  /// Keeps the "best effort" reflection pattern used throughout the plugin
  /// while making failures visible at debug level.
  /// </summary>
  public static void Swallow(string component, string operation, Exception exception)
  {
    Write(Level.Debug, component, $"Ignored error during {operation}: {exception.Message}", exception);
  }

  /// <summary>
  /// Sends every following entry to <paramref name="path"/> instead of the
  /// default file, so a host can keep the log where its own product keeps state.
  /// Nothing in this library calls it, and unset the default is used.
  ///
  /// Safe to call after logging has started, which is the point of it. Both this
  /// library and a host assembly may declare an extension application, and the
  /// order AutoCAD calls them in is not the host's to choose, so this hands the
  /// destination over rather than requiring an order nobody controls: the file
  /// being left names the one taking over, and the new file names where its
  /// history is. Entries already queued are written to the file they were logged
  /// against.
  ///
  /// The file name always comes from <paramref name="path"/>. The directory is
  /// <c>CIVIL3D_MCP_LOG_DIR</c> when that is set and the directory of
  /// <paramref name="path"/> otherwise. A path that cannot be prepared for
  /// writing changes nothing and says so, because a logger that quietly loses
  /// its file is worse than one that stays where it was.
  /// </summary>
  public static void UseLogFile(string path)
  {
    if (string.IsNullOrWhiteSpace(path))
    {
      Warn(Component, $"An empty log file path was ignored; entries continue in {LogFilePath}.");
      return;
    }

    string wanted;
    try
    {
      var directory = Environment.GetEnvironmentVariable(DirectoryVariable) is { Length: > 0 } named
        ? named
        : Path.GetDirectoryName(Path.GetFullPath(path))!;
      Directory.CreateDirectory(directory);
      wanted = Path.Combine(directory, Path.GetFileName(path));
    }
    catch (Exception ex)
    {
      Warn(Component, $"'{path}' cannot be written to; entries continue in {LogFilePath}.", ex);
      return;
    }

    string? left = null;
    lock (DestinationSync)
    {
      if (string.Equals(_logFilePath, wanted, StringComparison.OrdinalIgnoreCase))
      {
        return;
      }

      var leaving = _fileWriter;
      left = leaving == null ? null : _logFilePath;
      _logFilePath = wanted;
      Volatile.Write(ref _fileWriter, null);

      if (leaving != null)
      {
        // Into the old file before it is closed, so whoever opens that one
        // first is told where the rest of the log went.
        leaving.Write(Format(Level.Info, Component, $"Logging continues in {wanted}.", exception: null));
        leaving.Shutdown(HandoverWait);
      }
    }

    if (left != null)
    {
      Info(Component, $"Logging continued from {left}.");
    }
  }

  /// <summary>
  /// Waits for everything already logged to reach the file, then stops the
  /// writing thread. Call it last when the plugin unloads: an entry written
  /// after this still reaches the file, written by the calling thread.
  /// </summary>
  public static void Shutdown(TimeSpan timeout)
  {
    Volatile.Read(ref _fileWriter)?.Shutdown(timeout);
  }

  private static void Write(Level level, string component, string message, Exception? exception)
  {
    if (level < _minimumLevel)
    {
      return;
    }

    Writer().Write(Format(level, component, message, exception));
  }

  /// <summary>
  /// The writer for the current destination, created on the first entry so a
  /// process that never logs never starts a writing thread. Read without the
  /// lock once it exists, because entries come from every thread in the plugin -
  /// including Civil 3D's user interface thread, between the stages of the
  /// operation being timed.
  /// </summary>
  private static LogFileWriter Writer()
  {
    var current = Volatile.Read(ref _fileWriter);
    if (current != null)
    {
      return current;
    }

    lock (DestinationSync)
    {
      if (_fileWriter == null)
      {
        _logFilePath ??= BuildDefaultLogFilePath();
        Volatile.Write(ref _fileWriter, new LogFileWriter(_logFilePath, DescribeDroppedLines));
      }

      return _fileWriter;
    }
  }

  /// <summary>
  /// One entry as it appears in the file. A stack trace goes on its own line
  /// inside the same entry rather than being logged separately, so nothing can
  /// come between a failure and the trace that explains it.
  /// </summary>
  private static string Format(Level level, string component, string message, Exception? exception)
  {
    var line = new StringBuilder()
      .Append('[').Append(DateTimeOffset.UtcNow.ToString("O")).Append("] ")
      .Append('[').Append(level).Append("] ")
      .Append('[').Append(component).Append("] ")
      .Append(message);

    if (exception != null)
    {
      line.Append(" | ").Append(exception.GetType().Name).Append(": ").Append(exception.Message);

      if (exception.StackTrace != null)
      {
        line.Append(Environment.NewLine).Append(exception.StackTrace);
      }
    }

    return line.ToString();
  }

  private static string DescribeDroppedLines(int count) =>
    Format(
      Level.Warn,
      Component,
      $"{count} entries were dropped because they were logged faster than they could be written.",
      exception: null);

  private static string BuildDefaultLogFilePath()
  {
    var root = Environment.GetEnvironmentVariable(DirectoryVariable)
      ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        DefaultDirectoryName);

    try
    {
      Directory.CreateDirectory(root);
    }
    catch
    {
      // Fall back to temp if LocalAppData is not writable.
      root = Path.Combine(Path.GetTempPath(), DefaultDirectoryName);
      Directory.CreateDirectory(root);
    }

    return Path.Combine(root, DefaultFileName);
  }

  private static Level ReadLevelFromEnvironment()
  {
    var raw = Environment.GetEnvironmentVariable("CIVIL3D_MCP_LOG_LEVEL");
    if (string.IsNullOrWhiteSpace(raw))
    {
      return Level.Info;
    }

    return raw.Trim().ToLowerInvariant() switch
    {
      "debug" => Level.Debug,
      "info" => Level.Info,
      "warn" or "warning" => Level.Warn,
      "error" => Level.Error,
      _ => Level.Info,
    };
  }
}
