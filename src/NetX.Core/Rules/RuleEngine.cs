using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace NetX.Core.Rules;

/// <summary>
/// Rule engine for network automation. Every few seconds it checks each enabled
/// rule and, when the rule's conditions become true, runs its actions once.
/// Rules make this elevated app block, kill and limit, so they are stored where
/// only Administrators and SYSTEM can change them (see <see cref="RuleStorage"/>).
/// </summary>
public class RuleEngine
{
    private static readonly Lazy<RuleEngine> _instance = new(() => new RuleEngine());
    public static RuleEngine Instance => _instance.Value;

    /// <summary>How often enabled rules are checked.</summary>
    public static readonly TimeSpan EvaluationInterval = TimeSpan.FromSeconds(5);

    private const int MaxRules = RuleValidator.MaxRules;
    private const int MaxActivityEntries = 200;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly HttpClient WebhookClient = CreateWebhookClient();

    private readonly List<NetworkRule> _rules = new();
    private readonly object _lock = new();
    // Runtime-only, never saved: edge detection per rule and the rules whose
    // actions are running right now.
    private readonly Dictionary<string, RuleRuntimeState> _states = new();
    private readonly HashSet<string> _running = new();
    private readonly LinkedList<RuleActivityEntry> _activity = new(); // newest first
    private readonly object _activityLock = new();
    private readonly NetworkSpeedSampler _speedSampler = new();
    private Timer? _timer;
    private int _evaluating;
    private long _activitySequence;
    private bool _saveErrorReported;
    private RuleEngineState _reportedState = RuleEngineState.Stopped;

    public event EventHandler<RuleTriggeredEventArgs>? RuleTriggered;
    /// <summary>Something happened (rule fired, action result, notice). Raised on a background thread.</summary>
    public event EventHandler<RuleActivityEventArgs>? ActivityLogged;
    /// <summary>A "Show notification" action ran. Raised on a background thread.</summary>
    public event EventHandler<RuleNotificationEventArgs>? NotificationRequested;
    /// <summary>Rules or their run history changed. Raised on any thread.</summary>
    public event EventHandler? RulesChanged;
    public event EventHandler? StateChanged;

    private RuleEngine()
    {
        LoadRules();
    }

    /// <summary>Why rules cannot be used, or null when storage is fine.</summary>
    public RuleStorageError? StorageError { get; private set; }

    /// <summary>Protected folder that holds rules.json and the rule log files.</summary>
    public string StorageFolder => RuleStorage.RootDirectory;

    public RuleEngineState State
    {
        get
        {
            if (StorageError != null) return RuleEngineState.StorageUnavailable;
            bool started;
            lock (_lock)
            {
                started = _timer != null;
            }
            if (!started) return RuleEngineState.Stopped;
            return HasProAccess() ? RuleEngineState.Running : RuleEngineState.PausedNoPro;
        }
    }

    #region Rule Management

