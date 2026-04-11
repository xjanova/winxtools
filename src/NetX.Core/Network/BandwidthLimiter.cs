using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Security.Principal;
using System.Net.NetworkInformation;

namespace NetX.Core.Network;

/// <summary>
/// Real bandwidth limiter using multiple Windows techniques:
/// 1. Process Throttling: Suspend/Resume cycles for speed limiting
/// 2. Firewall Blocking: Windows Firewall for complete block
/// 3. BITS/NetSh: Built-in Windows bandwidth control where available
/// </summary>
public class BandwidthLimiter : IDisposable
{
    private static BandwidthLimiter? _instance;
    public static BandwidthLimiter Instance => _instance ??= new BandwidthLimiter();

    private readonly ConcurrentDictionary<string, BandwidthRule> _rules = new();
    private readonly ConcurrentDictionary<int, ProcessThrottleState> _throttleStates = new();
    private readonly string _settingsPath;
    private Timer? _enforcementTimer;
    private bool _isEnabled = false;
    private readonly bool _isAdmin;

    public event Action<string, BandwidthRule>? OnRuleApplied;
    public event Action<string>? OnRuleRemoved;
    public event Action<string, string>? OnLimitExceeded;
    public event Action<string>? OnError;
    public event Action<string, bool>? OnRuleStatusChanged;

    #region Native Process Control

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtResumeProcess(IntPtr processHandle);

    private const uint PROCESS_SUSPEND_RESUME = 0x0800;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;

    #endregion

    public BandwidthLimiter()
    {
        _settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NetX", "bandwidth_rules.json");

        _isAdmin = CheckAdminPrivileges();

        // Clean up leftover rules on startup
        Task.Run(() => CleanupAllRules());

        LoadRules();
    }

    private static bool CheckAdminPrivileges()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    #region Properties

