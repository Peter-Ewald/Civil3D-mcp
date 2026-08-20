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
  public static Task<T> RunInProcessRequestAsync<T>(
    string operation,
    CancellationToken cancellationToken,
    Func<Task<T>> action) =>
    RunWithRequestContextAsync(
      operation,
      $"in-process:{Interlocked.Increment(ref _inProcessRequestCount)}",
      cancellationToken,
      GetActiveDrawingIdentity(),
      action);

  internal static CancellationToken GetCurrentRequestCancellationToken() => CurrentRequestCancellation.Value;

  internal static string GetCurrentRequestOperation() => CurrentRequestOperation.Value ?? "Civil 3D operation";

  internal static string? GetCurrentRequestId() => CurrentRequestId.Value;

  internal static string? GetActiveDrawingIdentity()
  {
    var document = App.DocumentManager.MdiActiveDocument;
    return GetDrawingIdentity(document);
  }

  internal static string? GetDrawingIdentity(Autodesk.AutoCAD.ApplicationServices.Document? document)
  {
    if (document == null) return null;
    var fileName = document.Database.Filename;
    return string.IsNullOrWhiteSpace(fileName) ? document.Name : fileName;
  }

  internal static string? GetExpectedDrawingIdentity() => CurrentExpectedDrawingIdentity.Value;

  internal static async Task<T> RunWithRequestContextAsync<T>(
    string operation,
    string requestId,
    CancellationToken cancellationToken,
    string? expectedDrawingIdentity,
    Func<Task<T>> action)
  {
    var previousOperation = CurrentRequestOperation.Value;
    var previousRequestId = CurrentRequestId.Value;
    var previousCancellation = CurrentRequestCancellation.Value;
    var previousExpectedDrawingIdentity = CurrentExpectedDrawingIdentity.Value;
    CurrentRequestOperation.Value = operation;
    CurrentRequestId.Value = requestId;
    CurrentRequestCancellation.Value = cancellationToken;
    CurrentExpectedDrawingIdentity.Value = expectedDrawingIdentity;
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
