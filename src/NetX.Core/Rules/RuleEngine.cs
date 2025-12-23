using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetX.Core.Rules;

/// <summary>
/// Powerful rule engine for network automation with conditions, actions, and notifications
/// </summary>
public class RuleEngine
{
    private static readonly Lazy<RuleEngine> _instance = new(() => new RuleEngine());
    public static RuleEngine Instance => _instance.Value;

    private readonly List<NetworkRule> _rules = new();
    private readonly object _lock = new();
    private readonly string _rulesFilePath;
    private global::System.Timers.Timer? _evaluationTimer;

    public event EventHandler<RuleTriggeredEventArgs>? RuleTriggered;
    public event EventHandler<RuleActionExecutedEventArgs>? ActionExecuted;

    public RuleEngine()
    {
        _rulesFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NetX", "rules.json");

        EnsureDirectoryExists();
        LoadRules();
    }

    #region Rule Management

    public void AddRule(NetworkRule rule)
    {
        lock (_lock)
        {
            rule.Id = Guid.NewGuid().ToString();
            rule.CreatedAt = DateTime.Now;
            _rules.Add(rule);
            SaveRules();
        }
    }

    public void UpdateRule(NetworkRule rule)
    {
        lock (_lock)
        {
            var index = _rules.FindIndex(r => r.Id == rule.Id);
            if (index >= 0)
            {
                rule.UpdatedAt = DateTime.Now;
                _rules[index] = rule;
                SaveRules();
            }
        }
    }

    public void DeleteRule(string ruleId)
    {
        lock (_lock)
        {
            _rules.RemoveAll(r => r.Id == ruleId);
            SaveRules();
        }
    }

    public List<NetworkRule> GetAllRules()
    {
        lock (_lock)
        {
            return _rules.ToList();
        }
    }

    public NetworkRule? GetRuleById(string ruleId)
    {
        lock (_lock)
        {
            return _rules.FirstOrDefault(r => r.Id == ruleId);
        }
    }

    #endregion

    #region Rule Evaluation

    public void StartAutoEvaluation(int intervalMs = 5000)
    {
        _evaluationTimer?.Stop();
        _evaluationTimer = new global::System.Timers.Timer(intervalMs);
        _evaluationTimer.Elapsed += (s, e) => EvaluateAllRules();
        _evaluationTimer.Start();
    }

    public void StopAutoEvaluation()
    {
        _evaluationTimer?.Stop();
        _evaluationTimer?.Dispose();
        _evaluationTimer = null;
    }

