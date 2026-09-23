using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using NetX.Core.Helpers;

namespace NetX.Core.Network;

public class FirewallManager
{
    private static readonly Lazy<FirewallManager> _instance = new(() => new FirewallManager());
    public static FirewallManager Instance => _instance.Value;

    // Keyed by IP address for IP rules, or "port:{protocol}:{port}" for port rules.
    private readonly Dictionary<string, FirewallRule> _rules = new();
    private readonly object _lock = new();

    // The elevated app deletes firewall rules named in this file, so it lives in
    // the admin-only store rather than the user-writable profile.
    private const string StoreFileName = "firewall_blocks.json";

    // Only allow safe characters in rule names and IPs
    private static readonly Regex SafeNameRegex = new(@"^[a-zA-Z0-9._\-:/ ]+$", RegexOptions.Compiled);
    private static readonly Regex IpRegex = new(@"^[\d.:a-fA-F/]+$", RegexOptions.Compiled);

    public event EventHandler<FirewallRuleEventArgs>? RuleAdded;
    public event EventHandler<FirewallRuleEventArgs>? RuleRemoved;

    private static bool IsValidIp(string ip) => !string.IsNullOrEmpty(ip) && IpRegex.IsMatch(ip);
    private static string SanitizeName(string name) => SafeNameRegex.IsMatch(name) ? name : Regex.Replace(name, @"[^a-zA-Z0-9._\-]", "_");

    private static string PortKey(string protocol, int port) => $"port:{protocol}:{port}";

    private FirewallManager()
    {
        // Load persisted rules synchronously (fast, just JSON) so they are listed
        // and unblockable immediately after a restart, then verify them against
        // Windows Firewall in the background so stale rules drop off on their own.
        Load();
        _ = Task.Run(VerifyPersistedRules);
    }

