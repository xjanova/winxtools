using NetX.Core.Data;

namespace NetX.Core.System;

/// <summary>
/// What the user answered when the cleaner asked about files of their own
/// programs (CapCut, AI coding tools): which pieces to include. Kept so the next
/// scan starts from the same ticks, and so 1-Click Optimize cleans those
/// categories only after the user has said yes to them once — never on a default.
/// </summary>
public static class CleanerChoices
{
    private const string SelectionKey = "Cleaner.Selection";
    private const string PartsKey = "Cleaner.Parts";
    private const string KnownPartsKey = "Cleaner.PartsKnown";

    /// <summary>
    /// The pieces to tick for what a scan found: the saved answer for pieces the
    /// user has seen before, the piece's own default for new ones.
    /// </summary>
    public static HashSet<string> Resolve(IEnumerable<CleanPart> parts)
    {
        var chosen = Read(PartsKey);
        var known = Read(KnownPartsKey);
        return parts.Where(p => known.Contains(p.Key) ? chosen.Contains(p.Key) : p.DefaultOn).Select(p => p.Key).ToHashSet();
    }

    /// <summary>Remembers the answer for the pieces that were on screen; pieces of other categories keep theirs.</summary>
    public static void Save(IEnumerable<CleanPart> shown, IReadOnlySet<string> chosen)
    {
        var shownKeys = shown.Select(p => p.Key).ToHashSet();
        var allChosen = Read(PartsKey);
        allChosen.ExceptWith(shownKeys);
        allChosen.UnionWith(chosen.Where(shownKeys.Contains));
        var known = Read(KnownPartsKey);
        known.UnionWith(shownKeys);
        try
        {
            DatabaseService.Instance.SetSetting(PartsKey, string.Join(",", allChosen));
            DatabaseService.Instance.SetSetting(KnownPartsKey, string.Join(",", known));
        }
        catch
        {
            // Not saved: the next scan asks from the defaults again.
        }
    }

    /// <summary>
    /// For cleaning without the Cleaner page (1-Click Optimize): the pieces to
    /// clean, or null when the category must be left alone because the user never
    /// said yes to it (or switched it off).
    /// </summary>
    public static IReadOnlySet<string>? ForUnattended(CleanCategory category)
    {
        if (!category.AskFirst) return null;
        if (!Read(SelectionKey).Contains(category.Key)) return null;
        var chosen = Read(PartsKey);
        return chosen.Count == 0 ? null : chosen;
    }

    private static HashSet<string> Read(string key)
    {
        try
        {
            var value = DatabaseService.Instance.GetSetting(key);
            return value == null
                ? new HashSet<string>()
                : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        }
        catch
        {
            return new HashSet<string>();
        }
    }
}
