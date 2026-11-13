using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using NetX.App.Helpers;
using NetX.App.Views;
using NetX.Core.System.Background;

namespace NetX.App.Dialogs;

/// <summary>
/// "These would be switched off — untick what you want to keep." Used before the
/// recommended clean-up and before the game profile; nothing is switched until
/// the user confirms here.
/// </summary>
public partial class StartupPlanDialog : Window
{
    private readonly List<StartupPlanRow> _rows = new();

    /// <summary>The items the user left ticked (valid when the dialog returned true).</summary>
    public List<StartupItem> Chosen { get; } = new();

    public StartupPlanDialog(string title, string intro, string applyLabel, IEnumerable<(StartupItem Item, bool Ticked)> items, bool thai)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        IntroText.Text = intro;
        ApplyButton.Content = applyLabel;

        foreach (var (item, ticked) in items)
        {
            var row = new StartupPlanRow(item, ticked, thai);
            row.PropertyChanged += (s, e) => UpdateTotal();
            _rows.Add(row);
        }
        GroupList.ItemsSource = _rows.GroupBy(r => r.Item.Group)
            .Select(g => new StartupPlanGroup(Loc.T($"Startup_Group_{g.Key}", g.Key.ToString()), g.ToList()))
            .ToList();
        UpdateTotal();
    }

    private void UpdateTotal()
    {
        var ticked = _rows.Where(r => r.IsChecked).ToList();
        long bytes = ticked.Sum(r => r.Item.MemoryBytes);
        TotalText.Text = bytes > 0
            ? Loc.F("Startup_PlanTotalMemory", "Ticked: {0:N0} of {1:N0} — they hold {2} right now", ticked.Count, _rows.Count, CleanerView.FormatSize(bytes))
            : Loc.F("Startup_PlanTotal", "Ticked: {0:N0} of {1:N0}", ticked.Count, _rows.Count);
        ApplyButton.IsEnabled = ticked.Count > 0;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        Chosen.Clear();
        Chosen.AddRange(_rows.Where(r => r.IsChecked).Select(r => r.Item));
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) DialogResult = false;
    }

    private void Title_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}

public sealed record StartupPlanGroup(string Title, List<StartupPlanRow> Rows);

/// <summary>One thing the plan would switch off.</summary>
public sealed class StartupPlanRow : INotifyPropertyChanged
{
    public StartupPlanRow(StartupItem item, bool ticked, bool thai)
    {
        Item = item;
        _isChecked = ticked;
        Name = item.Name;
        Note = item.What.Get(thai);
        MemoryText = item.ProcessCount > 0 ? CleanerView.FormatSize(item.MemoryBytes) : "";
    }

    public StartupItem Item { get; }
    public string Name { get; }
    public string Note { get; }
    public string MemoryText { get; }

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