    public bool BlockIP(string ipAddress, string? ruleName = null)
    {
        if (!IsValidIp(ipAddress)) return false;

        try
        {
            ruleName = SanitizeName(ruleName ?? $"NetX_Block_{ipAddress.Replace(".", "_").Replace(":", "_")}");

            // Block inbound
            var inboundResult = RunNetshCommand(
                $"advfirewall firewall add rule name=\"{ruleName}_In\" dir=in action=block remoteip={ipAddress}");

            // Block outbound
            var outboundResult = RunNetshCommand(
                $"advfirewall firewall add rule name=\"{ruleName}_Out\" dir=out action=block remoteip={ipAddress}");

            if (inboundResult || outboundResult)
            {
                var rule = new FirewallRule
                {
                    Name = ruleName,
                    IPAddress = ipAddress,
                    Action = FirewallAction.Block,
                    Direction = FirewallDirection.Both,
                    CreatedAt = DateTime.Now
                };

                lock (_lock)
                {
                    _rules[ipAddress] = rule;
                    Save();
                }

                RuleAdded?.Invoke(this, new FirewallRuleEventArgs(rule));
                return true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error blocking IP: {ex.Message}");
        }

        return false;
    }

    public bool AllowIP(string ipAddress, string? ruleName = null)
    {
        if (!IsValidIp(ipAddress)) return false;

        try
        {
            ruleName = SanitizeName(ruleName ?? $"NetX_Allow_{ipAddress.Replace(".", "_").Replace(":", "_")}");

            // First remove any existing block rules
            UnblockIP(ipAddress);

            // Add allow rule (usually not needed as Windows Firewall allows by default)
            var rule = new FirewallRule
            {
                Name = ruleName,
                IPAddress = ipAddress,
                Action = FirewallAction.Allow,
                Direction = FirewallDirection.Both,
                CreatedAt = DateTime.Now
            };

            lock (_lock)
            {
                _rules[ipAddress] = rule;
                Save();
            }

            RuleAdded?.Invoke(this, new FirewallRuleEventArgs(rule));
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error allowing IP: {ex.Message}");
        }

        return false;
    }

    public bool UnblockIP(string ipAddress)
    {
        if (!IsValidIp(ipAddress)) return false;

        try
        {
            // Use the name the rule was actually created with (e.g. rule-engine rules
            // use "NetX_Rule_{id}"), falling back to the default block name.
            FirewallRule? existing;
            lock (_lock) _rules.TryGetValue(ipAddress, out existing);

            var ruleName = SanitizeName(existing?.Name ?? $"NetX_Block_{ipAddress.Replace(".", "_").Replace(":", "_")}");

            // Remove inbound rule
            RunNetshCommand($"advfirewall firewall delete rule name=\"{ruleName}_In\"");

            // Remove outbound rule
            RunNetshCommand($"advfirewall firewall delete rule name=\"{ruleName}_Out\"");

            lock (_lock)
            {
                if (_rules.TryGetValue(ipAddress, out var rule))
                {
                    _rules.Remove(ipAddress);
                    Save();
                    RuleRemoved?.Invoke(this, new FirewallRuleEventArgs(rule));
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error unblocking IP: {ex.Message}");
        }

        return false;
    }

    public bool BlockPort(int port, string protocol = "TCP", string? ruleName = null)
    {
        if (port <= 0 || port > 65535) return false;
        // Only allow TCP/UDP protocol values
        if (protocol != "TCP" && protocol != "UDP") protocol = "TCP";

        try
        {
            ruleName = SanitizeName(ruleName ?? $"NetX_BlockPort_{protocol}_{port}");

            // Block inbound
            var inboundResult = RunNetshCommand(
                $"advfirewall firewall add rule name=\"{ruleName}_In\" dir=in action=block protocol={protocol} localport={port}");

            // Block outbound
            var outboundResult = RunNetshCommand(
                $"advfirewall firewall add rule name=\"{ruleName}_Out\" dir=out action=block protocol={protocol} remoteport={port}");

            if (inboundResult || outboundResult)
            {
                var rule = new FirewallRule
                {
                    Name = ruleName,
                    Port = port,
                    Protocol = protocol,
                    Action = FirewallAction.Block,
                    Direction = FirewallDirection.Both,
                    CreatedAt = DateTime.Now
                };

                lock (_lock)
                {
                    _rules[PortKey(protocol, port)] = rule;
                    Save();
                }

                RuleAdded?.Invoke(this, new FirewallRuleEventArgs(rule));
                return true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error blocking port: {ex.Message}");
        }

        return false;
    }

    public bool UnblockPort(int port, string protocol = "TCP")
    {
        if (port <= 0 || port > 65535) return false;
        if (protocol != "TCP" && protocol != "UDP") protocol = "TCP";

        try
        {
            FirewallRule? existing;
            lock (_lock) _rules.TryGetValue(PortKey(protocol, port), out existing);

            var ruleName = SanitizeName(existing?.Name ?? $"NetX_BlockPort_{protocol}_{port}");

            RunNetshCommand($"advfirewall firewall delete rule name=\"{ruleName}_In\"");
            RunNetshCommand($"advfirewall firewall delete rule name=\"{ruleName}_Out\"");

            lock (_lock)
            {
                if (_rules.TryGetValue(PortKey(protocol, port), out var rule))
                {
                    _rules.Remove(PortKey(protocol, port));
                    Save();
                    RuleRemoved?.Invoke(this, new FirewallRuleEventArgs(rule));
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error unblocking port: {ex.Message}");
        }

        return false;
    }

    public List<FirewallRule> GetNetXRules()
    {
        lock (_lock)
        {
            return _rules.Values.ToList();
        }
    }

    public bool IsIPBlocked(string ipAddress)
    {
        lock (_lock)
        {
            return _rules.TryGetValue(ipAddress, out var rule) && rule.Action == FirewallAction.Block;
        }
    }

    public void ClearAllNetXRules()
    {
        try
        {
            // Delete each tracked NetX rule individually
            lock (_lock)
            {
                foreach (var rule in _rules.Values.ToList())
                {
                    var name = SanitizeName(rule.Name);
                    RunNetshCommand($"advfirewall firewall delete rule name=\"{name}_In\"");
                    RunNetshCommand($"advfirewall firewall delete rule name=\"{name}_Out\"");
                }
                _rules.Clear();
                Save();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error clearing rules: {ex.Message}");
        }
    }

    #region Persistence

    private sealed class StoreFile
    {
        public int Version { get; set; } = 1;
        public List<FirewallRule> Rules { get; set; } = new();
    }

    private void Load()
    {
        try
        {
            var file = AdminOnlyStore.Load<StoreFile>(StoreFileName);
            if (file?.Rules == null) return;

            lock (_lock)
            {
                foreach (var rule in file.Rules)
                {
                    if (string.IsNullOrWhiteSpace(rule.Name)) continue;

                    if (!string.IsNullOrEmpty(rule.IPAddress) && IsValidIp(rule.IPAddress))
                        _rules[rule.IPAddress] = rule;
                    else if (rule.Port > 0)
                        _rules[PortKey(rule.Protocol, rule.Port)] = rule;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error loading firewall rules: {ex.Message}");
        }
    }

    /// <summary>Writes the current rules to disk. Caller must hold <see cref="_lock"/>.</summary>
    private void Save()
    {
        if (!AdminOnlyStore.Save(StoreFileName, new StoreFile { Rules = _rules.Values.ToList() }))
            Debug.WriteLine("Admin-only store unavailable — IP blocks are kept for this session only");
    }

    /// <summary>
    /// Checks each persisted rule still exists in Windows Firewall (via netsh
    /// exit codes — the output is localized, so we never parse it) and drops any
    /// the user removed outside the app. Runs once in the background at startup.
    /// </summary>
    private void VerifyPersistedRules()
    {
        try
        {
            List<KeyValuePair<string, FirewallRule>> snapshot;
            lock (_lock) snapshot = _rules.ToList();
            if (snapshot.Count == 0) return;

            var stale = new List<KeyValuePair<string, FirewallRule>>();
            foreach (var kvp in snapshot)
            {
                var name = SanitizeName(kvp.Value.Name);
                // Keep the rule if either half is still present; only drop when both
                // are *confirmed* missing (null = couldn't determine, so keep).
                var inExists = RuleExists($"{name}_In");
                var outExists = RuleExists($"{name}_Out");

                if (inExists == false && outExists == false)
                    stale.Add(kvp);
            }

            if (stale.Count == 0) return;

            var removed = new List<FirewallRule>();
            lock (_lock)
            {
                foreach (var kvp in stale)
                {
                    if (_rules.TryGetValue(kvp.Key, out var current) && ReferenceEquals(current, kvp.Value))
                    {
                        _rules.Remove(kvp.Key);
                        removed.Add(current);
                    }
                }
                if (removed.Count > 0) Save();
            }

            foreach (var rule in removed)
                RuleRemoved?.Invoke(this, new FirewallRuleEventArgs(rule));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error verifying firewall rules: {ex.Message}");
        }
    }

    /// <summary>
    /// True if the named rule exists, false if confirmed missing, null if netsh
    /// could not be run (so the caller keeps the rule rather than dropping it).
    /// </summary>
    private static bool? RuleExists(string ruleName)
    {
        var exit = RunNetshExit($"advfirewall firewall show rule name=\"{ruleName}\"");
        if (exit == null) return null;
        return exit == 0;
    }

    #endregion

    private bool RunNetshCommand(string arguments) => RunNetshExit(arguments) == 0;

    /// <summary>
    /// Runs netsh and returns its exit code, or null if the process could not run
    /// or timed out. The app runs elevated (see app.manifest), so netsh inherits
    /// administrator rights; the old Verb="runas" was invalid with
    /// UseShellExecute=false and has been removed.
    /// </summary>
    private static int? RunNetshExit(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return null;

            // Drain stdout so a large rule listing can't fill the pipe and deadlock.
            process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(); } catch { }
                return null;
            }
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Netsh command failed: {ex.Message}");
        }

        return null;
    }
}

public class FirewallRule
{
    public string Name { get; set; } = string.Empty;
    public string IPAddress { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Protocol { get; set; } = "TCP";
    public FirewallAction Action { get; set; }
    public FirewallDirection Direction { get; set; }
    public DateTime CreatedAt { get; set; }
}

public enum FirewallAction
{
    Allow,
    Block
}

public enum FirewallDirection
{
    Inbound,
    Outbound,
    Both
}

public class FirewallRuleEventArgs : EventArgs
{
    public FirewallRule Rule { get; }

    public FirewallRuleEventArgs(FirewallRule rule)
    {
        Rule = rule;
    }
}
