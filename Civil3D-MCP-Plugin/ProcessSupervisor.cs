using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Civil3DMcpPlugin;

/// <summary>
/// Spawns and supervises the Node bridge, Python Local Orchestrator, and Python
/// chat client as child processes, so a human only ever has to NETLOAD this
/// plugin - never start anything in a separate terminal.
///
/// This is drainage-2.0-POC-specific orchestration layered on top of an
/// otherwise standalone, shareable community plugin: everything here is gated
/// on finding a POC directory (via CIVIL3D_MCP_POC_DIR or a best-effort dev
/// default) and no-ops quietly if it isn't found, so the plugin still works
/// on its own for anyone using just the Civil3D-mcp server.
/// </summary>
public static class ProcessSupervisor
{
  private static readonly object Sync = new();
  private static Process? _nodeProcess;
  private static Process? _orchestratorProcess;
  private static Process? _chatProcess;
  private static bool _started;

  // Bundled (@yao-pkg/pkg + PyInstaller) executables are the real deployment
  // target - no Node/Python install required on the engineer's machine. Set
  // to "1" only for active development: rebuilding both bundles from scratch
  // on every code change would wreck the fast dotnet-build/npm-run-build dev
  // loop, so this keeps the original venv/node spawning path available
  // side-by-side rather than deleting it.
  private static bool DevMode =>
    Environment.GetEnvironmentVariable("CIVIL3D_MCP_DEV_MODE") == "1";

  // AppContext.BaseDirectory resolves to the *host process's* base directory
  // (acad.exe's own install folder) for a plugin loaded via NETLOAD, not this
  // assembly's own location - a classic hosted-plugin gotcha. Assembly.Location
  // is this specific DLL's actual path regardless of what loaded it.
  private static string PluginDirectory =>
    Path.GetDirectoryName(typeof(ProcessSupervisor).Assembly.Location)!;

  public static void StartAll(string? pocDir)
  {
    lock (Sync)
    {
      if (_started)
      {
        return;
      }

      if (pocDir == null)
      {
        PluginLog.Info(
          "ProcessSupervisor",
          "POC directory not found; skipping auto-start of Node/orchestrator/chat. " +
          "Set CIVIL3D_MCP_POC_DIR to enable this for a non-default layout.");
        return;
      }

      var submoduleRoot = FindAncestorWithMarker(PluginDirectory, "package.json");
      if (submoduleRoot == null)
      {
        PluginLog.Warn("ProcessSupervisor", "Could not locate the civil3d-mcp submodule root; Node bridge not started.");
      }
      else
      {
        _nodeProcess = StartNodeBridge(submoduleRoot);
      }

      _orchestratorProcess = StartPythonComponent(Path.Combine(pocDir, "orchestrator"), "local_orchestrator", hidden: true, "orchestrator");
      _chatProcess = StartPythonComponent(Path.Combine(pocDir, "chat"), "chat_client", hidden: false, "chat");

      _started = true;
    }
  }

  public static void StopAll()
  {
    lock (Sync)
    {
      KillQuietly(ref _nodeProcess, "NodeServer");
      KillQuietly(ref _orchestratorProcess, "Orchestrator");
      KillQuietly(ref _chatProcess, "Chat");
      _started = false;
    }
  }

  public static string? ResolvePocDir()
  {
    var fromEnv = Environment.GetEnvironmentVariable("CIVIL3D_MCP_POC_DIR");
    if (!string.IsNullOrWhiteSpace(fromEnv))
    {
      return Directory.Exists(fromEnv) ? fromEnv : null;
    }

    // Best-effort dev-machine default: the plugin is still running from its
    // source-tree build output inside the drainage-2.0 superproject. Walk up
    // from the submodule root until an ancestor directory contains a POC/
    // folder, rather than hardcoding a fixed number of ".." segments (which
    // would silently break if the build output path ever changes shape).
    var submoduleRoot = FindAncestorWithMarker(PluginDirectory, "package.json");
    if (submoduleRoot == null)
    {
      return null;
    }

    var repoRoot = FindAncestorWithMarker(submoduleRoot, "POC", isDirectoryMarker: true);
    if (repoRoot == null)
    {
      return null;
    }

    var pocDir = Path.Combine(repoRoot, "POC");
    return Directory.Exists(pocDir) ? pocDir : null;
  }

