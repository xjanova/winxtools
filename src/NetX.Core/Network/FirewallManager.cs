using System.Diagnostics;
using System.Text.RegularExpressions;

namespace NetX.Core.Network;

public class FirewallManager
{
    private static readonly Lazy<FirewallManager> _instance = new(() => new FirewallManager());
    public static FirewallManager Instance => _instance.Value;

    private readonly Dictionary<string, FirewallRule> _rules = new();
    private readonly object _lock = new();

    // Only allow safe characters in rule names and IPs
    private static readonly Regex SafeNameRegex = new(@"^[a-zA-Z0-9._\-:/ ]+$", RegexOptions.Compiled);
    private static readonly Regex IpRegex = new(@"^[\d.:a-fA-F/]+$", RegexOptions.Compiled);

    public event EventHandler<FirewallRuleEventArgs>? RuleAdded;
    public event EventHandler<FirewallRuleEventArgs>? RuleRemoved;

    private static bool IsValidIp(string ip) => !string.IsNullOrEmpty(ip) && IpRegex.IsMatch(ip);
    private static string SanitizeName(string name) => SafeNameRegex.IsMatch(name) ? name : Regex.Replace(name, @"[^a-zA-Z0-9._\-]", "_");

    public bool BlockIP(string ipAddress, string? ruleName = null)
    {
        if (!IsValidIp(ipAddress)) return false;

        try
        {
            ruleName = SanitizeName(ruleName ?? $"NetX_Block_{ipAddress.Replace(".", "_")}");

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
            ruleName = SanitizeName(ruleName ?? $"NetX_Allow_{ipAddress.Replace(".", "_")}");

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
            var ruleName = $"NetX_Block_{ipAddress.Replace(".", "_")}";

            // Remove inbound rule
            RunNetshCommand($"advfirewall firewall delete rule name=\"{ruleName}_In\"");

            // Remove outbound rule
            RunNetshCommand($"advfirewall firewall delete rule name=\"{ruleName}_Out\"");

            lock (_lock)
            {
                if (_rules.TryGetValue(ipAddress, out var rule))
                {
                    _rules.Remove(ipAddress);
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

            return inboundResult || outboundResult;
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

        try
        {
            var ruleName = $"NetX_BlockPort_{protocol}_{port}";

            RunNetshCommand($"advfirewall firewall delete rule name=\"{ruleName}_In\"");
            RunNetshCommand($"advfirewall firewall delete rule name=\"{ruleName}_Out\"");

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
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error clearing rules: {ex.Message}");
        }
    }

    private bool RunNetshCommand(string arguments)
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
                CreateNoWindow = true,
                Verb = "runas"
            };

            using var process = Process.Start(psi);
            if (process != null)
            {
                process.WaitForExit(5000);
                return process.ExitCode == 0;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Netsh command failed: {ex.Message}");
        }

        return false;
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