    /// <summary>
    /// Adds a new rule. Throws <see cref="RuleValidationException"/> when the
    /// rule is not valid and <see cref="RuleStorageException"/> when it cannot be saved.
    /// </summary>
    public void AddRule(NetworkRule rule)
    {
        var copy = PrepareForStorage(rule);
        lock (_lock)
        {
            EnsureStorageAvailable();
            if (_rules.Count >= MaxRules)
                throw new RuleValidationException(new[] { new RuleValidationError(RuleProblem.TooManyRules) });

            copy.Id = Guid.NewGuid().ToString();
            copy.CreatedAt = DateTime.Now;
            copy.UpdatedAt = null;
            copy.LastTriggeredAt = null;
            copy.TriggerCount = 0;
            copy.LastRunSucceeded = 0;
            copy.LastRunFailed = 0;
            _rules.Add(copy);
            try
            {
                SaveRulesLocked();
            }
            catch
            {
                _rules.Remove(copy);
                throw;
            }
        }
        RulesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Saves the settings of an edited rule. Run history (times fired, last
    /// result) is kept from the stored rule, so a copy edited in a dialog cannot
    /// overwrite a run that happened meanwhile.
    /// </summary>
    public void UpdateRule(NetworkRule rule)
    {
        var edited = PrepareForStorage(rule);
        lock (_lock)
        {
            EnsureStorageAvailable();
            var stored = _rules.FirstOrDefault(r => r.Id == rule.Id);
            if (stored == null) return;

            var backup = stored.Clone();
            stored.CopySettingsFrom(edited);
            stored.UpdatedAt = DateTime.Now;
            try
            {
                SaveRulesLocked();
            }
            catch
            {
                stored.CopySettingsFrom(backup);
                stored.UpdatedAt = backup.UpdatedAt;
                throw;
            }

            // An edited rule starts fresh: conditions that are true now count as newly true.
            _states.Remove(stored.Id);
        }
        RulesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Turns a rule on or off. A rule is checked before it is turned on.</summary>
    public void SetRuleEnabled(string ruleId, bool enabled)
    {
        lock (_lock)
        {
            EnsureStorageAvailable();
            var stored = _rules.FirstOrDefault(r => r.Id == ruleId);
            if (stored == null || stored.IsEnabled == enabled) return;

            if (enabled)
            {
                var errors = RuleValidator.Validate(stored);
                if (errors.Count > 0) throw new RuleValidationException(errors);
            }

            stored.IsEnabled = enabled;
            try
            {
                SaveRulesLocked();
            }
            catch
            {
                stored.IsEnabled = !enabled;
                throw;
            }

            // Turning a rule on arms it: conditions that are already true count as new.
            _states.Remove(ruleId);
        }
        RulesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void DeleteRule(string ruleId)
    {
        lock (_lock)
        {
            EnsureStorageAvailable();
            var index = _rules.FindIndex(r => r.Id == ruleId);
            if (index < 0) return;

            var removed = _rules[index];
            _rules.RemoveAt(index);
            try
            {
                SaveRulesLocked();
            }
            catch
            {
                _rules.Insert(index, removed);
                throw;
            }
            _states.Remove(ruleId);
        }
        RulesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Copies of all rules; changing them does not change the engine.</summary>
    public List<NetworkRule> GetAllRules()
    {
        lock (_lock)
        {
            return _rules.Select(r => r.Clone()).ToList();
        }
    }

    /// <summary>A copy of the rule, or null if it no longer exists.</summary>
    public NetworkRule? GetRuleById(string ruleId)
    {
        lock (_lock)
        {
            return _rules.FirstOrDefault(r => r.Id == ruleId)?.Clone();
        }
    }

    /// <summary>Recent activity of this session, newest first.</summary>
    public List<RuleActivityEntry> GetRecentActivity()
    {
        lock (_activityLock)
        {
            return _activity.ToList();
        }
    }

    private static NetworkRule PrepareForStorage(NetworkRule rule)
    {
        var copy = rule.Clone();
        copy.Name = (copy.Name ?? string.Empty).Trim();
        copy.Description = (copy.Description ?? string.Empty).Trim();

        var errors = RuleValidator.Validate(copy);
        if (errors.Count > 0) throw new RuleValidationException(errors);
        return copy;
    }

    private void EnsureStorageAvailable()
    {
        if (StorageError is { } error)
            throw new RuleStorageException(error, RuleStorage.RootDirectory);
    }

    #endregion

    #region Rule Evaluation

    /// <summary>
    /// Starts checking enabled rules every <see cref="EvaluationInterval"/>.
    /// Call once at app startup; calling it again does nothing.
    /// </summary>
    public void Start()
    {
        lock (_lock)
        {
            _timer ??= new Timer(OnTimerTick, null, EvaluationInterval, EvaluationInterval);
        }
        RaiseStateChangedIfNeeded();
    }

    /// <summary>Stops checking rules. Actions that are already running finish.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
            _states.Clear();
        }
        RaiseStateChangedIfNeeded();
    }

    private void OnTimerTick(object? state)
    {
        // Skip this tick if the previous one is still reading system state.
        if (Interlocked.Exchange(ref _evaluating, 1) == 1) return;
        try
        {
            EvaluateAllRules();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Rule evaluation failed: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _evaluating, 0);
        }
        RaiseStateChangedIfNeeded();
    }

    private void EvaluateAllRules()
    {
        if (StorageError != null) return;

        // Automation Rules is a Pro feature. Without Pro access rules pause, and
        // when access returns they start fresh.
        bool hasPro = HasProAccess();
        List<NetworkRule> enabled;
        lock (_lock)
        {
            if (!hasPro)
            {
                _states.Clear();
                return;
            }
            enabled = _rules.Where(r => r.IsEnabled).Select(r => r.Clone()).ToList();
        }
        if (enabled.Count == 0) return;

        var context = new EvaluationContext(
            enabled.Any(UsesNetworkSpeed) ? _speedSampler.Sample() : null);
        var now = DateTime.Now;

        foreach (var rule in enabled)
        {
            bool conditionsMet;
            Exception? error = null;
            try
            {
                conditionsMet = EvaluateConditions(rule, context);
            }
            catch (Exception ex)
            {
                conditionsMet = false;
                error = ex;
            }

            NetworkRule? toRun = null;
            RuleStorageException? saveError = null;
            bool reportError;
            lock (_lock)
            {
                var stored = _rules.FirstOrDefault(r => r.Id == rule.Id);
                if (stored == null || !stored.IsEnabled) continue; // deleted or turned off meanwhile

                var state = GetStateLocked(rule.Id);
                reportError = error != null && !state.ErrorReported;
                state.ErrorReported = error != null;

                if (!conditionsMet)
                {
                    // Re-arm: the next time the conditions become true is a new event.
                    state.AlreadyFired = false;
                }
                else if (!state.AlreadyFired && !_running.Contains(rule.Id) && !IsInCooldown(stored, now))
                {
                    // Runs once per false -> true change, never every tick while the
                    // conditions stay true. During the cooldown the rule stays armed
                    // and runs when the cooldown ends, if the conditions still hold.
                    state.AlreadyFired = true;
                    _running.Add(rule.Id);
                    stored.LastTriggeredAt = now;
                    stored.TriggerCount++;
                    stored.LastRunSucceeded = 0;
                    stored.LastRunFailed = 0;
                    toRun = stored.Clone();
                    saveError = TrySaveLocked();
                }
            }

            if (reportError)
            {
                LogActivity(new RuleActivityEntry
                {
                    Kind = RuleActivityKind.Warning,
                    Code = RuleActivityCode.ConditionError,
                    RuleId = rule.Id,
                    RuleName = rule.Name,
                    Detail = error!.Message
                });
            }
            if (saveError != null) ReportSaveError(saveError);
            if (toRun != null) _ = Task.Run(() => RunRuleAsync(toRun));
        }
    }

    private RuleRuntimeState GetStateLocked(string ruleId)
    {
        if (!_states.TryGetValue(ruleId, out var state))
            _states[ruleId] = state = new RuleRuntimeState();
        return state;
    }

    private static bool IsInCooldown(NetworkRule rule, DateTime now) =>
        rule.LastTriggeredAt is { } last && last <= now &&
        now - last < TimeSpan.FromSeconds(rule.CooldownSeconds);

    private static bool UsesNetworkSpeed(NetworkRule rule) =>
        rule.Conditions.Any(c => c.Type == ConditionType.BandwidthExceeds);

    private static bool HasProAccess()
    {
        try
        {
            return NetX.Core.System.TrialService.Instance.HasProAccess;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Pro check failed: {ex.Message}");
            return false;
        }
    }

    private void RaiseStateChangedIfNeeded()
    {
        var state = State;
        bool changed;
        lock (_lock)
        {
            changed = state != _reportedState;
            _reportedState = state;
        }
        if (changed) StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool EvaluateConditions(NetworkRule rule, EvaluationContext context)
    {
        // No conditions behaves like "Always": the rule runs once each time it is
        // armed (app start, turned on, or edited).
        if (rule.Conditions.Count == 0) return true;

        return rule.ConditionLogic == ConditionLogic.Or
            ? rule.Conditions.Any(c => EvaluateCondition(c, rule, context))
            : rule.Conditions.All(c => EvaluateCondition(c, rule, context));
    }

    private static bool EvaluateCondition(RuleCondition condition, NetworkRule rule, EvaluationContext context)
    {
        return condition.Type switch
        {
            ConditionType.TimeRange => EvaluateTimeCondition(condition),
            ConditionType.DayOfWeek => EvaluateDayCondition(condition),
            ConditionType.ProcessRunning => EvaluateProcessCondition(condition, rule, context),
            ConditionType.BandwidthExceeds => EvaluateBandwidthCondition(condition, context),
            ConditionType.ConnectionCount => EvaluateConnectionCountCondition(condition, context),
            ConditionType.IPConnected => EvaluateIPConnectedCondition(condition, context),
            ConditionType.PortInUse => EvaluatePortCondition(condition, context),
            // Always true, so with edge triggering it runs once when the rule is
            // armed: at app start, when turned on, or after an edit.
            ConditionType.Always => true,
            _ => false
        };
    }

    private static bool EvaluateTimeCondition(RuleCondition condition)
    {
        if (!RuleValidator.TryParseTime(condition.Value, out var start) ||
            !RuleValidator.TryParseTime(condition.Value2, out var end) ||
            start == end)
            return false;

        var now = DateTime.Now.TimeOfDay;
        return start < end
            ? now >= start && now < end
            : now >= start || now < end; // Overnight range (e.g., 22:00 - 06:00)
    }

    private static bool EvaluateDayCondition(RuleCondition condition) =>
        RuleValidator.TryParseDays(condition.Value, out var days) && days.Contains(DateTime.Now.DayOfWeek);

    private static bool EvaluateProcessCondition(RuleCondition condition, NetworkRule rule, EvaluationContext context)
    {
        // A blank app name means the rule's Target app.
        var name = RuleValidator.ResolveProcessName(condition.Value, rule);
        if (!RuleValidator.IsValidProcessName(name)) return false;

        bool running = context.RunningProcessNames.Contains(name);
        return condition.Operator == ConditionOperator.NotEquals ? !running : running;
    }

    private static bool EvaluateBandwidthCondition(RuleCondition condition, EvaluationContext context)
    {
        // Threshold is KB/s of download + upload over all adapters.
        if (!RuleValidator.TryParseCount(condition.Value, RuleValidator.MaxSpeedKBps, out var thresholdKBps) ||
            context.TotalBytesPerSecond is not { } bytesPerSecond)
            return false;

        return Compare(bytesPerSecond / 1024.0, thresholdKBps, condition.Operator);
    }

    private static bool EvaluateConnectionCountCondition(RuleCondition condition, EvaluationContext context) =>
        RuleValidator.TryParseCount(condition.Value, RuleValidator.MaxConnectionCount, out var threshold) &&
        Compare(context.ConnectionCount, threshold, condition.Operator);

    private static bool EvaluateIPConnectedCondition(RuleCondition condition, EvaluationContext context)
    {
        if (!RuleValidator.TryParseIp(condition.Value, out var address)) return false;

        bool connected = context.IsConnectedTo(address);
        return condition.Operator == ConditionOperator.NotEquals ? !connected : connected;
    }

    private static bool EvaluatePortCondition(RuleCondition condition, EvaluationContext context)
    {
        if (!RuleValidator.TryParsePort(condition.Value, out var port)) return false;

        bool inUse = context.IsPortInUse(port);
        return condition.Operator == ConditionOperator.NotEquals ? !inUse : inUse;
    }

    private static bool Compare(double actual, double threshold, ConditionOperator op) => op switch
    {
        ConditionOperator.Equals => actual == threshold,
        ConditionOperator.NotEquals => actual != threshold,
        ConditionOperator.GreaterThan => actual > threshold,
        ConditionOperator.LessThan => actual < threshold,
        ConditionOperator.GreaterOrEqual => actual >= threshold,
        ConditionOperator.LessOrEqual => actual <= threshold,
        _ => false
    };

    #endregion

    #region Action Execution

    private async Task RunRuleAsync(NetworkRule rule)
    {
        int succeeded = 0, failed = 0;
        try
        {
            LogActivity(new RuleActivityEntry
            {
                Kind = RuleActivityKind.Fired,
                RuleId = rule.Id,
                RuleName = rule.Name
            });
            RuleTriggered?.Invoke(this, new RuleTriggeredEventArgs(rule));
            RulesChanged?.Invoke(this, EventArgs.Empty);

            foreach (var original in rule.Actions)
            {
                var action = ResolveDefaults(original, rule);
                ActionOutcome outcome;
                try
                {
                    outcome = await ExecuteActionAsync(action, rule);
                }
                catch (Exception ex)
                {
                    outcome = ActionOutcome.Failed(RuleActivityCode.UnexpectedError, ex.Message);
                }

                if (outcome.Kind == RuleActivityKind.Failed) failed++;
                else succeeded++;
                LogActivity(DescribeAction(rule, action, outcome));
            }
        }
        finally
        {
            RuleStorageException? saveError = null;
            lock (_lock)
            {
                _running.Remove(rule.Id);
                var stored = _rules.FirstOrDefault(r => r.Id == rule.Id);
                if (stored != null)
                {
                    stored.LastRunSucceeded = succeeded;
                    stored.LastRunFailed = failed;
                    saveError = TrySaveLocked();
                }
            }
            if (saveError != null) ReportSaveError(saveError);
            RulesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Copy of the action with blank app fields filled from the rule's Target
    /// app ("*" = whole PC for speed limits when the rule has no target app).
    /// </summary>
    private static RuleAction ResolveDefaults(RuleAction action, NetworkRule rule)
    {
        var resolved = action.Clone();
        switch (resolved.Type)
        {
            case ActionType.KillProcess:
                resolved.Target = RuleValidator.ResolveProcessName(resolved.Target, rule);
                break;

            case ActionType.LimitBandwidth:
                resolved.Parameters ??= new Dictionary<string, string>();
                resolved.Parameters["process"] = RuleValidator.ResolveAppOrAll(
                    resolved.Parameters.GetValueOrDefault("process"), rule);
                break;

            case ActionType.UnlimitBandwidth:
                resolved.Target = RuleValidator.ResolveAppOrAll(resolved.Target, rule);
                break;
        }
        return resolved;
    }

    private async Task<ActionOutcome> ExecuteActionAsync(RuleAction action, NetworkRule rule)
    {
        // Stored values are never trusted: every action is checked again right
        // before this elevated process acts on it.
        if (RuleValidator.ValidateAction(action, rule) is { } problem)
        {
            return ActionOutcome.Failed(problem switch
            {
                RuleProblem.RemovedAction => RuleActivityCode.ActionRemoved,
                RuleProblem.ProtectedProcess => RuleActivityCode.ProtectedProcess,
                _ => RuleActivityCode.InvalidSettings
            });
        }

        var outcome = ActionOutcome.Done;
        switch (action.Type)
        {
            case ActionType.BlockIP:
                outcome = BlockIp(action.Target);
                break;

            case ActionType.UnblockIP:
                outcome = UnblockIp(action.Target);
                break;

            case ActionType.UnblockPort:
            case ActionType.BlockPort:
                outcome = ChangePortBlock(action);
                break;

            case ActionType.LimitBandwidth:
                // Target is the limit in KB/s; "process" = app name, "*" = whole PC.
                if (int.TryParse(action.Target, out var limitKBps) && limitKBps >= 0)
                {
                    var processName = action.Parameters?.GetValueOrDefault("process", "*") ?? "*";
                    long bps = limitKBps * 1024L;
                    var limited = processName == "*"
                        ? await Network.BandwidthLimiter.Instance.SetGlobalLimitAsync(bps, bps).ConfigureAwait(false)
                        : await Network.BandwidthLimiter.Instance.SetAppLimitAsync(processName, bps, bps).ConfigureAwait(false);
                    outcome = limited.Success ? ActionOutcome.Done : ActionOutcome.Failed(RuleActivityCode.UnexpectedError, limited.Message);
                }
                else
                {
                    outcome = ActionOutcome.Failed(RuleActivityCode.InvalidSettings);
                }
                break;

            case ActionType.UnlimitBandwidth:
                var targetProcess = action.Target ?? "*";
                var unlimited = targetProcess == "*"
                    ? await Network.BandwidthLimiter.Instance.RemoveGlobalLimitAsync().ConfigureAwait(false)
                    : await Network.BandwidthLimiter.Instance.SetAppLimitAsync(targetProcess, Network.RateLimit.Unlimited, Network.RateLimit.Unlimited).ConfigureAwait(false);
                outcome = unlimited.Success ? ActionOutcome.Done : ActionOutcome.Failed(RuleActivityCode.UnexpectedError, unlimited.Message);
                break;

            case ActionType.SendEmail:
            case ActionType.SendSMS:
            case ActionType.SendLineNotify:
            case ActionType.ExecuteCommand:
                // Removed: no working backend (LINE Notify shut down on 31 Mar 2025,
                // SMS/email were never configurable) or unsafe (elevated cmd.exe run
                // from a data file). Loading drops them; this is only a last guard.
                outcome = ActionOutcome.Failed(RuleActivityCode.ActionRemoved);
                break;

            case ActionType.KillProcess:
                outcome = KillProcesses(action.Target);
                break;

            case ActionType.WriteToFile:
                outcome = WriteToLogFile(action, rule);
                break;

            case ActionType.ShowNotification:
                outcome = ShowNotification(action, rule);
                break;

            case ActionType.SendWebhook:
                outcome = await SendWebhookAsync(action, rule);
                break;

            default:
                outcome = ActionOutcome.Failed(RuleActivityCode.InvalidSettings);
                break;
        }

        return outcome;
    }

    private static ActionOutcome BlockIp(string? target)
    {
        RuleValidator.TryNormalizeIpTarget(target, out var address);
        var firewall = Network.FirewallManager.Instance;
        if (firewall.IsIPBlocked(address))
            return ActionOutcome.Skipped(RuleActivityCode.AlreadyBlocked);

        // Default rule name (NetX_Block_<ip>) so "Unblock IP" and the Connections
        // page can remove the block again.
        return firewall.BlockIP(address)
            ? ActionOutcome.Done
            : ActionOutcome.Failed(RuleActivityCode.FirewallFailed);
    }

    private static ActionOutcome UnblockIp(string? target)
    {
        RuleValidator.TryNormalizeIpTarget(target, out var address);
        return Network.FirewallManager.Instance.UnblockIP(address)
            ? ActionOutcome.Done
            : ActionOutcome.Failed(RuleActivityCode.FirewallFailed);
    }

    private static ActionOutcome ChangePortBlock(RuleAction action)
    {
        RuleValidator.TryParsePort(action.Target, out var port);
        // Validated to TCP or UDP: the value ends up inside a netsh argument.
        var protocol = RuleValidator.NormalizeProtocol(action.Parameters?.GetValueOrDefault("protocol")) ?? "TCP";
        var firewall = Network.FirewallManager.Instance;
        bool ok = action.Type == ActionType.BlockPort
            ? firewall.BlockPort(port, protocol)
            : firewall.UnblockPort(port, protocol);
        return ok ? ActionOutcome.Done : ActionOutcome.Failed(RuleActivityCode.FirewallFailed);
    }

    private static ActionOutcome KillProcesses(string? target)
    {
        // Protected Windows processes and WinXTools itself were refused by validation.
        var name = RuleValidator.NormalizeProcessName(target);
        var processes = Process.GetProcessesByName(name);
        try
        {
            if (processes.Length == 0)
                return ActionOutcome.Skipped(RuleActivityCode.NotRunning);

            int killed = 0;
            foreach (var process in processes)
            {
                if (process.Id == Environment.ProcessId) continue;
                try
                {
                    process.Kill();
                    killed++;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Kill failed for {name} ({process.Id}): {ex.Message}");
                }
            }

            return killed > 0
                ? ActionOutcome.Succeeded(killed.ToString(CultureInfo.InvariantCulture))
                : ActionOutcome.Failed(RuleActivityCode.KillFailed);
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private static ActionOutcome WriteToLogFile(RuleAction action, NetworkRule rule)
    {
        // Only a plain file name is accepted, and it always lands in the
        // protected log folder, so a rule can never write anywhere else.
        var fileName = RuleValidator.NormalizeLogFileName(action.Target);
        if (fileName == null) return ActionOutcome.Failed(RuleActivityCode.InvalidSettings);

        try
        {
            var message = ReplaceVariables(action.Parameters?.GetValueOrDefault("message") ?? string.Empty, rule);
            var line = $"[{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}] " +
                       $"Rule: {OneLine(rule.Name)} | {OneLine(message)}{Environment.NewLine}";
            var path = RuleStorage.AppendToLog(fileName, line);
            return ActionOutcome.Succeeded(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RuleStorageException)
        {
            return ActionOutcome.Failed(RuleActivityCode.WriteFailed, ex.Message);
        }
    }

    private ActionOutcome ShowNotification(RuleAction action, NetworkRule rule)
    {
        // The UI shows it (Rules page activity log + a small toast); a timer
        // thread must never open a modal dialog.
        var message = ReplaceVariables(action.Parameters?.GetValueOrDefault("message") ?? string.Empty, rule).Trim();
        NotificationRequested?.Invoke(this, new RuleNotificationEventArgs(rule.Name, message));
        return ActionOutcome.Succeeded(message);
    }

    private static async Task<ActionOutcome> SendWebhookAsync(RuleAction action, NetworkRule rule)
    {
        var message = action.Parameters?.GetValueOrDefault("message");
        if (string.IsNullOrWhiteSpace(message)) message = "Rule triggered: {rule_name}";
        message = ReplaceVariables(message, rule);

        using var content = new StringContent(
            JsonSerializer.Serialize(new { text = message, rule = rule.Name, timestamp = DateTime.Now }),
            Encoding.UTF8,
            "application/json");

        try
        {
            using var response = await WebhookClient.PostAsync((action.Target ?? string.Empty).Trim(), content);
            return response.IsSuccessStatusCode
                ? ActionOutcome.Done
                : ActionOutcome.Failed(RuleActivityCode.WebhookFailed,
                    "HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
        }
        catch (TaskCanceledException)
        {
            return ActionOutcome.Failed(RuleActivityCode.WebhookTimeout);
        }
        catch (HttpRequestException ex)
        {
            return ActionOutcome.Failed(RuleActivityCode.WebhookUnreachable, ex.Message);
        }
    }

    private static HttpClient CreateWebhookClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WinXTools-Rules");
        return client;
    }

    /// <summary>Builds the activity log entry for one action result.</summary>
    private static RuleActivityEntry DescribeAction(NetworkRule rule, RuleAction action, ActionOutcome outcome)
    {
        string? target = action.Target;
        long? amount = null;
        switch (action.Type)
        {
            case ActionType.BlockPort:
            case ActionType.UnblockPort:
                var protocol = RuleValidator.NormalizeProtocol(action.Parameters?.GetValueOrDefault("protocol")) ?? "TCP";
                target = $"{action.Target}/{protocol}";
                break;

            case ActionType.LimitBandwidth:
                target = action.Parameters?.GetValueOrDefault("process");
                if (RuleValidator.TryParseLimit(action.Target, out var kbps)) amount = kbps;
                break;

            case ActionType.WriteToFile:
                // After a write, show the full path so the user can find the file.
                if (outcome.Kind == RuleActivityKind.Succeeded && outcome.Detail != null) target = outcome.Detail;
                break;

            case ActionType.SendWebhook:
                // Only the host: webhook URLs often carry secret tokens.
                target = Uri.TryCreate(action.Target, UriKind.Absolute, out var uri) ? uri.Host : null;
                break;

            case ActionType.ShowNotification:
                target = null;
                break;
        }

        bool isNotification = action.Type == ActionType.ShowNotification && outcome.Kind == RuleActivityKind.Succeeded;
        return new RuleActivityEntry
        {
            Kind = isNotification ? RuleActivityKind.Notification : outcome.Kind,
            Code = outcome.Code,
            RuleId = rule.Id,
            RuleName = rule.Name,
            Action = action.Type,
            Target = target,
            Amount = amount,
            Detail = outcome.Detail
        };
    }

    private static string ReplaceVariables(string template, NetworkRule rule)
    {
        // Invariant culture: the same text on every PC (no Buddhist-calendar years in logs).
        var now = DateTime.Now;
        return template
            .Replace("{rule_name}", rule.Name)
            .Replace("{rule_id}", rule.Id)
            .Replace("{datetime}", now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
            .Replace("{date}", now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Replace("{time}", now.ToString("HH:mm:ss", CultureInfo.InvariantCulture))
            .Replace("{trigger_count}", rule.TriggerCount.ToString(CultureInfo.InvariantCulture));
    }

    // One log line per entry: a rule name or message cannot fake extra lines.
    private static string OneLine(string text) => text.Replace('\r', ' ').Replace('\n', ' ');

    #endregion

    #region Activity Log

    private void LogActivity(RuleActivityEntry entry)
    {
        entry.Sequence = Interlocked.Increment(ref _activitySequence);
        lock (_activityLock)
        {
            _activity.AddFirst(entry);
            while (_activity.Count > MaxActivityEntries) _activity.RemoveLast();
        }
        ActivityLogged?.Invoke(this, new RuleActivityEventArgs(entry));
    }

    private void LogEngineNotice(RuleActivityKind kind, RuleActivityCode code, long? amount = null, string? detail = null)
    {
        LogActivity(new RuleActivityEntry { Kind = kind, Code = code, Amount = amount, Detail = detail });
    }

    private void ReportSaveError(RuleStorageException ex)
    {
        lock (_lock)
        {
            if (_saveErrorReported) return;
            _saveErrorReported = true;
        }
        LogEngineNotice(RuleActivityKind.Warning, RuleActivityCode.SaveFailed,
            detail: ex.InnerException?.Message ?? ex.Message);
    }

    #endregion

    #region Persistence

    private void LoadRules()
    {
        void OnSetAside(string path) =>
            LogEngineNotice(RuleActivityKind.Warning, RuleActivityCode.UntrustedFileSetAside, detail: path);

        try
        {
            RuleStorage.EnsureSecureDirectory(RuleStorage.RootDirectory, OnSetAside);

            List<NetworkRule> rules;
            if (RuleStorage.TryReadRules(OnSetAside, out var json))
            {
                List<NetworkRule>? stored = null;
                if (json != null)
                {
                    try
                    {
                        stored = JsonSerializer.Deserialize<List<NetworkRule>>(json);
                    }
                    catch (JsonException)
                    {
                        // Keep the unreadable file for support instead of overwriting it.
                        LogEngineNotice(RuleActivityKind.Warning, RuleActivityCode.UnreadableFileSetAside,
                            detail: RuleStorage.SetAsideRulesFile("unreadable"));
                    }
                }
                rules = SanitizeRules(stored, imported: false);
            }
            else
            {
                // First start of this version: bring over the old per-user rules
                // once. The old file is left alone but never read again, because
                // rules.json exists from now on (it is saved below).
                rules = SanitizeRules(ReadLegacyRules(), imported: true);
            }

            lock (_lock)
            {
                _rules.Clear();
                _rules.AddRange(rules);
                SaveRulesLocked();
            }
        }
        catch (RuleStorageException ex)
        {
            FailStorage(ex.Error, ex);
        }
        catch (Exception ex)
        {
            FailStorage(RuleStorageError.IoError, ex);
        }
    }

    private void FailStorage(RuleStorageError error, Exception ex)
    {
        Debug.WriteLine($"Rule storage unavailable ({error}): {ex.Message}");
        lock (_lock)
        {
            _rules.Clear();
        }
        StorageError = error;
        LogEngineNotice(RuleActivityKind.Warning, error switch
        {
            RuleStorageError.AccessDenied => RuleActivityCode.StorageAccessDenied,
            RuleStorageError.NotSecure => RuleActivityCode.StorageNotSecure,
            _ => RuleActivityCode.StorageIoError
        }, detail: RuleStorage.RootDirectory);
    }

    /// <summary>
    /// Older versions kept rules in the user's roaming profile, which any program
    /// of that user can edit. They are imported once, with every rule turned
    /// off so the user reviews them before this elevated app acts on them.
    /// </summary>
    private static List<NetworkRule>? ReadLegacyRules()
    {
        try
        {
            var file = new FileInfo(RuleStorage.LegacyRulesFile);
            if (!file.Exists || file.Length > RuleStorage.MaxLegacyFileBytes ||
                RuleStorage.IsLink(file) || RuleStorage.IsLink(file.Directory))
                return null;

            return JsonSerializer.Deserialize<List<NetworkRule>>(File.ReadAllText(file.FullName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Debug.WriteLine($"Could not read old rules: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Drops removed actions (and rules left without actions) and repairs ids,
    /// names and limits. Settings this version does not know (a newer version's
    /// file) are kept, but such rules are turned off: dropping an unknown
    /// condition could make a rule run far more often than intended.
    /// </summary>
    private List<NetworkRule> SanitizeRules(List<NetworkRule>? rules, bool imported)
    {
        var result = new List<NetworkRule>();
        var ids = new HashSet<string>();
        int droppedActions = 0, droppedRules = 0, turnedOff = 0;

        foreach (var rule in (rules ?? new List<NetworkRule>()).Where(r => r != null).Take(MaxRules))
        {
            var actions = (rule.Actions ?? new List<RuleAction>()).Where(a => a != null).ToList();
            rule.Actions = actions
                .Where(a => !RuleValidator.IsRemovedAction(a.Type))
                .Take(RuleValidator.MaxActions)
                .ToList();
            droppedActions += actions.Count(a => RuleValidator.IsRemovedAction(a.Type));
            if (rule.Actions.Count == 0)
            {
                droppedRules++;
                continue;
            }

            rule.Conditions = (rule.Conditions ?? new List<RuleCondition>())
                .Where(c => c != null)
                .Take(RuleValidator.MaxConditions)
                .ToList();
            if (string.IsNullOrWhiteSpace(rule.Id) || !ids.Add(rule.Id))
            {
                rule.Id = Guid.NewGuid().ToString();
                ids.Add(rule.Id);
            }
            rule.Name = string.IsNullOrWhiteSpace(rule.Name) ? "Rule" : rule.Name.Trim();
            rule.Description ??= string.Empty;
            rule.CooldownSeconds = Math.Clamp(rule.CooldownSeconds, 0, RuleValidator.MaxCooldownSeconds);

            bool unknownSettings = !Enum.IsDefined(rule.ConditionLogic) ||
                                   rule.Conditions.Any(c => !Enum.IsDefined(c.Type)) ||
                                   rule.Actions.Any(a => !Enum.IsDefined(a.Type));
            if (imported)
            {
                rule.IsEnabled = false;
            }
            else if (unknownSettings && rule.IsEnabled)
            {
                rule.IsEnabled = false;
                turnedOff++;
            }
            result.Add(rule);
        }

        if (imported && result.Count > 0)
            LogEngineNotice(RuleActivityKind.Info, RuleActivityCode.RulesImported, amount: result.Count);
        if (droppedActions > 0)
            LogEngineNotice(RuleActivityKind.Warning, RuleActivityCode.RemovedActionsDropped, amount: droppedActions);
        if (droppedRules > 0)
            LogEngineNotice(RuleActivityKind.Warning, RuleActivityCode.InvalidRulesDropped, amount: droppedRules);
        if (turnedOff > 0)
            LogEngineNotice(RuleActivityKind.Warning, RuleActivityCode.UnknownSettingsTurnedOff, amount: turnedOff);
        return result;
    }

    /// <summary>Saves all rules. Call while holding <see cref="_lock"/>.</summary>
    private void SaveRulesLocked()
    {
        EnsureStorageAvailable();
        try
        {
            RuleStorage.WriteRules(JsonSerializer.Serialize(_rules, JsonOptions));
            _saveErrorReported = false;
        }
        catch (RuleStorageException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new RuleStorageException(RuleStorageError.IoError, RuleStorage.RulesFile, ex);
        }
    }

    /// <summary>Background saves (after a rule ran) report instead of throwing.</summary>
    private RuleStorageException? TrySaveLocked()
    {
        try
        {
            SaveRulesLocked();
            return null;
        }
        catch (RuleStorageException ex)
        {
            return ex;
        }
    }

    #endregion

    #region Helpers

    private sealed class RuleRuntimeState
    {
        /// <summary>The rule ran for the current "conditions true" period.</summary>
        public bool AlreadyFired;
        public bool ErrorReported;
    }

    private readonly record struct ActionOutcome(RuleActivityKind Kind, RuleActivityCode Code, string? Detail = null)
    {
        public static ActionOutcome Done => new(RuleActivityKind.Succeeded, RuleActivityCode.Done);
        public static ActionOutcome Requested => new(RuleActivityKind.Succeeded, RuleActivityCode.Requested);
        public static ActionOutcome Succeeded(string? detail) => new(RuleActivityKind.Succeeded, RuleActivityCode.Done, detail);
        public static ActionOutcome Skipped(RuleActivityCode code) => new(RuleActivityKind.Skipped, code);
        public static ActionOutcome Failed(RuleActivityCode code, string? detail = null) => new(RuleActivityKind.Failed, code, detail);
    }

    /// <summary>
    /// System state for one evaluation pass, read lazily and at most once, so
    /// ten rules asking about running apps cost one process snapshot. It uses
    /// the IP helper tables directly (no DNS lookups, unlike ConnectionMonitor).
    /// </summary>
    private sealed class EvaluationContext
    {
        private HashSet<string>? _processNames;
        private TcpConnectionInformation[]? _tcpConnections;
        private HashSet<int>? _portsInUse;

        public EvaluationContext(double? totalBytesPerSecond) => TotalBytesPerSecond = totalBytesPerSecond;

        /// <summary>Download + upload of all adapters in bytes/s, or null until two samples exist.</summary>
        public double? TotalBytesPerSecond { get; }

        public HashSet<string> RunningProcessNames => _processNames ??= ReadProcessNames();

        private TcpConnectionInformation[] TcpConnections =>
            _tcpConnections ??= IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections();

        /// <summary>Same number the Connections page shows: TCP connections with a remote address.</summary>
        public int ConnectionCount => TcpConnections.Count(c =>
            !c.RemoteEndPoint.Address.Equals(IPAddress.Any) && !c.RemoteEndPoint.Address.Equals(IPAddress.IPv6Any));

        public bool IsConnectedTo(IPAddress address) => TcpConnections.Any(c =>
            c.State == TcpState.Established && RuleValidator.NormalizeAddress(c.RemoteEndPoint.Address).Equals(address));

        public bool IsPortInUse(int port) => (_portsInUse ??= ReadPortsInUse()).Contains(port);

        private HashSet<int> ReadPortsInUse()
        {
            var ports = new HashSet<int>();
            foreach (var connection in TcpConnections)
            {
                ports.Add(connection.LocalEndPoint.Port);
                ports.Add(connection.RemoteEndPoint.Port);
            }

            var properties = IPGlobalProperties.GetIPGlobalProperties();
            foreach (var listener in properties.GetActiveTcpListeners()) ports.Add(listener.Port);
            foreach (var listener in properties.GetActiveUdpListeners()) ports.Add(listener.Port);
            return ports;
        }

        private static HashSet<string> ReadProcessNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    names.Add(process.ProcessName);
                }
                catch (InvalidOperationException)
                {
                    // Exited while enumerating.
                }
                finally
                {
                    process.Dispose();
                }
            }
            return names;
        }
    }

    /// <summary>
    /// Total adapter throughput from the OS byte counters. The engine keeps its
    /// own sampler because NetworkMonitor is driven from the UI thread and is
    /// not safe to call from this timer thread.
    /// </summary>
    private sealed class NetworkSpeedSampler
    {
        private long _lastBytes = -1;
        private long _lastTimestamp;

        /// <summary>Bytes/second since the previous sample, or null when there is no usable previous sample.</summary>
        public double? Sample()
        {
            long bytes = 0;
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up ||
                    adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;
                try
                {
                    var stats = adapter.GetIPStatistics();
                    bytes += stats.BytesReceived + stats.BytesSent;
                }
                catch (NetworkInformationException)
                {
                    // Adapter went away between listing and reading.
                }
            }

            long now = Stopwatch.GetTimestamp();
            double? speed = null;
            if (_lastBytes >= 0)
            {
                double seconds = (now - _lastTimestamp) / (double)Stopwatch.Frequency;
                // After a long gap (paused, no speed rules for a while) the average
                // would span minutes; start over instead.
                if (seconds is > 0.5 and < 30 && bytes >= _lastBytes)
                    speed = (bytes - _lastBytes) / seconds;
            }
            _lastBytes = bytes;
            _lastTimestamp = now;
            return speed;
        }
    }

    #endregion
}

#region Storage

/// <summary>
/// Where rules and rule log files live: %ProgramData%\WinXTools, owned by
/// Administrators with a protected ACL (Administrators + SYSTEM only, no
/// inheritance). Normal-user programs can neither change the rules this
/// elevated app acts on nor plant links where it writes.
/// </summary>
internal static class RuleStorage
{
    /// <summary>The old, user-writable file is not trusted with a huge read.</summary>
    public const long MaxLegacyFileBytes = 4 * 1024 * 1024;
    private const long MaxLogBytes = 5 * 1024 * 1024;

    public static string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WinXTools");
    public static string RulesFile { get; } = Path.Combine(RootDirectory, "rules.json");
    public static string LogDirectory { get; } = Path.Combine(RootDirectory, "Logs");

    /// <summary>Rules file of older versions: user-writable, read once for migration.</summary>
    public static string LegacyRulesFile { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetX", "rules.json");

    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly object LogLock = new();

    // Any of these lets a principal change or replace what the app reads or writes.
    private const int WriteRightsMask =
        (int)(FileSystemRights.WriteData | FileSystemRights.AppendData |
              FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes |
              FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete |
              FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership) |
        0x10000000 | // GENERIC_ALL
        0x40000000;  // GENERIC_WRITE

    /// <summary>
    /// Creates the folder, or repairs its ACL, so only Administrators and SYSTEM
    /// can write to it. A folder someone else created first (or a link in its
    /// place) is moved aside, never trusted or re-ACLed.
    /// </summary>
    public static void EnsureSecureDirectory(string path, Action<string>? onSetAside = null)
    {
        var info = new DirectoryInfo(path);
        if (info.Exists && (IsLink(info) || !HasTrustedOwner(info, path)))
        {
            var aside = $"{path}.untrusted-{Timestamp()}";
            try
            {
                Directory.Move(path, aside); // renames a junction itself, does not follow it
            }
            catch (Exception ex)
            {
                throw new RuleStorageException(RuleStorageError.NotSecure, path, ex);
            }
            onSetAside?.Invoke(aside);
            info.Refresh();
        }

        if (!info.Exists)
        {
            try
            {
                // Created with the final ACL in one call, so the inherited ProgramData
                // ACL (where Users may create files) never applies to it.
                CreateDirectorySecurity().CreateDirectory(path);
            }
            catch (Exception ex)
            {
                throw new RuleStorageException(RuleStorageError.AccessDenied, path, ex);
            }
            info.Refresh();
            if (IsLink(info) || !HasTrustedOwner(info, path))
                throw new RuleStorageException(RuleStorageError.NotSecure, path);
        }

        try
        {
            // Applied every time: removes any access granted to others since.
            info.SetAccessControl(CreateDirectorySecurity());
        }
        catch (Exception ex)
        {
            throw new RuleStorageException(RuleStorageError.AccessDenied, path, ex);
        }

        if (!IsSecure(info))
            throw new RuleStorageException(RuleStorageError.NotSecure, path);
    }

    /// <summary>
    /// Reads rules.json. Returns false when there is no rules file yet. A file
    /// that someone besides Administrators/SYSTEM could have written is moved
    /// aside and ignored (json is null).
    /// </summary>
    public static bool TryReadRules(Action<string>? onSetAside, out string? json)
    {
        json = null;
        var info = new FileInfo(RulesFile);
        if (!info.Exists) return false;

        if (IsLink(info) || !IsSecure(info))
        {
            onSetAside?.Invoke(SetAsideRulesFile("untrusted"));
            return true;
        }

        json = File.ReadAllText(RulesFile);
        return true;
    }

    /// <summary>Renames rules.json out of the way (a link is renamed, not followed).</summary>
    public static string SetAsideRulesFile(string reason)
    {
        var aside = $"{RulesFile}.{reason}-{Timestamp()}";
        File.Move(RulesFile, aside);
        return aside;
    }

    public static void WriteRules(string json)
    {
        EnsureSecureDirectory(RootDirectory);

        // Never write through a leftover (possibly linked) temp file: delete it
        // and create a brand-new one, then swap it in atomically.
        var temp = RulesFile + ".tmp";
        File.Delete(temp);
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(json);
        }
        new FileInfo(temp).SetAccessControl(CreateFileSecurity());
        File.Move(temp, RulesFile, overwrite: true);
    }

    /// <summary>Appends a line to a rule log file inside the protected log folder; returns its full path.</summary>
    public static string AppendToLog(string fileName, string line)
    {
        lock (LogLock)
        {
            // Checked on every write (rules write rarely): re-protecting the root
            // first removes access someone may have been granted meanwhile (e.g.
            // Explorer's "Continue" button), then a swapped Logs folder is moved aside.
            EnsureSecureDirectory(RootDirectory);
            EnsureSecureDirectory(LogDirectory);

            var path = Path.Combine(LogDirectory, fileName);
            var info = new FileInfo(path);
            if (info.Exists)
            {
                if (IsLink(info)) throw new RuleStorageException(RuleStorageError.NotSecure, path);
                // Keep each log bounded; the previous part stays as ".old".
                if (info.Length > MaxLogBytes) File.Move(path, path + ".old", overwrite: true);
            }

            File.AppendAllText(path, line, new UTF8Encoding(false));
            return path;
        }
    }

    public static bool IsLink(FileSystemInfo? info) =>
        info != null && info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0;

    private static bool HasTrustedOwner(DirectoryInfo info, string path)
    {
        try
        {
            var security = info.GetAccessControl(AccessControlSections.Owner);
            return IsTrusted(security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier);
        }
        catch (Exception ex)
        {
            // Typically not elevated: the folder is readable by Administrators only.
            throw new RuleStorageException(RuleStorageError.AccessDenied, path, ex);
        }
    }

    private static bool IsSecure(FileSystemInfo info)
    {
        try
        {
            FileSystemSecurity security = info is DirectoryInfo directory
                ? directory.GetAccessControl()
                : ((FileInfo)info).GetAccessControl();

            if (!IsTrusted(security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier))
                return false;

            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType == AccessControlType.Allow &&
                    ((int)rule.FileSystemRights & WriteRightsMask) != 0 &&
                    !IsTrusted(rule.IdentityReference as SecurityIdentifier))
                    return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not read ACL of {info.FullName}: {ex.Message}");
            return false;
        }
    }

    private static bool IsTrusted(SecurityIdentifier? sid) =>
        sid != null && (sid.Equals(Administrators) || sid.Equals(LocalSystem));

    private static DirectorySecurity CreateDirectorySecurity()
    {
        var security = new DirectorySecurity();
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { Administrators, LocalSystem })
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        }
        return security;
    }

    private static FileSecurity CreateFileSecurity()
    {
        var security = new FileSecurity();
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private static string Timestamp() => DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
}

public enum RuleStorageError
{
    /// <summary>The rules folder could not be created or protected (usually: not running as administrator).</summary>
    AccessDenied,
    /// <summary>The rules folder is a link or has an owner/permissions WinXTools cannot trust.</summary>
    NotSecure,
    /// <summary>Reading or writing the rules file failed.</summary>
    IoError
}

public class RuleStorageException : Exception
{
    public RuleStorageError Error { get; }
    public string Location { get; }

    public RuleStorageException(RuleStorageError error, string location, Exception? innerException = null)
        : base($"Rule storage error ({error}): {location}", innerException)
    {
        Error = error;
        Location = location;
    }
}

#endregion

#region Validation

public enum RuleProblem
{
    NameRequired,
    NoActions,
    TooManyRules,
    TooManyItems,
    InvalidCooldown,
    InvalidTargetApp,
    UnknownType,
    InvalidTime,
    SameStartEnd,
    NoDays,
    AppRequired,
    InvalidAppName,
    InvalidNumber,
    InvalidIp,
    InvalidPort,
    InvalidProtocol,
    InvalidLimit,
    InvalidFileName,
    InvalidUrl,
    ProtectedProcess,
    RemovedAction
}

/// <summary>A problem in a rule; the index says which condition or action (-1 = the rule itself).</summary>
public readonly record struct RuleValidationError(RuleProblem Problem, int ConditionIndex = -1, int ActionIndex = -1);

public class RuleValidationException : Exception
{
    public IReadOnlyList<RuleValidationError> Errors { get; }

    public RuleValidationException(IReadOnlyList<RuleValidationError> errors)
        : base($"The rule is not valid: {string.Join(", ", errors.Select(e => e.Problem))}")
    {
        Errors = errors;
    }
}

/// <summary>
/// Checks and normalizes rule values. The editor uses it to show friendly
/// errors; the engine uses it again before acting, because it never trusts
/// stored values.
/// </summary>
public static class RuleValidator
{
    public const int MaxRules = 200;
    public const int MaxConditions = 20;
    public const int MaxActions = 20;
    public const int DefaultCooldownSeconds = 60;
    public const int MaxCooldownSeconds = 86_400;
    public const long MaxSpeedKBps = 10_000_000;
    public const long MaxConnectionCount = 1_000_000;
    public const int MaxLimitKBps = 10_000_000;

    /// <summary>App value meaning "the whole PC" for speed limits.</summary>
    public const string AllApps = "*";

    private static readonly string[] TimeFormats = { @"h\:mm", @"hh\:mm", @"h\:mm\:ss", @"hh\:mm\:ss" };
    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();
    private static readonly string[] ReservedFileNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    // On top of ProcessKiller's list: closing these crashes or unlocks Windows.
    private static readonly HashSet<string> ExtraProtectedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "idle", "lsaiso", "logonui", "memory compression", "secure system", "msmpeng", "securityhealthservice"
    };

    private static readonly string CurrentProcessName = GetCurrentProcessName();

    /// <summary>Actions that no longer run (kept in the enum so old files still load).</summary>
    public static bool IsRemovedAction(ActionType type) =>
        type is ActionType.SendLineNotify or ActionType.SendSMS or ActionType.SendEmail or ActionType.ExecuteCommand;

    public static bool IsSupportedAction(ActionType type) => Enum.IsDefined(type) && !IsRemovedAction(type);

    public static List<RuleValidationError> Validate(NetworkRule rule)
    {
        var errors = new List<RuleValidationError>();
        if (string.IsNullOrWhiteSpace(rule.Name))
            errors.Add(new RuleValidationError(RuleProblem.NameRequired));
        if (!string.IsNullOrWhiteSpace(rule.TargetProcessName) &&
            !IsValidProcessName(NormalizeProcessName(rule.TargetProcessName)))
            errors.Add(new RuleValidationError(RuleProblem.InvalidTargetApp));
        if (rule.CooldownSeconds is < 0 or > MaxCooldownSeconds)
            errors.Add(new RuleValidationError(RuleProblem.InvalidCooldown));

        var conditions = rule.Conditions ?? new List<RuleCondition>();
        var actions = rule.Actions ?? new List<RuleAction>();
        if (conditions.Count > MaxConditions || actions.Count > MaxActions)
            errors.Add(new RuleValidationError(RuleProblem.TooManyItems));

        for (int i = 0; i < conditions.Count; i++)
        {
            if (ValidateCondition(conditions[i], rule) is { } problem)
                errors.Add(new RuleValidationError(problem, ConditionIndex: i));
        }

        if (actions.Count == 0)
            errors.Add(new RuleValidationError(RuleProblem.NoActions));
        for (int i = 0; i < actions.Count; i++)
        {
            if (ValidateAction(actions[i], rule) is { } problem)
                errors.Add(new RuleValidationError(problem, ActionIndex: i));
        }
        return errors;
    }

    public static RuleProblem? ValidateCondition(RuleCondition condition, NetworkRule rule)
    {
        switch (condition.Type)
        {
            case ConditionType.TimeRange:
                if (!TryParseTime(condition.Value, out var start) || !TryParseTime(condition.Value2, out var end))
                    return RuleProblem.InvalidTime;
                return start == end ? RuleProblem.SameStartEnd : null;

            case ConditionType.DayOfWeek:
                return TryParseDays(condition.Value, out _) ? null : RuleProblem.NoDays;

            case ConditionType.ProcessRunning:
                return ValidateAppName(ResolveProcessName(condition.Value, rule));

            case ConditionType.BandwidthExceeds:
                return TryParseCount(condition.Value, MaxSpeedKBps, out _) ? null : RuleProblem.InvalidNumber;

            case ConditionType.ConnectionCount:
                return TryParseCount(condition.Value, MaxConnectionCount, out _) ? null : RuleProblem.InvalidNumber;

            case ConditionType.IPConnected:
                return TryParseIp(condition.Value, out _) ? null : RuleProblem.InvalidIp;

            case ConditionType.PortInUse:
                return TryParsePort(condition.Value, out _) ? null : RuleProblem.InvalidPort;

            case ConditionType.Always:
                return null;

            default:
                return RuleProblem.UnknownType;
        }
    }

    public static RuleProblem? ValidateAction(RuleAction action, NetworkRule rule)
    {
        if (IsRemovedAction(action.Type)) return RuleProblem.RemovedAction;

        var parameters = action.Parameters;
        switch (action.Type)
        {
            case ActionType.BlockIP:
            case ActionType.UnblockIP:
                return TryNormalizeIpTarget(action.Target, out _) ? null : RuleProblem.InvalidIp;

            case ActionType.BlockPort:
            case ActionType.UnblockPort:
                if (!TryParsePort(action.Target, out _)) return RuleProblem.InvalidPort;
                return NormalizeProtocol(parameters?.GetValueOrDefault("protocol")) == null ? RuleProblem.InvalidProtocol : null;

            case ActionType.LimitBandwidth:
                if (!TryParseLimit(action.Target, out _)) return RuleProblem.InvalidLimit;
                return ValidateAppOrAll(parameters?.GetValueOrDefault("process"));

            case ActionType.UnlimitBandwidth:
                return ValidateAppOrAll(action.Target);

            case ActionType.KillProcess:
                var name = ResolveProcessName(action.Target, rule);
                return ValidateAppName(name) ?? (IsProtectedProcess(name) ? RuleProblem.ProtectedProcess : null);

            case ActionType.WriteToFile:
                return NormalizeLogFileName(action.Target) == null ? RuleProblem.InvalidFileName : null;

            case ActionType.ShowNotification:
                return null;

            case ActionType.SendWebhook:
                return IsValidWebhookUrl(action.Target) ? null : RuleProblem.InvalidUrl;

            default:
                return RuleProblem.UnknownType;
        }
    }

    private static RuleProblem? ValidateAppName(string name)
    {
        if (name.Length == 0) return RuleProblem.AppRequired;
        return IsValidProcessName(name) ? null : RuleProblem.InvalidAppName;
    }

    private static RuleProblem? ValidateAppOrAll(string? value)
    {
        // Blank uses the rule's target app (or the whole PC).
        var name = NormalizeProcessName(value);
        return name.Length == 0 || name == AllApps || IsValidProcessName(name) ? null : RuleProblem.InvalidAppName;
    }

    /// <summary>"chrome.exe " -> "chrome".</summary>
    public static string NormalizeProcessName(string? value)
    {
        var name = (value ?? string.Empty).Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4].TrimEnd();
        return name;
    }

    /// <summary>A plain app name: no folders, wildcards or other characters Windows forbids in file names.</summary>
    public static bool IsValidProcessName(string name) =>
        name.Length is > 0 and <= 128 && name != "." && name != ".." && name.IndexOfAny(InvalidNameChars) < 0;

    /// <summary>Windows processes a rule must never close, and WinXTools itself.</summary>
    public static bool IsProtectedProcess(string name) =>
        NetX.Core.Optimization.ProcessKiller.IsProtectedProcess(name) ||
        ExtraProtectedProcesses.Contains(name) ||
        string.Equals(name, CurrentProcessName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The app name, or the rule's Target app when the value is blank.</summary>
    public static string ResolveProcessName(string? value, NetworkRule rule)
    {
        var name = NormalizeProcessName(value);
        return name.Length > 0 ? name : NormalizeProcessName(rule.TargetProcessName);
    }

    /// <summary>Like <see cref="ResolveProcessName"/>, but falls back to "*" (whole PC).</summary>
    public static string ResolveAppOrAll(string? value, NetworkRule rule)
    {
        var name = ResolveProcessName(value, rule);
        return name.Length > 0 ? name : AllApps;
    }

    /// <summary>Accepts "22:00", "9:30" and "22.00" (Thai style).</summary>
    public static bool TryParseTime(string? value, out TimeSpan time)
    {
        var text = (value ?? string.Empty).Trim().Replace('.', ':');
        return TimeSpan.TryParseExact(text, TimeFormats, CultureInfo.InvariantCulture, out time) &&
               time >= TimeSpan.Zero && time < TimeSpan.FromDays(1);
    }

    public static string FormatTime(TimeSpan time) => time.ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    /// <summary>English day names or abbreviations separated by commas, e.g. "Monday,Fri".</summary>
    public static bool TryParseDays(string? value, out HashSet<DayOfWeek> days)
    {
        days = new HashSet<DayOfWeek>();
        foreach (var token in (value ?? string.Empty).Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var day in Enum.GetValues<DayOfWeek>())
            {
                var name = day.ToString();
                if (token.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                    (token.Length >= 3 && name.StartsWith(token, StringComparison.OrdinalIgnoreCase)))
                {
                    days.Add(day);
                    break;
                }
            }
        }
        return days.Count > 0;
    }

    /// <summary>Stored form, Monday first: "Monday,Friday".</summary>
    public static string FormatDays(IEnumerable<DayOfWeek> days) =>
        string.Join(",", days.Distinct().OrderBy(d => ((int)d + 6) % 7));

    /// <summary>A whole number from 0 to max (digits only, any culture).</summary>
    public static bool TryParseCount(string? value, long max, out long count) =>
        long.TryParse((value ?? string.Empty).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out count) &&
        count <= max;

    public static bool TryParsePort(string? value, out int port) =>
        int.TryParse((value ?? string.Empty).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) &&
        port is >= 1 and <= 65535;

    /// <summary>Speed limit in KB/s; 0 blocks.</summary>
    public static bool TryParseLimit(string? value, out int kbps) =>
        int.TryParse((value ?? string.Empty).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out kbps) &&
        kbps <= MaxLimitKBps;

    /// <summary>"TCP" (also for blank) or "UDP"; null for anything else.</summary>
    public static string? NormalizeProtocol(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0 || text.Equals("TCP", StringComparison.OrdinalIgnoreCase)) return "TCP";
        return text.Equals("UDP", StringComparison.OrdinalIgnoreCase) ? "UDP" : null;
    }

    /// <summary>A single IPv4/IPv6 address (IPv4-mapped IPv6 becomes IPv4).</summary>
    public static bool TryParseIp(string? value, out IPAddress address)
    {
        address = IPAddress.None;
        var text = (value ?? string.Empty).Trim();
        // Reject short IPv4 forms ("10.1") and zone ids ("fe80::1%3").
        if (text.Length == 0 || text.Contains('%') || !IPAddress.TryParse(text, out var parsed)) return false;
        if (parsed.AddressFamily == AddressFamily.InterNetwork && text.Count(c => c == '.') != 3) return false;

        address = NormalizeAddress(parsed);
        return true;
    }

    /// <summary>An IP address or a CIDR range ("10.0.0.0/24"), in canonical form.</summary>
    public static bool TryNormalizeIpTarget(string? value, out string normalized)
    {
        normalized = string.Empty;
        var text = (value ?? string.Empty).Trim();
        var slash = text.IndexOf('/');
        if (!TryParseIp(slash >= 0 ? text[..slash] : text, out var address)) return false;

        normalized = address.ToString();
        if (slash < 0) return true;

        int maxPrefix = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (!int.TryParse(text[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix) ||
            prefix > maxPrefix)
            return false;

        normalized += "/" + prefix.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    public static IPAddress NormalizeAddress(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>
    /// A plain .log/.txt file name ("rules" becomes "rules.log"), or null. No
    /// folders, streams or device names, so the file stays in the log folder.
    /// </summary>
    public static string? NormalizeLogFileName(string? value)
    {
        var name = (value ?? string.Empty).Trim();
        if (name.Length is 0 or > 64 || name.StartsWith('.') || name.EndsWith('.') || name.Contains(".."))
            return null;
        foreach (var c in name)
        {
            // Letters/digits of any script (Thai names need their vowel and tone marks).
            if (char.IsLetterOrDigit(c) || c is ' ' or '_' or '-' or '.' ||
                char.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)
                continue;
            return null;
        }

        var extension = Path.GetExtension(name);
        if (extension.Length == 0) name += ".log";
        else if (!extension.Equals(".log", StringComparison.OrdinalIgnoreCase) &&
                 !extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
            return null;

        var stem = name[..name.IndexOf('.')].TrimEnd();
        if (stem.Length == 0 || ReservedFileNames.Contains(stem, StringComparer.OrdinalIgnoreCase)) return null;
        return name;
    }

    public static bool IsValidWebhookUrl(string? value) =>
        Uri.TryCreate((value ?? string.Empty).Trim(), UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
        uri.Host.Length > 0;

    private static string GetCurrentProcessName()
    {
        using var process = Process.GetCurrentProcess();
        return process.ProcessName;
    }
}

#endregion

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

    /// <summary>
    /// Minimum seconds between two runs. A rule only runs when its conditions
    /// become true; the cooldown also calms conditions that flip on and off.
    /// </summary>
    public int CooldownSeconds { get; set; } = RuleValidator.DefaultCooldownSeconds;

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? LastTriggeredAt { get; set; }
    public int TriggerCount { get; set; }

    /// <summary>Result of the last run: actions that worked (or had nothing to do) and actions that failed.</summary>
    public int LastRunSucceeded { get; set; }
    public int LastRunFailed { get; set; }

    // For specific app targeting: the app used by process conditions and
    // actions whose own app field is blank.
    public string? TargetProcessName { get; set; }
    public int? TargetProcessId { get; set; }

    public NetworkRule Clone()
    {
        var copy = (NetworkRule)MemberwiseClone();
        copy.Conditions = (Conditions ?? new List<RuleCondition>()).Where(c => c != null).Select(c => c.Clone()).ToList();
        copy.Actions = (Actions ?? new List<RuleAction>()).Where(a => a != null).Select(a => a.Clone()).ToList();
        return copy;
    }

    /// <summary>Copies what the editor changes; run history stays as it is.</summary>
    internal void CopySettingsFrom(NetworkRule source)
    {
        Name = source.Name;
        Description = source.Description;
        IsEnabled = source.IsEnabled;
        Priority = source.Priority;
        ConditionLogic = source.ConditionLogic;
        Conditions = source.Conditions.Select(c => c.Clone()).ToList();
        Actions = source.Actions.Select(a => a.Clone()).ToList();
        CooldownSeconds = source.CooldownSeconds;
        TargetProcessName = source.TargetProcessName;
        TargetProcessId = source.TargetProcessId;
    }
}

public class RuleCondition
{
    public ConditionType Type { get; set; }
    public ConditionOperator Operator { get; set; } = ConditionOperator.Equals;
    public string? Value { get; set; }
    public string? Value2 { get; set; } // For range conditions
    public Dictionary<string, string>? Parameters { get; set; }

    public RuleCondition Clone()
    {
        var copy = (RuleCondition)MemberwiseClone();
        copy.Parameters = Parameters == null ? null : new Dictionary<string, string>(Parameters);
        return copy;
    }
}

public class RuleAction
{
    public ActionType Type { get; set; }
    public string? Target { get; set; }
    public Dictionary<string, string>? Parameters { get; set; }

    public RuleAction Clone()
    {
        var copy = (RuleAction)MemberwiseClone();
        copy.Parameters = Parameters == null ? null : new Dictionary<string, string>(Parameters);
        return copy;
    }
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
    BandwidthExceeds,   // Total speed (KB/s) compared with a threshold
    ConnectionCount,    // Number of connections
    IPConnected,        // Specific IP is connected
    PortInUse,          // Specific port is in use
    Always              // Once when the rule is armed (app start, enabled, edited)
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

/// <summary>
/// Stored in rules.json as numbers: never renumber. Removed actions keep their
/// value so old files still load; they are dropped on load and never run.
/// </summary>
public enum ActionType
{
    BlockIP = 0,
    UnblockIP = 1,
    BlockPort = 2,
    UnblockPort = 3,
    LimitBandwidth = 4,
    UnlimitBandwidth = 5,
    WriteToFile = 6,
    ShowNotification = 7,
    SendWebhook = 8,
    SendLineNotify = 9,   // Removed: LINE Notify shut down on 31 Mar 2025
    SendSMS = 10,         // Removed: no SMS service
    SendEmail = 11,       // Removed: no email configuration
    ExecuteCommand = 12,  // Removed: ran cmd.exe elevated from a data file
    KillProcess = 13
}

public enum RuleEngineState
{
    /// <summary>Start() has not been called.</summary>
    Stopped,
    Running,
    /// <summary>Automation Rules needs Pro (license or trial).</summary>
    PausedNoPro,
    /// <summary>The rules folder could not be created or trusted; see <see cref="RuleEngine.StorageError"/>.</summary>
    StorageUnavailable
}

#endregion

#region Activity

public enum RuleActivityKind
{
    Fired,
    Succeeded,
    Skipped,
    Failed,
    Notification,
    Info,
    Warning
}

/// <summary>What happened, as a code the UI turns into localized text.</summary>
public enum RuleActivityCode
{
    None,
    // Action results
    Done,
    Requested,
    AlreadyBlocked,
    NotRunning,
    ProtectedProcess,
    InvalidSettings,
    FirewallFailed,
    KillFailed,
    WriteFailed,
    WebhookFailed,
    WebhookTimeout,
    WebhookUnreachable,
    ActionRemoved,
    UnexpectedError,
    // Engine notices
    ConditionError,
    RulesImported,
    RemovedActionsDropped,
    InvalidRulesDropped,
    UnknownSettingsTurnedOff,
    UntrustedFileSetAside,
    UnreadableFileSetAside,
    SaveFailed,
    StorageAccessDenied,
    StorageNotSecure,
    StorageIoError
}

public class RuleActivityEntry
{
    /// <summary>Increases with every entry; lets the UI skip entries it already has.</summary>
    public long Sequence { get; internal set; }
    public DateTime Time { get; init; } = DateTime.Now;
    public RuleActivityKind Kind { get; init; }
    public RuleActivityCode Code { get; init; }
    public string? RuleId { get; init; }
    public string RuleName { get; init; } = string.Empty;
    public ActionType? Action { get; init; }
    /// <summary>IP, "port/protocol", app ("*" = whole PC), file name or webhook host.</summary>
    public string? Target { get; init; }
    /// <summary>A number for the message: KB/s for speed limits, counts for notices.</summary>
    public long? Amount { get; init; }
    /// <summary>Error detail, notification text, log file path or folder.</summary>
    public string? Detail { get; init; }
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

public class RuleActivityEventArgs : EventArgs
{
    public RuleActivityEntry Entry { get; }

    public RuleActivityEventArgs(RuleActivityEntry entry)
    {
        Entry = entry;
    }
}

public class RuleNotificationEventArgs : EventArgs
{
    public string RuleName { get; }
    /// <summary>Message with variables filled in; empty means "use the default text".</summary>
    public string Message { get; }

    public RuleNotificationEventArgs(string ruleName, string message)
    {
        RuleName = ruleName;
        Message = message;
    }
}

#endregion
