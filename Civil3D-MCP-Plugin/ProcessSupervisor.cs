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

      _orchestratorProcess = StartPython(Path.Combine(pocDir, "orchestrator"), "local_orchestrator.py", hidden: true, "orchestrator");
      _chatProcess = StartPython(Path.Combine(pocDir, "chat"), "chat_client.py", hidden: false, "chat");

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

    var startInfo = new ProcessStartInfo
    {
      FileName = "node",
      Arguments = "build/index.js",
      WorkingDirectory = submoduleRoot,
      UseShellExecute = false,
      CreateNoWindow = true,
      WindowStyle = ProcessWindowStyle.Hidden,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
    };

    return StartAndLog(startInfo, "node.log", "NodeServer");
  }

  private static Process? StartPython(string workingDir, string scriptFileName, bool hidden, string logName)
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

    var startInfo = new ProcessStartInfo
    {
      FileName = venvPython,
      Arguments = $"\"{scriptFileName}\"",
      WorkingDirectory = workingDir,
      UseShellExecute = false,
      CreateNoWindow = hidden,
      WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
      RedirectStandardOutput = hidden,
      RedirectStandardError = hidden,
    };

    // No longer needed for az (DirectAzureCliCredential calls the azure-cli
    // Python package directly, bypassing az.bat entirely - see
    // azure_cli_direct_credential.py), but harmless and keeps this venv's own
    // Scripts folder generally preferred over anything else on PATH.
    var venvScripts = Path.Combine(workingDir, ".venv", "Scripts");
    startInfo.EnvironmentVariables["PATH"] = venvScripts + ";" + Environment.GetEnvironmentVariable("PATH");

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
