using System.Text;

namespace Civil3DMcpPlugin;

/// <summary>
/// Lightweight plugin-side logger that writes timestamped entries to a rotating
/// log file under <c>%LOCALAPPDATA%\Civil3DMcpPlugin\plugin.log</c> without
/// calling host APIs from worker threads.
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
/// whatever is still queued when the process ends never reaches the file.
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

  private static readonly Lazy<string> LogFilePathLazy = new(BuildLogFilePath);
  private static readonly Lazy<LogFileWriter> FileWriter = new(
    () => new LogFileWriter(LogFilePathLazy.Value, DescribeDroppedLines));

  private static Level _minimumLevel = ReadLevelFromEnvironment();

  public static string LogFilePath => LogFilePathLazy.Value;

  /// <summary>
  /// The last failure to write to the file, or null if the most recent write
  /// succeeded. It reports the writing thread's state, so it lags the call that
  /// caused it by however long that entry waited to be written.
  /// </summary>
  public static string? LastFileError => FileWriter.Value.LastError;

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
  /// Waits for everything already logged to reach the file, then stops the
  /// writing thread. Call it last when the plugin unloads: an entry written
  /// after this still reaches the file, written by the calling thread.
  /// </summary>
  public static void Shutdown(TimeSpan timeout)
  {
    if (FileWriter.IsValueCreated)
    {
      FileWriter.Value.Shutdown(timeout);
    }
  }

  private static void Write(Level level, string component, string message, Exception? exception)
  {
    if (level < _minimumLevel)
    {
      return;
    }

    FileWriter.Value.Write(Format(level, component, message, exception));
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

  private static string BuildLogFilePath()
  {
    var root = Environment.GetEnvironmentVariable("CIVIL3D_MCP_LOG_DIR")
      ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Civil3DMcpPlugin");

    try
    {
      Directory.CreateDirectory(root);
    }
    catch
    {
      // Fall back to temp if LocalAppData is not writable.
      root = Path.Combine(Path.GetTempPath(), "Civil3DMcpPlugin");
      Directory.CreateDirectory(root);
    }

    return Path.Combine(root, "plugin.log");
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