  private static string? FindAncestorWithMarker(string startDir, string marker, bool isDirectoryMarker = false)
  {
    var dir = new DirectoryInfo(startDir);
    while (dir != null)
    {
      var markerPath = Path.Combine(dir.FullName, marker);
      var found = isDirectoryMarker ? Directory.Exists(markerPath) : File.Exists(markerPath);
      if (found)
      {
        return dir.FullName;
      }

      dir = dir.Parent;
    }

    return null;
  }

  private static Process? StartNodeBridge(string submoduleRoot)
  {
    if (IsPortInUse(3000))
    {
      PluginLog.Info("ProcessSupervisor", "Something is already listening on :3000 - reusing it instead of starting a new Node bridge.");
      return null;
    }

    ProcessStartInfo startInfo;
    if (DevMode)
    {
      startInfo = new ProcessStartInfo
      {
        FileName = "node",
        Arguments = "build/index.js",
        WorkingDirectory = submoduleRoot,
      };
    }
    else
    {
      // Produced by `npm run package` (esbuild -> @yao-pkg/pkg; see
      // package.json) - a single exe with the Node runtime embedded, no
      // separate node/node_modules install needed on this machine.
      var exePath = Path.Combine(submoduleRoot, "dist-bundle", "civil3d-mcp-bridge.exe");
      if (!File.Exists(exePath))
      {
        PluginLog.Warn(
          "ProcessSupervisor",
          $"NodeServer: bundled exe not found at {exePath} (run `npm run package`, or set " +
          "CIVIL3D_MCP_DEV_MODE=1 for the dev venv/node loop); skipping.");
        return null;
      }

      startInfo = new ProcessStartInfo { FileName = exePath, WorkingDirectory = submoduleRoot };
    }

    startInfo.UseShellExecute = false;
    startInfo.CreateNoWindow = true;
    startInfo.WindowStyle = ProcessWindowStyle.Hidden;
    startInfo.RedirectStandardOutput = true;
    startInfo.RedirectStandardError = true;

    // The bridge's own default (Civil3D-mcp's ConnectionManager.ts) connects
    // to the plugin's TCP listener at "localhost", which Node/Windows can
    // resolve to the IPv6 loopback (::1) first - but RpcTcpServer.cs binds
    // only to IPAddress.Loopback (127.0.0.1), so that resolution produces a
    // silent, intermittent ECONNREFUSED ::1:8080 with nothing else wrong:
    // C3DMCPSTATUS reports the plugin as running (checked in-process, no
    // socket involved) while the bridge simply can't reach it. Pin the host
    // explicitly so this never depends on how "localhost" happens to
    // resolve on a given run.
    startInfo.EnvironmentVariables["CIVIL3D_HOST"] = "127.0.0.1";

    return StartAndLog(startInfo, "node.log", "NodeServer");
  }

