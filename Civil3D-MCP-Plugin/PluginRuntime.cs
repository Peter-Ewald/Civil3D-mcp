using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using App = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3DMcpPlugin;

public sealed record PluginStatus(
  bool IsRunning,
  bool OperationInProgress,
  string? CurrentOperation,
  int QueueDepth,
  int QueueCapacity,
  long? CurrentOperationStartedAtUnixMs,
  string? CurrentRequestId,
  string? CurrentStage,
  long? CurrentStageDurationMs);

/// <summary>
/// How long one completed host operation took, and where the time went inside
/// it.
///
/// The same figures <see cref="CivilExecution"/> already writes to the log, made
/// available to whoever asked for the operation. A caller waiting on a Civil 3D
/// call cannot otherwise tell a queue it was stuck behind from a document lock it
/// could not get from an action that was genuinely slow, and those have nothing
/// in common but their duration.
///
/// The stages arrive already written out rather than as the list they were
/// collected in. That list is appended to from Civil 3D's user interface thread
/// while the caller waits on another, so handing it over would be handing over
/// something still being written.
/// </summary>
/// <param name="Operation">The dispatch method this operation ran for.</param>
/// <param name="TotalMs">How long the whole operation took, including the wait for the host gate.</param>
/// <param name="Stages">The per stage breakdown, as <c>stage=Nms</c> separated by spaces.</param>
public readonly record struct HostOperationTiming(string Operation, long TotalMs, string Stages);

public sealed class JsonRpcDispatchException : Exception
{
  public JsonRpcDispatchException(string code, string message) : base(message)
  {
    Code = code;
  }

  public string Code { get; }
}

public static class PluginRuntime
{
  public const int Port = 8080;

  private static readonly object Sync = new();
  private static RpcTcpServer? _server;
  private static long _inProcessRequestCount;
  private static readonly AsyncLocal<string?> CurrentRequestOperation = new();
  private static readonly AsyncLocal<string?> CurrentRequestId = new();
  private static readonly AsyncLocal<CancellationToken> CurrentRequestCancellation = new();
  private static readonly AsyncLocal<string?> CurrentExpectedDrawingIdentity = new();

  /// <summary>
  /// Where a completed host operation's timings go, for the request that asked
  /// for it. Null for every request that did not ask, which is all of them by
  /// default.
  ///
  /// Set and restored with the rest of the request context rather than only
  /// where an observer is supplied, and that is what keeps it correct. Some
  /// commands start work with <c>Task.Run</c> and answer with a job id
  /// immediately; that work inherits the execution context and would otherwise
  /// still be reporting to a caller that finished with it minutes earlier. Its
  /// own request context sets this back to null, so the case is closed by how
  /// the context is scoped rather than by which commands happen to do it.
  /// </summary>
  private static readonly AsyncLocal<Action<HostOperationTiming>?> CurrentRequestTimings = new();

  private const int MaxQueuedHostOperations = 64;
  private static int _queueDepth;
  private static int _activeOperations;
  private static string? _currentOperation;
  private static string? _currentRequestId;
  private static long? _currentOperationStartedAtUnixMs;
  // Where the active operation currently sits inside the CivilExecution
  // pipeline. Deliberately a single global rather than per-request state:
  // CivilExecution's HostExecutionGate admits exactly one operation at a time,
  // so there is never a second operation whose stage this could confuse.
  //
  // Read from RPC worker threads while written from AutoCAD's UI thread, so
  // both fields go through Volatile rather than the Sync lock - a stage write
  // happens on the UI thread inside the command context, where blocking on a
  // lock held by an unrelated status read would be a real (if small) risk for
  // no benefit. A reference write and a 64-bit long write are each atomic.
  private static string? _currentStage;
  private static long _currentStageStartedAtUnixMs;

  /// <summary>
  /// Consulted once per incoming request, before it is dispatched and therefore
  /// before the host execution gate is taken - so an implementation that waits
  /// on a person does not hold that gate while it waits.
  ///
  /// Null by default, which admits every request: a host that sets nothing here
  /// behaves exactly as it did before this hook existed. An implementation
  /// refuses a request by throwing, and a <see cref="JsonRpcDispatchException"/>
  /// is reported to the caller with its own code, like any other domain error.
  /// </summary>
  public static Func<string, JsonObject?, CancellationToken, Task>? AuthorizeRequest { get; set; }

  public static void StartServer()
  {
    lock (Sync)
    {
      if (_server != null)
      {
        return;
      }

      _server = new RpcTcpServer(Port, HandleRawRequestAsync);
      _server.Start();
    }
  }