    public void EvaluateAllRules()
    {
        List<NetworkRule> rulesToEvaluate;
        lock (_lock)
        {
            rulesToEvaluate = _rules.Where(r => r.IsEnabled).ToList();
        }

        foreach (var rule in rulesToEvaluate)
        {
            try
            {
                if (EvaluateConditions(rule))
                {
                    ExecuteActions(rule);
                    rule.LastTriggeredAt = DateTime.Now;
                    rule.TriggerCount++;
                    RuleTriggered?.Invoke(this, new RuleTriggeredEventArgs(rule));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error evaluating rule {rule.Name}: {ex.Message}");
            }
        }

        SaveRules();
    }

    private bool EvaluateConditions(NetworkRule rule)
    {
        if (rule.Conditions == null || rule.Conditions.Count == 0)
            return true;

        bool result = rule.ConditionLogic == ConditionLogic.And;

        foreach (var condition in rule.Conditions)
        {
            bool conditionMet = EvaluateCondition(condition);

            if (rule.ConditionLogic == ConditionLogic.And)
            {
                result = result && conditionMet;
                if (!result) break; // Short circuit
            }
            else // Or
            {
                result = result || conditionMet;
                if (result) break; // Short circuit
            }
        }

        return result;
    }

    private bool EvaluateCondition(RuleCondition condition)
    {
        return condition.Type switch
        {
            ConditionType.TimeRange => EvaluateTimeCondition(condition),
            ConditionType.DayOfWeek => EvaluateDayCondition(condition),
            ConditionType.ProcessRunning => EvaluateProcessCondition(condition),
            ConditionType.BandwidthExceeds => EvaluateBandwidthCondition(condition),
            ConditionType.ConnectionCount => EvaluateConnectionCountCondition(condition),
            ConditionType.IPConnected => EvaluateIPConnectedCondition(condition),
            ConditionType.PortInUse => EvaluatePortCondition(condition),
            _ => false
        };
    }

    private bool EvaluateTimeCondition(RuleCondition condition)
    {
        var now = DateTime.Now.TimeOfDay;
        if (TimeSpan.TryParse(condition.Value, out var startTime) &&
            TimeSpan.TryParse(condition.Value2, out var endTime))
        {
            if (startTime <= endTime)
                return now >= startTime && now <= endTime;
            else // Overnight range (e.g., 22:00 - 06:00)
                return now >= startTime || now <= endTime;
        }
        return false;
    }

    private bool EvaluateDayCondition(RuleCondition condition)
    {
        var today = DateTime.Now.DayOfWeek.ToString();
        var allowedDays = condition.Value?.Split(',') ?? Array.Empty<string>();
        return allowedDays.Any(d => d.Trim().Equals(today, StringComparison.OrdinalIgnoreCase));
    }

    private bool EvaluateProcessCondition(RuleCondition condition)
    {
        var processes = System.Diagnostics.Process.GetProcessesByName(condition.Value ?? "");
        return condition.Operator switch
        {
            ConditionOperator.Equals => processes.Length > 0,
            ConditionOperator.NotEquals => processes.Length == 0,
            _ => processes.Length > 0
        };
    }

    private bool EvaluateBandwidthCondition(RuleCondition condition)
    {
        // This would integrate with NetworkMonitor
        if (long.TryParse(condition.Value, out var threshold))
        {
            // TODO: Get actual bandwidth from NetworkMonitor
            return false;
        }
        return false;
    }

    private bool EvaluateConnectionCountCondition(RuleCondition condition)
    {
        var connections = Network.ConnectionMonitor.Instance.GetActiveConnections();
        int count = connections.Count;

        if (int.TryParse(condition.Value, out var threshold))
        {
            return condition.Operator switch
            {
                ConditionOperator.Equals => count == threshold,
                ConditionOperator.NotEquals => count != threshold,
                ConditionOperator.GreaterThan => count > threshold,
                ConditionOperator.LessThan => count < threshold,
                ConditionOperator.GreaterOrEqual => count >= threshold,
                ConditionOperator.LessOrEqual => count <= threshold,
                _ => false
            };
        }
        return false;
    }

    private bool EvaluateIPConnectedCondition(RuleCondition condition)
    {
        var connections = Network.ConnectionMonitor.Instance.GetActiveConnections();
        return connections.Any(c => c.RemoteAddress == condition.Value);
    }

    private bool EvaluatePortCondition(RuleCondition condition)
    {
        if (int.TryParse(condition.Value, out var port))
        {
            var connections = Network.ConnectionMonitor.Instance.GetActiveConnections();
            return connections.Any(c => c.LocalPort == port || c.RemotePort == port);
        }
        return false;
    }

    #endregion

    #region Action Execution

    private void ExecuteActions(NetworkRule rule)
    {
        if (rule.Actions == null) return;

        foreach (var action in rule.Actions)
        {
            try
            {
                ExecuteAction(action, rule);
                ActionExecuted?.Invoke(this, new RuleActionExecutedEventArgs(rule, action, true));
            }
            catch (Exception ex)
            {
                ActionExecuted?.Invoke(this, new RuleActionExecutedEventArgs(rule, action, false, ex.Message));
            }
        }
    }

    private void ExecuteAction(RuleAction action, NetworkRule rule)
    {
        switch (action.Type)
        {
            case ActionType.BlockIP:
                Network.FirewallManager.Instance.BlockIP(action.Target ?? "", $"NetX_Rule_{rule.Id}");
                break;

            case ActionType.UnblockIP:
                Network.FirewallManager.Instance.UnblockIP(action.Target ?? "");
                break;

            case ActionType.BlockPort:
                if (int.TryParse(action.Target, out var port))
                    Network.FirewallManager.Instance.BlockPort(port, action.Parameters?.GetValueOrDefault("protocol", "TCP") ?? "TCP");
                break;

            case ActionType.LimitBandwidth:
                // TODO: Integrate with bandwidth limiter
                break;

            case ActionType.WriteToFile:
                WriteToLogFile(action, rule);
                break;

            case ActionType.ShowNotification:
                // Notification will be handled by the UI layer
                break;

            case ActionType.SendWebhook:
                _ = SendWebhookAsync(action, rule);
                break;

            case ActionType.ExecuteCommand:
                ExecuteCommand(action);
                break;

            case ActionType.SendLineNotify:
                _ = SendLineNotifyAsync(action, rule);
                break;

            case ActionType.SendSMS:
                // SMS requires external service integration
                break;
        }
    }

    private void WriteToLogFile(RuleAction action, NetworkRule rule)
    {
        var logPath = action.Target ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NetX", "rule_log.txt");

        var message = action.Parameters?.GetValueOrDefault("message", "") ?? "";
        message = ReplaceVariables(message, rule);

        var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Rule: {rule.Name} | {message}{Environment.NewLine}";
        File.AppendAllText(logPath, logEntry);
    }

    private async Task SendWebhookAsync(RuleAction action, NetworkRule rule)
    {
        if (string.IsNullOrEmpty(action.Target)) return;

        using var client = new HttpClient();
        var message = action.Parameters?.GetValueOrDefault("message", $"Rule triggered: {rule.Name}") ?? "";
        message = ReplaceVariables(message, rule);

        var content = new StringContent(
            JsonSerializer.Serialize(new { text = message, rule = rule.Name, timestamp = DateTime.Now }),
            System.Text.Encoding.UTF8,
            "application/json");

        await client.PostAsync(action.Target, content);
    }

    private async Task SendLineNotifyAsync(RuleAction action, NetworkRule rule)
    {
        var token = action.Parameters?.GetValueOrDefault("token", "") ?? "";
        if (string.IsNullOrEmpty(token)) return;

        var message = action.Parameters?.GetValueOrDefault("message", $"[NetX] Rule triggered: {rule.Name}") ?? "";
        message = ReplaceVariables(message, rule);

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("message", message)
        });