  private static Process? StartPythonComponent(string workingDir, string componentName, bool hidden, string logName)
  {
    ProcessStartInfo startInfo;

    if (DevMode)
    {
      // pythonw.exe (not python.exe) for anything not hidden - it's the
      // windowless CPython launcher meant for GUI apps like tkinter. Using
      // python.exe here allocates a console window whose lifetime is tied to
      // the GUI process, so closing that "extra" window kills chat too.
      var executableName = hidden ? "python.exe" : "pythonw.exe";
      var venvPython = Path.Combine(workingDir, ".venv", "Scripts", executableName);
      if (!File.Exists(venvPython))
      {
        PluginLog.Warn("ProcessSupervisor", $"{logName}: venv python not found at {venvPython}; skipping.");
        return null;
      }

      startInfo = new ProcessStartInfo
      {
        FileName = venvPython,
        Arguments = $"\"{componentName}.py\"",
        WorkingDirectory = workingDir,
      };

      // No longer needed for az (DirectAzureCliCredential calls the azure-cli
      // Python package directly, bypassing az.bat entirely - see
      // azure_cli_direct_credential.py), but harmless and keeps this venv's own
      // Scripts folder generally preferred over anything else on PATH.
      var venvScripts = Path.Combine(workingDir, ".venv", "Scripts");
      startInfo.EnvironmentVariables["PATH"] = venvScripts + ";" + Environment.GetEnvironmentVariable("PATH");
    }
    else
    {
      // PyInstaller onedir output: <workingDir>/dist/<componentName>/<componentName>.exe.
      // Onedir over onefile - no runtime self-extraction step to fail or add
      // startup latency, which matters since this process is started hidden
      // and polled for readiness. Chat's build uses --windowed, so it never
      // allocates a console in the first place - no pythonw.exe-style
      // distinction needed for the bundled path.
      var exePath = Path.Combine(workingDir, "dist", componentName, $"{componentName}.exe");
      if (!File.Exists(exePath))
      {
        PluginLog.Warn(
          "ProcessSupervisor",
          $"{logName}: bundled exe not found at {exePath} (run PyInstaller, or set " +
          "CIVIL3D_MCP_DEV_MODE=1 for the dev venv loop); skipping.");
        return null;
      }

      startInfo = new ProcessStartInfo { FileName = exePath, WorkingDirectory = workingDir };
    }

    startInfo.UseShellExecute = false;
    startInfo.CreateNoWindow = hidden;
    startInfo.WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal;
    startInfo.RedirectStandardOutput = hidden;
    startInfo.RedirectStandardError = hidden;

    // Python fully buffers stdout when it isn't a real console (true for a
    // redirected, hidden child process, whether it's a venv interpreter or a
    // PyInstaller-bundled one) - print() calls sit in that buffer and may
    // never reach the log file while the process keeps running. Without
    // this, orchestrator.log has come back empty mid-session more than once
    // even though the orchestrator was actively working.
    if (hidden)
    {
      startInfo.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
    }

    return StartAndLog(startInfo, hidden ? $"{logName}.log" : null, logName);
  }

  private static Process? StartAndLog(ProcessStartInfo startInfo, string? logFileName, string component)
  {
    string? logPath = null;
    if (logFileName != null)
    {
      logPath = Path.Combine(Path.GetDirectoryName(PluginLog.LogFilePath)!, logFileName);
      TryResetLogFile(logPath);
    }

    var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

    if (logPath != null)
    {
      var capturedPath = logPath;
      process.OutputDataReceived += (_, e) => { if (e.Data != null) AppendChildLog(capturedPath, e.Data); };
      process.ErrorDataReceived += (_, e) => { if (e.Data != null) AppendChildLog(capturedPath, e.Data); };
    }

    try
    {
      process.Start();
    }
    catch (Exception ex)
    {
      PluginLog.Error("ProcessSupervisor", $"{component} failed to start", ex);
      return null;
    }

    if (logPath != null)
    {
      process.BeginOutputReadLine();
      process.BeginErrorReadLine();
    }

    JobObjectInterop.Assign(process);
    PluginLog.Info("ProcessSupervisor", $"{component} started (PID {process.Id}).");
    return process;
  }

  private static void TryResetLogFile(string path)
  {
    // Fresh log per Civil 3D session, not accumulated across restarts -
    // matches this component's plugin.log rotation in spirit without needing
    // its own rotation machinery for what's a dev-loop convenience feature.
    try
    {
      File.WriteAllText(path, string.Empty);
    }
    catch
    {
      // best effort
    }
  }

