namespace NetX.Core.System.Tweaks;

/// <summary>
/// A user-facing text in both UI languages. The Windows Tricks page picks the
/// Thai or English string when it builds the cards (the page is recreated on
/// every navigation, so switching language takes effect on the next visit).
/// </summary>
public sealed record LocText(string En, string Th)
{
    public string Get(bool thai) => thai && !string.IsNullOrEmpty(Th) ? Th : En;

    /// <summary>Joins several texts line by line, keeping both languages.</summary>
    public static LocText Join(IEnumerable<LocText> parts, string separator = "\n")
    {
        var list = parts.ToList();
        return new LocText(
            string.Join(separator, list.Select(p => p.En)),
            string.Join(separator, list.Select(p => p.Th)));
    }
}

/// <summary>What Detect() found on this machine right now.</summary>
public enum TrickState
{
    /// <summary>Detection failed or the state cannot be read.</summary>
    Unknown,
    /// <summary>The trick is in effect.</summary>
    Applied,
    /// <summary>The machine has the Windows default for this setting.</summary>
    Default,
    /// <summary>Some of the values are ours, some aren't (or a third value is set).</summary>
    Custom,
    /// <summary>The feature does not exist on this PC (hardware, edition, build).</summary>
    NotSupported
}

public sealed record TrickStatus(TrickState State, LocText? Note = null)
{
    public static TrickStatus Unknown(LocText? note = null) => new(TrickState.Unknown, note);
    public static TrickStatus Applied(LocText? note = null) => new(TrickState.Applied, note);
    public static TrickStatus Default(LocText? note = null) => new(TrickState.Default, note);
    public static TrickStatus Custom(LocText? note = null) => new(TrickState.Custom, note);
    public static TrickStatus NotSupported(LocText? note = null) => new(TrickState.NotSupported, note);
}

public enum TrickRisk
{
    Safe,
    Moderate,
    Dangerous
}

/// <summary>What the user has to do before a change is fully visible.</summary>
public enum RestartScope
{
    None = 0,
    Explorer = 1,
    SignOut = 2,
    Reboot = 3
}

/// <summary>Outcome of Apply / Restore / Run. Success is only reported after it was checked.</summary>
public sealed class TrickResult
{
    public bool Success { get; init; }
    public bool Cancelled { get; init; }
    public LocText? Message { get; init; }
    /// <summary>Raw tool output (already decoded) to show in the output dialog.</summary>
    public string? Output { get; init; }
    public RestartScope Restart { get; init; }
    /// <summary>A file or URI the page should open (un-elevated) after success, e.g. a report.</summary>
    public string? OpenAfter { get; init; }

    public static TrickResult Ok(LocText? message = null, RestartScope restart = RestartScope.None, string? output = null) =>
        new() { Success = true, Message = message, Restart = restart, Output = output };

    public static TrickResult Fail(LocText message, string? output = null) =>
        new() { Success = false, Message = message, Output = output };

    public static TrickResult Canceled(string? output = null) =>
        new()
        {
            Success = false,
            Cancelled = true,
            Output = output,
            Message = new LocText("Cancelled.", "ยกเลิกแล้ว")
        };
}

/// <summary>Everything a one-shot "Run" action receives.</summary>
public sealed class TrickRunContext
{
    public TrickRunContext(bool thai, CancellationToken token, string? input = null)
    {
        Thai = thai;
        Token = token;
        Input = input;
    }

    /// <summary>UI language, so tools can format their report text.</summary>
    public bool Thai { get; }
    public CancellationToken Token { get; }
    /// <summary>Value typed by the user when the trick defines an <see cref="TrickDefinition.InputPrompt"/>.</summary>
    public string? Input { get; }
    /// <summary>Live output shown in the dialog while the action runs.</summary>
    public ConsoleOutputBuffer Output { get; } = new();
}

/// <summary>
/// One card on the Windows Tricks page. Buttons are derived from what is set:
/// Apply/Revert → "Apply" + "Restore default", Run → "Run", Open → "Open",
/// SettingsUri → "Open Settings", NavigateTo → in-app page link.
/// All delegates run on a background thread.
/// </summary>
public sealed class TrickDefinition
{
    public required string Id { get; init; }
    public required string Category { get; init; }
    public required LocText Name { get; init; }
    public required LocText Description { get; init; }
    public LocText? Tip { get; init; }

    /// <summary>Extra warning shown in the confirmation dialog.</summary>
    public LocText? Warning { get; init; }

    public TrickRisk Risk { get; init; }
    public RestartScope Restart { get; init; }

    /// <summary>Geometry key defined in WindowsTricksView.xaml.</summary>
    public string Icon { get; init; } = "SettingsIcon";

    /// <summary>What exactly is changed or run (registry value, command). Shown on the card.</summary>
    public string? Technical { get; init; }

    /// <summary>Text for the "Copy" button (a command the user can run themselves).</summary>
    public string? CopyText { get; init; }

    // ---- state ----
    public Func<TrickStatus>? Detect { get; init; }

    /// <summary>Optional badge wording when the generic "Applied / Windows default / Partly" doesn't fit (e.g. "Removed").</summary>
    public LocText? AppliedLabel { get; init; }
    public LocText? DefaultLabel { get; init; }
    public LocText? CustomLabel { get; init; }

    // ---- toggle ----
    public Func<TrickRunContext, Task<TrickResult>>? Apply { get; init; }
    public Func<TrickRunContext, Task<TrickResult>>? Revert { get; init; }
    public LocText? ApplyLabel { get; init; }
    public LocText? RevertLabel { get; init; }

    // ---- one-shot action ----
    public Func<TrickRunContext, Task<TrickResult>>? Run { get; init; }
    public LocText? RunLabel { get; init; }
    /// <summary>Show the output dialog (with live output) while Run/Apply/Revert executes.</summary>
    public bool ShowsOutput { get; init; }
    /// <summary>The action can take minutes and may be cancelled from the output dialog.</summary>
    public bool Cancellable { get; init; }
    /// <summary>When set, the page asks for a value before Run (e.g. minutes for a timer).</summary>
    public LocText? InputPrompt { get; init; }
    public string? InputDefault { get; init; }

    // ---- launchers ----
    public Func<TrickResult>? Open { get; init; }
    public LocText? OpenLabel { get; init; }
    public string? SettingsUri { get; init; }
    public LocText? SettingsLabel { get; init; }
    /// <summary>Tag of an in-app page (MainWindow navigation tag), e.g. "Cleaner".</summary>
    public string? NavigateTo { get; init; }
}