        await client.PostAsync("https://notify-api.line.me/api/notify", content);
    }

    private void ExecuteCommand(RuleAction action)
    {
        if (string.IsNullOrEmpty(action.Target)) return;

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c {action.Target}",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        System.Diagnostics.Process.Start(psi);
    }

    private string ReplaceVariables(string template, NetworkRule rule)
    {
        return template
            .Replace("{rule_name}", rule.Name)
            .Replace("{rule_id}", rule.Id)
            .Replace("{datetime}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
            .Replace("{date}", DateTime.Now.ToString("yyyy-MM-dd"))
            .Replace("{time}", DateTime.Now.ToString("HH:mm:ss"))
            .Replace("{trigger_count}", rule.TriggerCount.ToString());
    }

    #endregion

    #region Persistence

    private void EnsureDirectoryExists()
    {
        var dir = Path.GetDirectoryName(_rulesFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }

    private void LoadRules()
    {
        try
        {
            if (File.Exists(_rulesFilePath))
            {
                var json = File.ReadAllText(_rulesFilePath);
                var rules = JsonSerializer.Deserialize<List<NetworkRule>>(json);
                if (rules != null)
                {
                    lock (_lock)
                    {
                        _rules.Clear();
                        _rules.AddRange(rules);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading rules: {ex.Message}");
        }
    }

    private void SaveRules()
    {
        try
        {
            EnsureDirectoryExists();
            var json = JsonSerializer.Serialize(_rules, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_rulesFilePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error saving rules: {ex.Message}");
        }
    }

    #endregion
}

#region Models

public class NetworkRule
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public int Priority { get; set; } = 0;

    public ConditionLogic ConditionLogic { get; set; } = ConditionLogic.And;
    public List<RuleCondition> Conditions { get; set; } = new();
    public List<RuleAction> Actions { get; set; } = new();

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? LastTriggeredAt { get; set; }
    public int TriggerCount { get; set; }

    // For specific app targeting
    public string? TargetProcessName { get; set; }
    public int? TargetProcessId { get; set; }
}

public class RuleCondition
{
    public ConditionType Type { get; set; }
    public ConditionOperator Operator { get; set; } = ConditionOperator.Equals;
    public string? Value { get; set; }
    public string? Value2 { get; set; } // For range conditions
    public Dictionary<string, string>? Parameters { get; set; }
}

public class RuleAction
{
    public ActionType Type { get; set; }
    public string? Target { get; set; }
    public Dictionary<string, string>? Parameters { get; set; }
}

public enum ConditionLogic
{
    And,
    Or
}

public enum ConditionType
{
    TimeRange,          // Between specific times
    DayOfWeek,          // On specific days
    ProcessRunning,     // When process is running
    BandwidthExceeds,   // When bandwidth exceeds threshold
    ConnectionCount,    // Number of connections
    IPConnected,        // Specific IP is connected
    PortInUse,          // Specific port is in use
    Always              // Always true (immediate trigger)
}

public enum ConditionOperator
{
    Equals,
    NotEquals,
    GreaterThan,
    LessThan,
    GreaterOrEqual,
    LessOrEqual,
    Contains,
    StartsWith,
    EndsWith
}

public enum ActionType
{
    BlockIP,
    UnblockIP,
    BlockPort,
    UnblockPort,
    LimitBandwidth,
    UnlimitBandwidth,
    WriteToFile,
    ShowNotification,
    SendWebhook,
    SendLineNotify,
    SendSMS,
    ExecuteCommand,
    KillProcess
}

#endregion

#region Events

public class RuleTriggeredEventArgs : EventArgs
{
    public NetworkRule Rule { get; }
    public DateTime TriggeredAt { get; } = DateTime.Now;

    public RuleTriggeredEventArgs(NetworkRule rule)
    {
        Rule = rule;
    }
}

public class RuleActionExecutedEventArgs : EventArgs
{
    public NetworkRule Rule { get; }
    public RuleAction Action { get; }
    public bool Success { get; }
    public string? ErrorMessage { get; }

    public RuleActionExecutedEventArgs(NetworkRule rule, RuleAction action, bool success, string? errorMessage = null)
    {
        Rule = rule;
        Action = action;
        Success = success;
        ErrorMessage = errorMessage;
    }
}

#endregion
