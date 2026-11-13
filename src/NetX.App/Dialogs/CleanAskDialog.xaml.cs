using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NetX.App.Helpers;
using NetX.App.Views;
using NetX.Core.System;

namespace NetX.App.Dialogs;

/// <summary>
/// After a scan: "these files were made by programs you use — clean them too?".
/// Lists what was found program by program; conversation records and downloaded
/// models are shown with a warning and are never ticked for the user.
/// </summary>
public partial class CleanAskDialog : Window
{
    private readonly List<CleanAskRow> _rows = new();

    /// <summary>The pieces the user ticked (valid when the dialog returned true).</summary>
    public HashSet<string> Chosen { get; } = new();

    /// <param name="groups">One per category: its title, its key ("CapCut", "AiCoding"), what was found and what is ticked now.</param>
    public CleanAskDialog(IEnumerable<(string Title, string CategoryKey, IReadOnlyList<CleanPart> Parts, IReadOnlySet<string> Ticked)> groups)
    {
        InitializeComponent();

        var list = new List<CleanAskGroup>();
        foreach (var (title, key, parts, ticked) in groups)
        {
            var rows = parts.Select(p => new CleanAskRow(p, ticked.Contains(p.Key))).ToList();
            foreach (var row in rows) row.PropertyChanged += (s, e) => UpdateTotal();
            _rows.AddRange(rows);
            list.Add(new CleanAskGroup(title, Loc.T($"CleanAsk_Kept_{key}", ""), rows));
        }
        GroupList.ItemsSource = list;
        UpdateTotal();
    }

    private void UpdateTotal()
    {
        long bytes = _rows.Where(r => r.IsChecked).Sum(r => r.Part.Bytes);
        TotalText.Text = Loc.F("CleanAsk_Total", "Ticked: {0}", CleanerView.FormatSize(bytes));
    }

    private void Include_Click(object sender, RoutedEventArgs e)
    {
        Chosen.Clear();
        foreach (var row in _rows.Where(r => r.IsChecked)) Chosen.Add(row.Part.Key);
        DialogResult = true;
    }

    private void Skip_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) DialogResult = false;
    }

    private void Title_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}

public sealed record CleanAskGroup(string Title, string Kept, List<CleanAskRow> Rows);

/// <summary>One program + kind of file in the dialog.</summary>
public sealed class CleanAskRow : INotifyPropertyChanged
{
    public CleanAskRow(CleanPart part, bool ticked)
    {
        Part = part;
        // Nothing of it can be removed right now (the program is running): shown, but not tickable.
        CanTick = part.FileCount > 0;
        _isChecked = ticked && CanTick;

        Title = $"{part.App} — {Loc.T($"CleanPart_{part.Kind}", part.Kind)}";
        bool warn = part.Kind is CleanPartKind.History or CleanPartKind.Models;
        Note = CanTick ? Loc.T($"CleanPart_{part.Kind}_Desc", "") : Loc.T("CleanAsk_InUse", "in use now — close the program first");
        NoteBrush = (Application.Current?.TryFindResource(warn || !CanTick ? "WarningBrush" : "TextTertiaryBrush") as Brush) ?? Brushes.Gray;
        SizeText = CleanerView.FormatSize(CanTick ? part.Bytes : part.InUseBytes);
    }

    public CleanPart Part { get; }
    public string Title { get; }
    public string Note { get; }
    public Brush NoteBrush { get; }
    public string SizeText { get; }
    public bool CanTick { get; }

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
