using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using App = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(Civil3DMcpPlugin.PluginEntry))]

namespace Civil3DMcpPlugin;

/// <summary>
/// The listener's own start, stop and status commands, and the entry point a
/// host would run on load.
///
/// <b>This library is deliberately not declared as the assembly's extension
/// application in this fork</b>, so AutoCAD never calls <see cref="Initialize"/>
/// or <see cref="Terminate"/> here: the drainage plugin that references this
/// library is the extension application, and it decides whether the listener
/// runs. It does not run it. Nothing goes over the socket any more - the
/// conversation with the agent runs inside Civil 3D and reaches these commands
/// by an assembly reference - and a library resolved as a dependency should not
/// open a port on its own.
///
/// Both methods are kept rather than deleted. They are correct for a host that
/// does load this library directly, and restoring that behaviour is one
/// attribute line: <c>[assembly: ExtensionApplication(typeof(PluginEntry))]</c>.
/// <c>C3DMCPSTART</c> starts the listener for a session meanwhile, which
/// is how a third party MCP client is given one deliberately.
/// </summary>
public sealed class PluginEntry : IExtensionApplication
{
  /// <summary>
  /// How long an unloading plugin waits for queued log entries to reach the
  /// file. Long enough for an ordinary backlog, short enough not to hold up a
  /// closing application if the disk is the thing that is stuck.
  /// </summary>
  private static readonly TimeSpan LogFlushWait = TimeSpan.FromSeconds(2);

  public void Initialize()
  {
    try
    {
      PluginRuntime.StartServer();
      PluginLog.Info("PluginEntry", $"Civil3D MCP plugin initialized on port {PluginRuntime.Port}. Log file: {PluginLog.LogFilePath}");
      WriteMessage("Civil3D MCP plugin initialized.");
    }
    catch (System.Exception ex)
    {
      PluginLog.Error("PluginEntry", "Plugin failed to initialize", ex);
      WriteMessage($"Civil3D MCP plugin failed to initialize: {ex.Message}");
    }
  }

  public void Terminate()
  {
    try
    {
      PluginRuntime.StopServer();
      PluginLog.Info("PluginEntry", "Civil3D MCP plugin terminated cleanly.");
    }
    catch (System.Exception ex)
    {
      PluginLog.Error("PluginEntry", "Error during plugin termination", ex);
    }
    finally
    {
      // Last, and in a finally: log entries are written on a thread of their
      // own, so whatever is still queued when this returns is lost.
      PluginLog.Shutdown(LogFlushWait);
    }
  }

  [CommandMethod("C3DMCPSTART")]
  public void StartCommand()
  {
    PluginRuntime.StartServer();
    WriteMessage($"Civil3D MCP listener started on port {PluginRuntime.Port}.");
  }

  [CommandMethod("C3DMCPSTOP")]
  public void StopCommand()
  {
    PluginRuntime.StopServer();
    WriteMessage("Civil3D MCP listener stopped.");
  }

  [CommandMethod("C3DMCPSTATUS")]
  public void StatusCommand()
  {
    var status = PluginRuntime.GetStatus();
    // Includes the pipeline stage so this command can diagnose an operation
    // that is still running: typed in Civil 3D's own command line, it reports
    // where that operation is stuck without going through the RPC path it may
    // be blocking.
    var stage = status.CurrentStage == null
      ? "<none>"
      : $"{status.CurrentStage} ({status.CurrentStageDurationMs}ms)";
    WriteMessage($"Civil3D MCP listener running: {status.IsRunning}; pending: {status.QueueDepth}; active: {status.OperationInProgress}; current: {status.CurrentOperation ?? "<none>"}; stage: {stage}");
  }

  private static void WriteMessage(string message)
  {
    var doc = App.DocumentManager.MdiActiveDocument;
    doc?.Editor.WriteMessage($"\n{message}");
  }
}
