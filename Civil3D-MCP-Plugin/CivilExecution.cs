using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using App = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3DMcpPlugin;

public static class CivilExecution
{
  private static readonly SemaphoreSlim HostExecutionGate = new(1, 1);

  /// <summary>
  /// A host operation taking at least this long is reported at warning level
  /// even when debug logging is off. Operations here are single Civil 3D API
  /// calls that normally complete in well under a second, so anything this
  /// slow is the anomaly worth catching rather than routine traffic.
  /// </summary>
  private static readonly TimeSpan SlowOperationThreshold = TimeSpan.FromSeconds(5);

  /// <summary>
  /// Positions in this pipeline, reported by <c>civil3d_health</c> while an
  /// operation is still running. Each names the work currently in progress, so
  /// a stalled operation's reported stage reads as what it is stuck doing.
  ///
  /// The gap between <c>entering-command-context</c> and
  /// <c>inside-command-context</c> is the one that matters most: it separates
  /// "Civil 3D never invoked our delegate" from "our delegate is slow", which
  /// no other signal distinguishes.
  /// </summary>
  private static class Stage
  {
    public const string AwaitingHostGate = "awaiting-host-gate";
    public const string EnteringCommandContext = "entering-command-context";
    public const string InsideCommandContext = "inside-command-context";
    public const string AcquiringDocumentLock = "acquiring-document-lock";
    public const string RunningAction = "running-action";
    public const string Committing = "committing";
    public const string CommandContextExited = "command-context-exited";
  }

  public static async Task<T> ExecuteAsync<T>(Func<Document, CivilDocument, Database, Transaction, T> action, bool write)
  {
    return await ExecuteSerializedAsync(async trace =>
    {
      T? result = default;
      Exception? capturedException = null;

      // Read here, before entering the command context, and captured for the
      // callback to compare against. The expected drawing travels with the
      // request as ambient per-request state, and Civil 3D invokes the callback
      // below from its own command loop rather than as a continuation of this
      // call, so that state is not reliably visible inside it. Reading it on
      // this side removes the dependency on how Civil 3D schedules the callback.
      var expectedDrawingIdentity = PluginRuntime.GetExpectedDrawingIdentity();
      if (string.IsNullOrWhiteSpace(expectedDrawingIdentity))
      {
        // Surfaced rather than passed over: an operation with no recorded
        // drawing is one this check cannot protect, and a check that quietly
        // does nothing is worse than one that says so.
        PluginLog.Warn(
          "CivilExecution",
          $"No expected drawing was recorded for '{PluginRuntime.GetCurrentRequestOperation()}', so the drawing identity check cannot run for it.");
      }

      // Assigned inside the callback and read after it, so a completed write can
      // report the drawing it landed in without doing any logging from inside
      // the command context.
      string? activeDrawingIdentity = null;

      trace.Enter(Stage.EnteringCommandContext);
      await App.DocumentManager.ExecuteInCommandContextAsync(async _ =>
      {
        trace.Enter(Stage.InsideCommandContext);
        try
        {
          var doc = App.DocumentManager.MdiActiveDocument ?? throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active drawing is open in Civil 3D.");
          activeDrawingIdentity = PluginRuntime.GetDrawingIdentity(doc);
          if (!string.IsNullOrWhiteSpace(expectedDrawingIdentity) &&
              !string.Equals(expectedDrawingIdentity, activeDrawingIdentity, StringComparison.OrdinalIgnoreCase))
          {
            throw new JsonRpcDispatchException(
              "CIVIL3D.CONFLICT",
              $"The active drawing changed from '{expectedDrawingIdentity}' to '{activeDrawingIdentity}' while the operation was queued. No drawing changes were made.");
          }
          var civilDoc = CivilApplication.ActiveDocument ?? throw new JsonRpcDispatchException("CIVIL3D.NO_DRAWING", "No active Civil 3D document is available.");
          var database = doc.Database;

          trace.Enter(Stage.AcquiringDocumentLock);
          using var documentLock = doc.LockDocument();
          using var transaction = database.TransactionManager.StartTransaction();

          trace.Enter(Stage.RunningAction);
          result = action(doc, civilDoc, database, transaction);

          if (write)
          {
            trace.Enter(Stage.Committing);
            transaction.Commit();
          }
        }
        catch (Exception ex)
        {
          capturedException = ex;
        }

        await Task.CompletedTask;
      }, null);
      trace.Enter(Stage.CommandContextExited);

      if (write && capturedException == null)
      {
        // Which drawing a write actually landed in, recorded after the command
        // context has unwound rather than from inside it. This is the one fact
        // about a completed write that cannot be recovered afterwards from
        // anything else in this log.
        PluginLog.Info(
          "CivilExecution",
          $"{PluginRuntime.GetCurrentRequestOperation()} wrote to drawing '{activeDrawingIdentity}'.");
      }

      if (capturedException != null)
      {
        throw capturedException;
      }

      return result!;
    });
  }

