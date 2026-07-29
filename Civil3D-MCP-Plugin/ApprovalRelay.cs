using System.Text.Json;
using System.Windows.Forms;

namespace Civil3DMcpPlugin;

/// <summary>
/// Surfaces the Local Orchestrator's human-approval requests as a native
/// dialog inside Civil 3D, instead of a terminal input() prompt the hidden
/// orchestrator process has no way to show. File-based handoff, matching the
/// convention already used for POC/active_run.json: the orchestrator writes
/// pending_approval.json (toolName, action, parameters) when it needs a
/// decision; this polls for it, shows a Yes/No dialog, and writes
/// approval_response.json with the answer.
/// </summary>
public static class ApprovalRelay
{
  private static readonly object Sync = new();
  private static System.Windows.Forms.Timer? _timer;
  private static string? _pocDir;

  public static void Start(string? pocDir)
  {
    lock (Sync)
    {
      if (_timer != null || pocDir == null)
      {
        return;
      }

      _pocDir = pocDir;
      // A Forms.Timer ticks on the UI thread - the same message pump AutoCAD
      // uses - so MessageBox.Show can be called directly from OnTick without
      // any cross-thread marshaling.
      _timer = new System.Windows.Forms.Timer { Interval = 1000 };
      _timer.Tick += OnTick;
      _timer.Start();
    }
  }

  public static void Stop()
  {
    lock (Sync)
    {
      _timer?.Stop();
      _timer?.Dispose();
      _timer = null;
      _pocDir = null;
    }
  }

  private static void OnTick(object? sender, EventArgs e)
  {
    var pendingPath = Path.Combine(_pocDir!, "pending_approval.json");
    if (!File.Exists(pendingPath))
    {
      return;
    }

    // MessageBox.Show is modal, but it still pumps its own nested message
    // loop - WM_TIMER for this same timer keeps firing while it's open. Stop
    // the timer before showing it, or every tick before you dismiss the
    // dialog stacks another identical one on top (each needing its own
    // click), since the pending file isn't deleted until after you answer.
    _timer!.Stop();

    string toolName = "?";
    string action = "?";
    try
    {
      using var document = JsonDocument.Parse(File.ReadAllText(pendingPath));
      var root = document.RootElement;
      toolName = root.TryGetProperty("toolName", out var t) ? t.GetString() ?? "?" : "?";
      action = root.TryGetProperty("action", out var a) ? a.GetString() ?? "?" : "?";
      var parametersText = root.TryGetProperty("parameters", out var p) ? p.ToString() : "{}";

      var message = $"Agent wants to run:\n\n{toolName} (action={action})\n\nParameters:\n{parametersText}";
      var result = MessageBox.Show(message, "Civil 3D MCP - Approval Required", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

      WriteResponse(result == DialogResult.Yes);
      PluginLog.Info("ApprovalRelay", $"Approval {(result == DialogResult.Yes ? "granted" : "declined")} for {toolName}/{action}.");
    }
    catch (Exception ex)
    {
      PluginLog.Error("ApprovalRelay", $"Failed to process pending approval request for {toolName}/{action}", ex);
      // Answer "no" rather than leave the orchestrator waiting forever for a
      // request we couldn't parse/show - it surfaces as a declined action,
      // which is the safe default, not a hang.
      WriteResponse(false);
    }
    finally
    {
      TryDelete(pendingPath);
      _timer?.Start();
    }
  }

  private static void WriteResponse(bool approved)
  {
    var responsePath = Path.Combine(_pocDir!, "approval_response.json");
    File.WriteAllText(responsePath, JsonSerializer.Serialize(new { approved }));
  }

  private static void TryDelete(string path)
  {
    try
    {
      File.Delete(path);
    }
    catch
    {
      // best effort
    }
  }
}