  private static void AppendChildLog(string path, string line)
  {
    try
    {
      File.AppendAllText(path, $"[{DateTimeOffset.UtcNow:O}] {line}{Environment.NewLine}");
    }
    catch
    {
      // best effort; a logging failure must never take down the supervised process
    }
  }

  private static bool IsPortInUse(int port)
  {
    try
    {
      using var client = new TcpClient();
      var result = client.BeginConnect("127.0.0.1", port, null, null);
      if (result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(300)) && client.Connected)
      {
        client.EndConnect(result);
        return true;
      }

      return false;
    }
    catch
    {
      return false;
    }
  }

  private static void KillQuietly(ref Process? process, string component)
  {
    if (process == null)
    {
      return;
    }

    try
    {
      if (!process.HasExited)
      {
        process.Kill(entireProcessTree: true);
      }
    }
    catch (Exception ex)
    {
      PluginLog.Swallow("ProcessSupervisor", $"stopping {component}", ex);
    }
    finally
    {
      process.Dispose();
      process = null;
    }
  }
}

/// <summary>
/// Minimal Windows Job Object wrapper: assigns supervised child processes to a
/// job configured to kill them automatically if the job handle closes, so an
/// abnormal Civil 3D exit (crash, Task Manager force-kill) doesn't orphan
/// Node/Python processes even when Terminate() never gets a chance to run.
/// </summary>
internal static class JobObjectInterop
{
  private const uint JobObjectInfoClassExtendedLimit = 9;
  private const uint JobObjectLimitKillOnJobClose = 0x2000;

  private static readonly IntPtr JobHandle = CreateAndConfigureJob();

  public static void Assign(Process process)
  {
    if (JobHandle == IntPtr.Zero)
    {
      return;
    }

    try
    {
      AssignProcessToJobObject(JobHandle, process.Handle);
    }
    catch (Exception ex)
    {
      PluginLog.Swallow("JobObjectInterop", $"assigning PID {process.Id} to job", ex);
    }
  }

  private static IntPtr CreateAndConfigureJob()
  {
    var job = CreateJobObject(IntPtr.Zero, null);
    if (job == IntPtr.Zero)
    {
      return IntPtr.Zero;
    }

    var info = new JobObjectExtendedLimitInformation
    {
      BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose },
    };

    var length = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
    var infoPtr = Marshal.AllocHGlobal(length);
    try
    {
      Marshal.StructureToPtr(info, infoPtr, false);
      SetInformationJobObject(job, JobObjectInfoClassExtendedLimit, infoPtr, (uint)length);
    }
    finally
    {
      Marshal.FreeHGlobal(infoPtr);
    }

    return job;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct JobObjectBasicLimitInformation
  {
    public long PerProcessUserTimeLimit;
    public long PerJobUserTimeLimit;
    public uint LimitFlags;
    public UIntPtr MinimumWorkingSetSize;
    public UIntPtr MaximumWorkingSetSize;
    public uint ActiveProcessLimit;
    public UIntPtr Affinity;
    public uint PriorityClass;
    public uint SchedulingClass;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct IoCounters
  {
    public ulong ReadOperationCount;
    public ulong WriteOperationCount;
    public ulong OtherOperationCount;
    public ulong ReadTransferCount;
    public ulong WriteTransferCount;
    public ulong OtherTransferCount;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct JobObjectExtendedLimitInformation
  {
    public JobObjectBasicLimitInformation BasicLimitInformation;
    public IoCounters IoInfo;
    public UIntPtr ProcessMemoryLimit;
    public UIntPtr JobMemoryLimit;
    public UIntPtr PeakProcessMemoryUsed;
    public UIntPtr PeakJobMemoryUsed;
  }

  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? name);

  [DllImport("kernel32.dll", SetLastError = true)]
  private static extern bool SetInformationJobObject(IntPtr job, uint infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

  [DllImport("kernel32.dll", SetLastError = true)]
  private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