  public static async Task<T> ExecuteInCommandContextAsync<T>(Func<Task<T>> action)
  {
    return await ExecuteSerializedAsync(async trace =>
    {
      T? result = default;
      Exception? capturedException = null;

      trace.Enter(Stage.EnteringCommandContext);
      await App.DocumentManager.ExecuteInCommandContextAsync(async _ =>
      {
        trace.Enter(Stage.InsideCommandContext);
        try
        {
          trace.Enter(Stage.RunningAction);
          result = await action();
        }
        catch (Exception ex)
        {
          capturedException = ex;
        }
      }, null);
      trace.Enter(Stage.CommandContextExited);

      if (capturedException != null)
      {
        throw capturedException;
      }

      return result!;
    });
  }

  public static Task<T> ReadAsync<T>(Func<Document, CivilDocument, Database, Transaction, T> action)
  {
    return ExecuteAsync(action, false);
  }

  public static Task<T> WriteAsync<T>(Func<Document, CivilDocument, Database, Transaction, T> action)
  {
    return ExecuteAsync(action, true);
  }

  private static async Task<T> ExecuteSerializedAsync<T>(Func<HostOperationTrace, Task<T>> action)
  {
    var cancellationToken = PluginRuntime.GetCurrentRequestCancellationToken();
    var operation = PluginRuntime.GetCurrentRequestOperation();
    PluginRuntime.QueueHostOperation();
    var trace = new HostOperationTrace();
    var started = false;

    try
    {
      trace.Enter(Stage.AwaitingHostGate, publish: false);
      await HostExecutionGate.WaitAsync(cancellationToken);
      started = true;
      PluginRuntime.StartHostOperation();
      cancellationToken.ThrowIfCancellationRequested();
      return await action(trace);
    }
    finally
    {
      if (started)
      {
        PluginRuntime.CompleteHostOperation();
        HostExecutionGate.Release();
      }
      else
      {
        PluginRuntime.CancelQueuedHostOperation();
      }

      // Reported after the command context has unwound, never from inside it:
      // PluginLog writes to disk synchronously on the calling thread, and the
      // callback above runs on Civil 3D's UI thread.
      trace.Report(operation);
    }
  }

  /// <summary>
  /// Per-stage timings for one host operation. Recording a stage is two field
  /// writes and a list append - no I/O - because stages are entered from Civil
  /// 3D's UI thread, where blocking on a disk write would slow the very thing
  /// being measured.
  ///
  /// This reports only operations that complete. One that never completes
  /// writes nothing here by definition, which is why the live stage is also
  /// published through <see cref="PluginRuntime.RecordOperationStage"/> for
  /// <c>civil3d_health</c> to read while the operation is still stuck.
  /// </summary>
  private sealed class HostOperationTrace
  {
    private readonly System.Diagnostics.Stopwatch _elapsed = System.Diagnostics.Stopwatch.StartNew();
    private readonly List<(string Stage, long EnteredAtMs)> _stages = new();

    /// <param name="publish">
    /// Whether to also report this stage as the plugin's current one. False for
    /// stages reached before the host gate is held: several callers can be
    /// queued at once, and a waiting caller publishing its own stage would
    /// overwrite the stage of the operation actually holding the gate - which
    /// is precisely the one worth seeing during a stall. How many are waiting
    /// is already reported separately as the queue depth.
    /// </param>
    public void Enter(string stage, bool publish = true)
    {
      _stages.Add((stage, _elapsed.ElapsedMilliseconds));
      if (publish)
      {
        PluginRuntime.RecordOperationStage(stage);
      }
    }

    public void Report(string operation)
    {
      if (_stages.Count == 0)
      {
        return;
      }

      var totalMs = _elapsed.ElapsedMilliseconds;
      var breakdown = string.Join(
        " ",
        _stages.Select((entry, index) =>
        {
          var endedAtMs = index + 1 < _stages.Count ? _stages[index + 1].EnteredAtMs : totalMs;
          return $"{entry.Stage}={endedAtMs - entry.EnteredAtMs}ms";
        }));

      var message = $"{operation} totalMs={totalMs} {breakdown}";
      PluginLog.Debug("CivilExecution", message);

      if (totalMs >= SlowOperationThreshold.TotalMilliseconds)
      {
        // Surfaced at warning level so an abnormally slow host operation is
        // visible at the default log level, not only when debug is enabled.
        PluginLog.Warn("CivilExecution", $"Slow Civil 3D host operation: {message}");
      }
    }
  }
}