    public bool IsAdmin => _isAdmin;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            _isEnabled = value;
            if (value)
                StartEnforcement();
            else
                StopEnforcement();
        }
    }

    public IReadOnlyDictionary<string, BandwidthRule> Rules => _rules;

    #endregion

    #region Rule Management

    /// <summary>
    /// Sets bandwidth limit for a specific process using process throttling
    /// </summary>
    public async Task<bool> SetProcessLimitAsync(string processName, long downloadKBps, long uploadKBps)
    {
        var rule = new BandwidthRule
        {
            ProcessName = processName,
            DownloadLimitKBps = downloadKBps,
            UploadLimitKBps = uploadKBps,
            IsEnabled = true,
            CreatedAt = DateTime.Now,
            Method = LimitMethod.ProcessThrottle
        };

        _rules[processName.ToLowerInvariant()] = rule;
        SaveRules();

        bool success = await ApplyRuleAsync(processName, rule);

        OnRuleApplied?.Invoke(processName, rule);
        OnRuleStatusChanged?.Invoke(processName, success);

        return success;
    }

    /// <summary>
    /// Synchronous version for compatibility
    /// </summary>
    public void SetProcessLimit(string processName, long downloadKBps, long uploadKBps)
    {
        _ = SetProcessLimitAsync(processName, downloadKBps, uploadKBps);
    }

    /// <summary>
    /// Sets bandwidth limit for a network interface
    /// </summary>
    public async Task<bool> SetInterfaceLimitAsync(string interfaceId, long downloadKBps, long uploadKBps)
    {
        var ruleName = $"interface:{interfaceId}";

        var rule = new BandwidthRule
        {
            ProcessName = ruleName,
            DownloadLimitKBps = downloadKBps,
            UploadLimitKBps = uploadKBps,
            IsEnabled = true,
            CreatedAt = DateTime.Now,
            Method = LimitMethod.InterfaceLimit
        };

        _rules[ruleName.ToLowerInvariant()] = rule;
        SaveRules();

        bool success = await ApplyInterfaceRuleAsync(interfaceId, rule);

        OnRuleApplied?.Invoke(ruleName, rule);
        OnRuleStatusChanged?.Invoke(ruleName, success);

        return success;
    }

    /// <summary>
    /// Removes bandwidth limit
    /// </summary>
    public async Task RemoveLimitAsync(string processName)
    {
        var key = processName.ToLowerInvariant();
        if (_rules.TryRemove(key, out var rule))
        {
            SaveRules();
            await RemoveRuleAsync(processName, rule);
            OnRuleRemoved?.Invoke(processName);
        }
    }

    public void RemoveLimit(string processName)
    {
        _ = RemoveLimitAsync(processName);
    }

    /// <summary>
    /// Gets the current limit for a process/interface
    /// </summary>
    public BandwidthRule? GetLimit(string processName)
    {
        _rules.TryGetValue(processName.ToLowerInvariant(), out var rule);
        return rule;
    }

    /// <summary>
    /// Blocks all network traffic for a process (uses Windows Firewall)
    /// </summary>
    public async Task<bool> BlockProcessAsync(string processName)
    {
        return await SetProcessLimitAsync(processName, 0, 0);
    }

    public void BlockProcess(string processName)
    {
        _ = BlockProcessAsync(processName);
    }

    /// <summary>
    /// Unblocks a process
    /// </summary>
    public void UnblockProcess(string processName)
    {
        RemoveLimit(processName);
    }

    #endregion

    #region Rule Application

    private async Task<bool> ApplyRuleAsync(string processName, BandwidthRule rule)
    {
        try
        {
            // For complete block (0 KB/s), use Windows Firewall
            if (rule.DownloadLimitKBps == 0 && rule.UploadLimitKBps == 0)
            {
                return await BlockWithFirewallAsync(processName);
            }

            // For speed limiting, use process throttling
            if (rule.DownloadLimitKBps > 0 || rule.UploadLimitKBps > 0)
            {
                return await ApplyProcessThrottleAsync(processName, rule);
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to apply rule for {processName}: {ex.Message}");
            OnError?.Invoke($"Failed to apply rule: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> ApplyInterfaceRuleAsync(string interfaceId, BandwidthRule rule)
    {
        return await Task.Run(() =>
        {
            try
            {
                var ni = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => n.Id == interfaceId);

                if (ni == null)
                {
                    OnError?.Invoke($"Network interface not found: {interfaceId}");
                    return false;
                }

                // For complete block, use firewall
                if (rule.DownloadLimitKBps == 0 || rule.UploadLimitKBps == 0)
                {
                    return BlockInterfaceWithFirewall(ni.Name,
                        rule.DownloadLimitKBps == 0,
                        rule.UploadLimitKBps == 0);
                }

                // For speed limiting on interface, try NetSh QoS (requires admin)
                if (_isAdmin)
                {
                    return ApplyNetshQos(ni.Name, rule);
                }
                else
                {
                    OnError?.Invoke("Administrator privileges required for interface bandwidth limiting");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to apply interface rule: {ex.Message}");
                OnError?.Invoke($"Failed to apply interface rule: {ex.Message}");
                return false;
            }
        });
    }

    /// <summary>
    /// Apply process throttling using PacketEngine (WinDivert) when available,
    /// falls back to suspend/resume cycles otherwise.
    /// </summary>
    private async Task<bool> ApplyProcessThrottleAsync(string processName, BandwidthRule rule)
    {
        return await Task.Run(() =>
        {
            try
            {
                var cleanName = processName.Replace(".exe", "");
                var processes = Process.GetProcessesByName(cleanName);

                // Try to use PacketEngine for real packet-level throttling
                var packetEngine = PacketEngine.Instance;
                if (packetEngine.IsRunning)
                {
                    // Set throttle in PacketEngine (real packet queuing/dropping)
                    packetEngine.SetThrottle(cleanName,
                        rule.DownloadLimitKBps * 1024,
                        rule.UploadLimitKBps * 1024);

                    Debug.WriteLine($"PacketEngine throttle applied for {processName}: {rule.DownloadLimitKBps} KB/s down, {rule.UploadLimitKBps} KB/s up");
                    return true;
                }

                // Fallback: Use process suspend/resume cycles
                if (processes.Length == 0)
                {
                    Debug.WriteLine($"Process not found: {processName} - rule will apply when process starts");
                    return true;
                }

                foreach (var process in processes)
                {
                    _throttleStates[process.Id] = new ProcessThrottleState
                    {
                        ProcessId = process.Id,
                        ProcessName = processName,
                        DownloadLimitBps = rule.DownloadLimitKBps * 1024,
                        UploadLimitBps = rule.UploadLimitKBps * 1024,
                        IsActive = true
                    };
                }

                if (!_isEnabled)
                {
                    IsEnabled = true;
                }

                Debug.WriteLine($"Suspend/Resume throttle applied for {processName}: {rule.DownloadLimitKBps} KB/s down, {rule.UploadLimitKBps} KB/s up");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to apply process throttle: {ex.Message}");
                return false;
            }
        });
    }

    /// <summary>
    /// Block process using Windows Firewall (most reliable blocking method)
    /// </summary>
    private async Task<bool> BlockWithFirewallAsync(string processName)
    {
        return await Task.Run(() =>
        {
            try
            {
                var cleanName = processName.Replace(".exe", "");
                var processes = Process.GetProcessesByName(cleanName);
                string? exePath = null;

                // Try to get exe path from running process
                foreach (var proc in processes)
                {
                    try
                    {
                        exePath = proc.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(exePath)) break;
                    }
                    catch { }
                }

                // If process not running, try common paths
                if (string.IsNullOrEmpty(exePath))
                {
                    var commonPaths = new[]
                    {
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), cleanName, $"{cleanName}.exe"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), cleanName, $"{cleanName}.exe"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), cleanName, $"{cleanName}.exe"),
                    };

                    exePath = commonPaths.FirstOrDefault(File.Exists);
                }

                if (string.IsNullOrEmpty(exePath))
                {
                    OnError?.Invoke($"Cannot find executable path for {processName}. Start the application first.");
                    return false;
                }

                // Remove existing rules first
                RemoveFirewallRulesSync(processName);

                // Create outbound block rule
                var success = RunNetshCommand(
                    $"advfirewall firewall add rule name=\"NetX_Block_{cleanName}\" dir=out action=block program=\"{exePath}\" enable=yes");

                // Create inbound block rule
                success &= RunNetshCommand(
                    $"advfirewall firewall add rule name=\"NetX_Block_{cleanName}_In\" dir=in action=block program=\"{exePath}\" enable=yes");

                if (success)
                {
                    Debug.WriteLine($"Firewall block applied for {processName}");
                }
                else
                {
                    OnError?.Invoke("Failed to create firewall rules. Run as Administrator.");
                }

                return success;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to block with firewall: {ex.Message}");
                OnError?.Invoke($"Firewall block failed: {ex.Message}");
                return false;
            }
        });
    }

    private bool BlockInterfaceWithFirewall(string interfaceName, bool blockIn, bool blockOut)
    {
        try
        {
            var safeName = interfaceName.Replace(" ", "_").Replace("(", "").Replace(")", "");
            bool success = true;

            // Remove existing rules
            RunNetshCommand($"advfirewall firewall delete rule name=\"NetX_IBlock_{safeName}_In\"");
            RunNetshCommand($"advfirewall firewall delete rule name=\"NetX_IBlock_{safeName}_Out\"");

            if (blockIn)
            {
                success &= RunNetshCommand(
                    $"advfirewall firewall add rule name=\"NetX_IBlock_{safeName}_In\" dir=in action=block interfacetype=any localip=any remoteip=any enable=yes");
            }

            if (blockOut)
            {
                success &= RunNetshCommand(
                    $"advfirewall firewall add rule name=\"NetX_IBlock_{safeName}_Out\" dir=out action=block interfacetype=any localip=any remoteip=any enable=yes");
            }

            return success;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to block interface: {ex.Message}");
            return false;
        }
    }

    private bool ApplyNetshQos(string interfaceName, BandwidthRule rule)
    {
        try
        {
            // Use netsh to apply bandwidth limit via Policy-based QoS
            // Note: This requires Windows Pro/Enterprise and proper QoS setup

            var policyName = $"NetX_QoS_{interfaceName.Replace(" ", "_")}";
            long throttleBitsPerSecond = rule.DownloadLimitKBps * 1024 * 8;

            // Remove existing policy
            var removeCmd = $"powershell -NoProfile -Command \"Remove-NetQosPolicy -Name '{policyName}' -Confirm:$false -ErrorAction SilentlyContinue\"";
            RunCommandHidden("cmd", $"/c {removeCmd}");

            // For actual throttling, we need to use Group Policy or netsh approach
            // Try using BITS-style throttling for background transfers
            var cmd = $"powershell -NoProfile -Command \"" +
                $"try {{ " +
                $"New-NetQosPolicy -Name '{policyName}' -NetworkProfile All -ThrottleRateActionBitsPerSecond {throttleBitsPerSecond} -ErrorAction Stop; " +
                $"Write-Host 'SUCCESS' " +
                $"}} catch {{ Write-Host 'FAILED:' $_.Exception.Message }}\"";

            var result = RunCommandWithOutput("powershell", $"-NoProfile -Command \"{cmd}\"");

            if (result.Contains("SUCCESS"))
            {
                Debug.WriteLine($"NetSh QoS applied for {interfaceName}");
                return true;
            }
            else
            {
                // QoS not available, fall back to monitoring-only mode
                Debug.WriteLine($"QoS not available: {result}");
                OnError?.Invoke("Hardware QoS not supported. Using monitoring mode only.");
                return false;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to apply NetSh QoS: {ex.Message}");
            return false;
        }
    }

    private async Task RemoveRuleAsync(string processName, BandwidthRule rule)
    {
        await Task.Run(() =>
        {
            try
            {
                if (processName.StartsWith("interface:"))
                {
                    var interfaceId = processName.Replace("interface:", "");
                    var ni = NetworkInterface.GetAllNetworkInterfaces()
                        .FirstOrDefault(n => n.Id == interfaceId);

                    if (ni != null)
                    {
                        var safeName = ni.Name.Replace(" ", "_").Replace("(", "").Replace(")", "");
                        RunNetshCommand($"advfirewall firewall delete rule name=\"NetX_IBlock_{safeName}_In\"");
                        RunNetshCommand($"advfirewall firewall delete rule name=\"NetX_IBlock_{safeName}_Out\"");

                        // Remove QoS policy
                        var policyName = $"NetX_QoS_{ni.Name.Replace(" ", "_")}";
                        RunCommandHidden("powershell", $"-NoProfile -Command \"Remove-NetQosPolicy -Name '{policyName}' -Confirm:$false -ErrorAction SilentlyContinue\"");
                    }
                }
                else
                {
                    // Remove firewall rules
                    RemoveFirewallRulesSync(processName);

                    // Remove throttle states
                    var cleanName = processName.Replace(".exe", "").ToLowerInvariant();
                    var toRemove = _throttleStates.Where(kv =>
                        kv.Value.ProcessName.Replace(".exe", "").ToLowerInvariant() == cleanName).ToList();

                    foreach (var kv in toRemove)
                    {
                        _throttleStates.TryRemove(kv.Key, out _);
                    }
                }

                Debug.WriteLine($"Removed rule for {processName}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to remove rule: {ex.Message}");
            }
        });
    }

    private void RemoveFirewallRulesSync(string processName)
    {
        var cleanName = processName.Replace(".exe", "");
        RunNetshCommand($"advfirewall firewall delete rule name=\"NetX_Block_{cleanName}\"");
        RunNetshCommand($"advfirewall firewall delete rule name=\"NetX_Block_{cleanName}_In\"");
    }

    #endregion

    #region Enforcement (Process Throttling)

    private void StartEnforcement()
    {
        _enforcementTimer?.Dispose();
        _enforcementTimer = new Timer(EnforceLimits, null,
            TimeSpan.FromMilliseconds(200),
            TimeSpan.FromMilliseconds(200));
    }

    private void StopEnforcement()
    {
        _enforcementTimer?.Dispose();
        _enforcementTimer = null;

        // Resume all throttled processes
        foreach (var state in _throttleStates.Values)
        {
            if (state.IsSuspended)
            {
                ResumeProcess(state.ProcessId);
                state.IsSuspended = false;
            }
        }
    }

    private void EnforceLimits(object? state)
    {
        try
        {
            // First, check for new processes that match our rules
            CheckForNewProcesses();

            // Then enforce limits on tracked processes
            foreach (var throttleState in _throttleStates.Values.ToList())
            {
                if (!throttleState.IsActive) continue;

                try
                {
                    EnforceProcessThrottle(throttleState);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error enforcing limit for PID {throttleState.ProcessId}: {ex.Message}");
                    // Process might have exited
                    _throttleStates.TryRemove(throttleState.ProcessId, out _);
                }
            }
        }
        catch { }
    }

    private void CheckForNewProcesses()
    {
        foreach (var rule in _rules.Values.Where(r => r.IsEnabled && !r.ProcessName.StartsWith("interface:")))
        {
            var cleanName = rule.ProcessName.Replace(".exe", "");
            var processes = Process.GetProcessesByName(cleanName);

            foreach (var proc in processes)
            {
                if (!_throttleStates.ContainsKey(proc.Id))
                {
                    _throttleStates[proc.Id] = new ProcessThrottleState
                    {
                        ProcessId = proc.Id,
                        ProcessName = rule.ProcessName,
                        DownloadLimitBps = rule.DownloadLimitKBps * 1024,
                        UploadLimitBps = rule.UploadLimitKBps * 1024,
                        IsActive = true
                    };
                    Debug.WriteLine($"Started tracking process {cleanName} (PID: {proc.Id})");
                }
            }
        }
    }

    private void EnforceProcessThrottle(ProcessThrottleState state)
    {
        // Get current bandwidth usage from NetworkMonitor
        var stats = NetworkMonitor.Instance.GetProcessStats(state.ProcessId);
        if (stats == null)
        {
            // Process might have exited
            try
            {
                var proc = Process.GetProcessById(state.ProcessId);
                if (proc.HasExited)
                {
                    _throttleStates.TryRemove(state.ProcessId, out _);
                }
            }
            catch
            {
                _throttleStates.TryRemove(state.ProcessId, out _);
            }
            return;
        }

        double currentSpeed = stats.DownloadSpeed + stats.UploadSpeed;
        double limitSpeed = state.DownloadLimitBps + state.UploadLimitBps;

        if (limitSpeed <= 0) return; // No limit

        // Calculate if we're over the limit
        double ratio = currentSpeed / limitSpeed;

        if (ratio > 1.2) // 20% over limit
        {
            // Need to throttle - suspend briefly
            if (!state.IsSuspended)
            {
                SuspendProcess(state.ProcessId);
                state.IsSuspended = true;
                state.LastSuspendTime = DateTime.Now;

                // Calculate suspend duration based on how much over the limit
                // More over = longer suspend
                int suspendMs = Math.Min(100, (int)((ratio - 1) * 50));
                state.SuspendDurationMs = suspendMs;

                OnLimitExceeded?.Invoke(state.ProcessName,
                    $"Speed: {currentSpeed / 1024:F0} KB/s > Limit: {limitSpeed / 1024:F0} KB/s");
            }
        }
        else if (state.IsSuspended)
        {
            // Check if we should resume
            var elapsed = (DateTime.Now - state.LastSuspendTime).TotalMilliseconds;
            if (elapsed >= state.SuspendDurationMs)
            {
                ResumeProcess(state.ProcessId);
                state.IsSuspended = false;
            }
        }
    }

    private void SuspendProcess(int processId)
    {
        try
        {
            IntPtr handle = OpenProcess(PROCESS_SUSPEND_RESUME, false, processId);
            if (handle != IntPtr.Zero)
            {
                NtSuspendProcess(handle);
                CloseHandle(handle);
            }
        }
        catch { }
    }

    private void ResumeProcess(int processId)
    {
        try
        {
            IntPtr handle = OpenProcess(PROCESS_SUSPEND_RESUME, false, processId);
            if (handle != IntPtr.Zero)
            {
                NtResumeProcess(handle);
                CloseHandle(handle);
            }
        }
        catch { }
    }

    #endregion

    #region Helper Methods

    private bool RunNetshCommand(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = arguments,
                UseShellExecute = true,
                CreateNoWindow = true,
                Verb = _isAdmin ? "" : "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            var proc = Process.Start(psi);
            return proc?.WaitForExit(5000) == true && proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private void RunCommandHidden(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            Process.Start(psi)?.WaitForExit(5000);
        }
        catch { }
    }

    private string RunCommandWithOutput(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return "";

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            return output;
        }
        catch
        {
            return "";
        }
    }

    #endregion

    #region Cleanup

    public void CleanupAllRules()
    {
        try
        {
            // Remove all NetX firewall rules using PowerShell (faster than multiple netsh calls)
            var psCommand = @"
                Get-NetFirewallRule -ErrorAction SilentlyContinue |
                Where-Object { $_.DisplayName -like 'NetX_*' } |
                Remove-NetFirewallRule -ErrorAction SilentlyContinue;
                Get-NetQosPolicy -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -like 'NetX_*' } |
                Remove-NetQosPolicy -Confirm:$false -ErrorAction SilentlyContinue
            ";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{psCommand}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            Process.Start(psi)?.WaitForExit(10000);
            Debug.WriteLine("Cleaned up all NetX rules");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to cleanup rules: {ex.Message}");
        }
    }

    public void ResetAllLimits()
    {
        // Stop enforcement
        StopEnforcement();

        // Clear all rules
        _rules.Clear();
        _throttleStates.Clear();
        SaveRules();

        // Cleanup system rules
        CleanupAllRules();

        Debug.WriteLine("All bandwidth limits have been reset");
    }

    #endregion

    #region Persistence

    private void LoadRules()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                var rules = JsonSerializer.Deserialize<List<BandwidthRule>>(json);
                if (rules != null)
                {
                    foreach (var rule in rules)
                    {
                        _rules[rule.ProcessName.ToLowerInvariant()] = rule;
                    }
                }
            }
        }
        catch { }
    }

    private void SaveRules()
    {
        try
        {
            var dir = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_rules.Values.ToList(),
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json);
        }
        catch { }
    }

    #endregion

    #region Dispose

    public void QuickDispose()
    {
        StopEnforcement();
    }

    public void Dispose()
    {
        StopEnforcement();
        CleanupAllRules();
    }

    #endregion
}

public class BandwidthRule
{
    public string ProcessName { get; set; } = "";
    public long DownloadLimitKBps { get; set; }
    public long UploadLimitKBps { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public LimitMethod Method { get; set; } = LimitMethod.ProcessThrottle;
}

public enum LimitMethod
{
    ProcessThrottle,  // Suspend/Resume cycles
    Firewall,         // Complete block via firewall
    InterfaceLimit,   // Interface-level QoS
    QosPolicy         // Windows QoS Policy
}

public class ProcessThrottleState
{
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = "";
    public long DownloadLimitBps { get; set; }
    public long UploadLimitBps { get; set; }
    public bool IsActive { get; set; }
    public bool IsSuspended { get; set; }
    public DateTime LastSuspendTime { get; set; }
    public int SuspendDurationMs { get; set; }
}