  public static void StopServer()
  {
    lock (Sync)
    {
      _server?.Stop();
      _server = null;
      _currentOperation = null;
      _activeOperations = 0;
      _queueDepth = 0;
      _currentRequestId = null;
      _currentOperationStartedAtUnixMs = null;
      ClearOperationStage();
    }
  }

  public static PluginStatus GetStatus()
  {
    // Read the stage pair outside the lock, matching how it is written. A
    // torn read here is harmless: the worst case is a stage name paired with
    // the neighbouring stage's start time, which shifts a reported duration by
    // the length of one pipeline step.
    var currentStage = Volatile.Read(ref _currentStage);
    var stageStartedAt = Volatile.Read(ref _currentStageStartedAtUnixMs);

    lock (Sync)
    {
      return new PluginStatus(
        _server != null,
        _activeOperations > 0,
        _currentOperation,
        _queueDepth,
        MaxQueuedHostOperations,
        _currentOperationStartedAtUnixMs,
        _currentRequestId,
        currentStage,
        currentStage == null
          ? null
          : Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - stageStartedAt));
    }
  }

  public static async Task<string> HandleRawRequestAsync(string rawRequest, CancellationToken cancellationToken)
  {
    JsonNode? parsed;
    try
    {
      parsed = JsonNode.Parse(rawRequest);
    }
    catch (Exception ex)
    {
      return JsonRpcProtocol.SerializeError(null, -32700, "CIVIL3D.INVALID_JSON", $"Invalid JSON request: {ex.Message}");
    }

    if (parsed is not JsonObject request)
    {
      return JsonRpcProtocol.SerializeError(null, -32600, "CIVIL3D.INVALID_REQUEST", "JSON-RPC request must be an object.");
    }

    var id = request["id"]?.DeepClone();
    if (request["jsonrpc"] is not JsonValue versionValue
      || !versionValue.TryGetValue<string>(out var version)
      || version != "2.0")
    {
      return JsonRpcProtocol.SerializeError(id, -32600, "CIVIL3D.INVALID_REQUEST", "JSON-RPC request must specify jsonrpc='2.0'.");
    }

    var method = request["method"] is JsonValue methodValue
      && methodValue.TryGetValue<string>(out var methodText)
      ? methodText
      : null;

    if (string.IsNullOrWhiteSpace(method))
    {
      return JsonRpcProtocol.SerializeError(id, -32600, "CIVIL3D.INVALID_REQUEST", "JSON-RPC request is missing a string method.");
    }

    if (request["params"] != null && request["params"] is not JsonObject)
    {
      return JsonRpcProtocol.SerializeError(id, -32602, "CIVIL3D.INVALID_INPUT", "JSON-RPC params must be an object when provided.");
    }
    var parameters = request["params"] as JsonObject;

    var previousOperation = CurrentRequestOperation.Value;
    var previousRequestId = CurrentRequestId.Value;
    var previousCancellation = CurrentRequestCancellation.Value;
    var previousExpectedDrawingIdentity = CurrentExpectedDrawingIdentity.Value;
    CurrentRequestOperation.Value = method;
    CurrentRequestId.Value = id?.ToJsonString();
    CurrentRequestCancellation.Value = cancellationToken;
    // Recorded here, when the request is admitted, so the identity check inside
    // the command context has an earlier value to compare the live drawing
    // against. Read from this worker thread the same way a queued job already
    // reads it, before any host work is scheduled.
    CurrentExpectedDrawingIdentity.Value = GetActiveDrawingIdentity();

    var timer = System.Diagnostics.Stopwatch.StartNew();
    try
    {
      PluginLog.Debug("Dispatch", $"-> {method} [{CurrentRequestId.Value ?? "no-id"}]");
      if (AuthorizeRequest is { } authorize)
      {
        await authorize(method, parameters, cancellationToken);
      }

      var result = await CommandDispatcher.DispatchAsync(method, parameters, cancellationToken);
      PluginLog.Debug("Dispatch", $"<- {method} [{CurrentRequestId.Value ?? "no-id"}] ok durationMs={timer.ElapsedMilliseconds}");
      return JsonRpcProtocol.SerializeResult(id, result);
    }
    catch (JsonRpcDispatchException ex)
    {
      // Domain-level errors are part of the contract; record at info so they
      // show up in diagnostics without looking like runtime faults.
      PluginLog.Info("Dispatch", $"<- {method} [{CurrentRequestId.Value ?? "no-id"}] dispatch error {ex.Code} durationMs={timer.ElapsedMilliseconds}: {ex.Message}");
      return JsonRpcProtocol.SerializeError(id, JsonRpcProtocol.NumericErrorCode(ex.Code), ex.Code, ex.Message);
    }
    catch (OperationCanceledException)
    {
      PluginLog.Info("Dispatch", $"<- {method} [{CurrentRequestId.Value ?? "no-id"}] cancelled durationMs={timer.ElapsedMilliseconds}");
      return JsonRpcProtocol.SerializeError(id, -32010, "CIVIL3D.CANCELLED", $"Operation '{method}' was cancelled.");
    }
    catch (Exception ex)
    {
      PluginLog.Error("Dispatch", $"<- {method} [{CurrentRequestId.Value ?? "no-id"}] unhandled failure durationMs={timer.ElapsedMilliseconds} category={ex.GetType().Name}", ex);
      return JsonRpcProtocol.SerializeError(id, -32603, "CIVIL3D.INTERNAL_ERROR", "The Civil 3D plugin encountered an unexpected error.");
    }
    finally
    {
      CurrentRequestOperation.Value = previousOperation;
      CurrentRequestId.Value = previousRequestId;
      CurrentRequestCancellation.Value = previousCancellation;
      CurrentExpectedDrawingIdentity.Value = previousExpectedDrawingIdentity;
    }
  }

  /// <summary>
  /// Runs one command from inside this process, with the same request context an
  /// incoming RPC request gets - so the drawing-identity check applies to a
  /// caller in the same process exactly as it does to a remote one.
  /// </summary>
  /// <param name="observeTiming">
  /// Told how long each host operation this request runs took, and where the
  /// time went inside it. Null for a caller that does not care, which leaves the
  /// timings in the log exactly as they were.
  /// </param>
  public static Task<T> RunInProcessRequestAsync<T>(
    string operation,
    CancellationToken cancellationToken,
    Func<Task<T>> action,
    Action<HostOperationTiming>? observeTiming = null) =>
    RunWithRequestContextAsync(
      operation,
      $"in-process:{Interlocked.Increment(ref _inProcessRequestCount)}",
      cancellationToken,
      GetActiveDrawingIdentity(),
      action,
      observeTiming);

  internal static CancellationToken GetCurrentRequestCancellationToken() => CurrentRequestCancellation.Value;

  internal static string GetCurrentRequestOperation() => CurrentRequestOperation.Value ?? "Civil 3D operation";

  internal static string? GetCurrentRequestId() => CurrentRequestId.Value;

  /// <summary>
  /// Which drawing is live right now. Public because the plugin beside this
  /// library reports it alongside a tool call, and reading the document name
  /// there instead would be a second copy of the rule below that could drift
  /// from this one.
  /// </summary>
  public static string? GetActiveDrawingIdentity()
  {
    var document = App.DocumentManager.MdiActiveDocument;
    return GetDrawingIdentity(document);
  }

  /// <summary>
  /// Identifies which open drawing an operation belongs to.
  ///
  /// The document's own name, not <c>Database.Filename</c>: a drawing that has
  /// never been saved reports the template it was created from as its file name,
  /// so two unsaved drawings from one template are indistinguishable by that
  /// measure - which is precisely the case an operation queued against the wrong
  /// drawing needs to be caught in. The document name is distinct per open
  /// document either way, and reads as a drawing name when reported.
  /// </summary>
  internal static string? GetDrawingIdentity(Autodesk.AutoCAD.ApplicationServices.Document? document)
  {
    if (document == null) return null;
    var name = document.Name;
    return string.IsNullOrWhiteSpace(name) ? document.Database.Filename : name;
  }

  internal static string? GetExpectedDrawingIdentity() => CurrentExpectedDrawingIdentity.Value;

  internal static async Task<T> RunWithRequestContextAsync<T>(
    string operation,
    string requestId,
    CancellationToken cancellationToken,
    string? expectedDrawingIdentity,
    Func<Task<T>> action,
    Action<HostOperationTiming>? observeTiming = null)
  {
    var previousOperation = CurrentRequestOperation.Value;
    var previousRequestId = CurrentRequestId.Value;
    var previousCancellation = CurrentRequestCancellation.Value;
    var previousExpectedDrawingIdentity = CurrentExpectedDrawingIdentity.Value;
    var previousTimings = CurrentRequestTimings.Value;
    CurrentRequestOperation.Value = operation;
    CurrentRequestId.Value = requestId;
    CurrentRequestCancellation.Value = cancellationToken;
    CurrentExpectedDrawingIdentity.Value = expectedDrawingIdentity;
    CurrentRequestTimings.Value = observeTiming;
    try
    {
      return await action();
    }
    finally
    {
      CurrentRequestOperation.Value = previousOperation;
      CurrentRequestId.Value = previousRequestId;
      CurrentRequestCancellation.Value = previousCancellation;
      CurrentExpectedDrawingIdentity.Value = previousExpectedDrawingIdentity;
    }
  }

  internal static void QueueHostOperation()
  {
    lock (Sync)
    {
      if (_queueDepth >= MaxQueuedHostOperations)
      {
        throw new JsonRpcDispatchException(
          "CIVIL3D.HOST_BUSY",
          $"Civil 3D host queue is full ({MaxQueuedHostOperations} operations). Retry after current work completes.");
      }

      _queueDepth++;
    }
  }

  internal static void StartHostOperation()
  {
    lock (Sync)
    {
      _queueDepth = Math.Max(0, _queueDepth - 1);
      _activeOperations++;
      _currentOperation = GetCurrentRequestOperation();
      _currentRequestId = GetCurrentRequestId();
      _currentOperationStartedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
  }

  internal static void CancelQueuedHostOperation()
  {
    lock (Sync)
    {
      _queueDepth = Math.Max(0, _queueDepth - 1);
    }
  }

  internal static void CompleteHostOperation()
  {
    lock (Sync)
    {
      _activeOperations = Math.Max(0, _activeOperations - 1);
      if (_activeOperations == 0)
      {
        _currentOperation = null;
        _currentRequestId = null;
        _currentOperationStartedAtUnixMs = null;
        ClearOperationStage();
      }
    }
  }

  /// <summary>
  /// Records where the active host operation currently sits, so a stalled
  /// operation can be located while it is still stalled. <c>civil3d_health</c>
  /// reports this and does not itself acquire the host gate, so it still
  /// answers during a stall that is blocking every other call.
  /// </summary>
  internal static void RecordOperationStage(string stage)
  {
    Volatile.Write(ref _currentStageStartedAtUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    Volatile.Write(ref _currentStage, stage);
  }

  internal static void ClearOperationStage()
  {
    Volatile.Write(ref _currentStage, null);
  }

  /// <summary>
  /// Hands a finished operation's timings to whoever asked for this request, and
  /// to nobody otherwise.
  ///
  /// Guarded, because this is called from the finally block that unwinds a host
  /// operation. An observer that threw there would replace whatever exception is
  /// already on its way out with one about reporting, which is the least useful
  /// error available and would hide the real one.
  /// </summary>
  internal static void PublishOperationTiming(HostOperationTiming timing)
  {
    var observe = CurrentRequestTimings.Value;
    if (observe == null)
    {
      return;
    }

    try
    {
      observe(timing);
    }
    catch (Exception ex)
    {
      PluginLog.Warn("CivilExecution", $"Could not report timings for '{timing.Operation}': {ex.Message}");
    }
  }

  public static object? GetParameter(JsonObject? parameters, string name)
  {
    if (parameters == null)
    {
      return null;
    }

    return parameters.TryGetPropertyValue(name, out var value) ? value : null;
  }

  public static string GetRequiredString(JsonObject? parameters, string name)
  {
    var value = GetParameter(parameters, name) as JsonNode;
    if (value == null)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Missing required parameter '{name}'.");
    }

    var stringValue = value.GetValue<string>();
    if (string.IsNullOrWhiteSpace(stringValue))
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Parameter '{name}' must be a non-empty string.");
    }

    return stringValue;
  }

  public static double GetRequiredDouble(JsonObject? parameters, string name)
  {
    var value = GetParameter(parameters, name) as JsonNode;
    if (value == null)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Missing required parameter '{name}'.");
    }

    return value.GetValue<double>();
  }

  public static int GetRequiredInt(JsonObject? parameters, string name)
  {
    var value = GetParameter(parameters, name) as JsonNode;
    if (value == null)
    {
      throw new JsonRpcDispatchException("CIVIL3D.INVALID_INPUT", $"Missing required parameter '{name}'.");
    }

    return value.GetValue<int>();
  }

  public static string? GetOptionalString(JsonObject? parameters, string name)
  {
    var value = GetParameter(parameters, name) as JsonNode;
    return value == null ? null : value.GetValue<string?>();
  }

  public static double? GetOptionalDouble(JsonObject? parameters, string name)
  {
    var value = GetParameter(parameters, name) as JsonNode;
    return value == null ? null : value.GetValue<double>();
  }

  public static int? GetOptionalInt(JsonObject? parameters, string name)
  {
    var value = GetParameter(parameters, name) as JsonNode;
    return value == null ? null : value.GetValue<int>();
  }

  public static bool? GetOptionalBool(JsonObject? parameters, string name)
  {
    var value = GetParameter(parameters, name) as JsonNode;
    return value == null ? null : value.GetValue<bool>();
  }

}
