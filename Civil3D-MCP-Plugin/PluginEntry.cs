using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using App = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: ExtensionApplication(typeof(Civil3DMcpPlugin.PluginEntry))]
[assembly: CommandClass(typeof(Civil3DMcpPlugin.PluginEntry))]

namespace Civil3DMcpPlugin;

/// <summary>
/// This assembly's entry point, and the listener's own start, stop and status
/// commands.
///
/// <b>Loading does not start the listener in this fork.</b> Nothing goes over
/// the socket any more: the conversation with the agent runs inside Civil 3D and
/// reaches these commands by an assembly reference, so a library that AutoCAD
/// resolves as a dependency should not open a port on its own. Whoever wants one
/// asks for it with <c>C3DMCPSTART</c>, which is how a third party client that
/// speaks the Model Context Protocol is given a way in deliberately. Restoring
/// the old behaviour is one call in <see cref="Initialize"/>.
///
/// <b>AutoCAD calls this whether or not the attribute above is present.</b> The
/// attribute names the entry point; with no attribute AutoCAD searches the
/// assembly for a public type implementing <see cref="IExtensionApplication"/>
/// and calls that instead, and this class is that type. Removing the attribute
/// therefore does not stop AutoCAD running this code, only stop it saying so,
/// which is why it is declared: the truthful shape is an entry point that is
/// called and does not open a port, rather than one that hides how it was found.
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
    // No listener, deliberately. Nothing is started here at all: this reports
    // that the command library is loaded and reachable, which is all a host
    // needs to know from it.
    PluginLog.Info(
      "PluginEntry",
      $"Civil3D MCP command library loaded. The listener is not running; C3DMCPSTART starts it on port {PluginRuntime.Port}. Log file: {PluginLog.LogFilePath}");
    WriteMessage("Civil3D MCP command library loaded. Listener off; C3DMCPSTART starts it.");
  }

  public void Terminate()
  {
    try
    {
      // Nothing here started it, but C3DMCPSTART may have, and an unloading
      // library must not leave a port open behind it.
      PluginRuntime.StopServer();
      PluginLog.Info("PluginEntry", "Civil3D MCP command library unloaded.");
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
