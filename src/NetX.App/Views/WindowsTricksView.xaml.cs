using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NetX.App.Dialogs;
using NetX.Core.Helpers;
using NetX.Core.System;
using NetX.Core.System.Tweaks;

namespace NetX.App.Views;

/// <summary>
/// Windows Tricks. Every card shows this PC's real state (detected off the UI
/// thread when the page loads and after every action), applies itself
/// in-process with a verified result, and restores the true Windows default.
/// Texts come from the bilingual catalog (Thai when the Thai language
/// dictionary is active; the page is recreated on each navigation).
/// </summary>
public partial class WindowsTricksView : Page
{
    private const string AllCategory = "all";

    private enum ActionKind { Apply, Revert, Run }

    private sealed class CardView
    {
        public CardView(TrickDefinition trick) => Trick = trick;

        public TrickDefinition Trick { get; }
        public Border Root { get; set; } = null!;
        public FrameworkElement StateRow { get; set; } = null!;
        public Ellipse StateDot { get; set; } = null!;
        public TextBlock StateText { get; set; } = null!;
        public TextBlock NoteText { get; set; } = null!;
        public TextBlock ResultText { get; set; } = null!;
        public Button? Apply { get; set; }
        public Button? Revert { get; set; }
        public Button? Run { get; set; }
        public TrickStatus? Status { get; set; }
        public bool Checking { get; set; }
        public string SearchText { get; set; } = "";
    }

    private readonly bool _thai;
    private readonly List<CardView> _cards = new();
    private readonly List<(string Category, TextBlock Header, WrapPanel Panel)> _groups = new();
    private List<TrickDefinition> _tricks = new();
    private string _selectedCategory = AllCategory;
    private bool _busy;
    private bool _built;
    private RestartScope _pendingRestart = RestartScope.None;
    private CancellationTokenSource? _detectCts;

    public WindowsTricksView()
    {
        InitializeComponent();
        _thai = IsThaiUi();

        TitleText.Text = TryFindResource("Nav_Tricks") as string ?? T("Windows Tricks", "เทคนิค Windows");
        SubtitleText.Text = T(
            "Every trick checks this PC's real state and can be undone with \"Restore default\".",
            "ทุกเทคนิคตรวจสถานะจริงของเครื่องนี้ และย้อนกลับได้ด้วยปุ่ม \"คืนค่าเริ่มต้น\"");
        SearchPlaceholder.Text = T("Search tricks…", "ค้นหาเทคนิค…");
        LoadingText.Text = T("Checking this PC…", "กำลังตรวจสอบเครื่องนี้…");
        EmptyText.Text = T("No tricks match your search.", "ไม่พบเทคนิคที่ตรงกับคำค้นหา");
        RestartBannerClose.ToolTip = T("Hide", "ซ่อน");

        Loaded += OnLoaded;
        Unloaded += (_, _) => _detectCts?.Cancel();
    }

    private string T(string en, string th) => _thai ? th : en;

    private static bool IsThaiUi() =>
        Application.Current?.Resources.MergedDictionaries.Any(d =>
            d.Source?.OriginalString.Contains("th-TH", StringComparison.OrdinalIgnoreCase) == true) == true;

    private Brush Res(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    private Geometry? Icon(string key) => TryFindResource(key) as Geometry ?? TryFindResource("InfoIcon") as Geometry;

    #region Loading

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_built)
        {
            // Back-navigation to a kept-alive page: just re-check the states.
            await RefreshStatesAsync();
            return;
        }
        _built = true;

        try
        {
            var os = WindowsVersionInfo.Current;
            _tricks = await Task.Run(() => TrickCatalog.Build(os));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Windows Tricks: catalog failed: {ex}");
            LoadingText.Text = T("The tricks could not be loaded. Please open this page again.",
                                 "โหลดรายการเทคนิคไม่สำเร็จ กรุณาเปิดหน้านี้อีกครั้ง");
            return;
        }

        LoadingText.Visibility = Visibility.Collapsed;
        BuildCategoryTabs();
        BuildCards();
        ApplyFilter();
        await CheckEnvironmentAsync();
        await RefreshStatesAsync();
    }

    private async Task CheckEnvironmentAsync()
    {
        string? text = null;
        if (!AdminHelper.IsRunAsAdmin())
        {
            text = T("WinXTools isn't running as Administrator, so most tricks can't change anything. Restart WinXTools as Administrator.",
                     "WinXTools ไม่ได้ทำงานแบบผู้ดูแลระบบ เทคนิคส่วนใหญ่จึงเปลี่ยนค่าไม่ได้ กรุณาเปิด WinXTools แบบ Administrator");
        }
        else
        {
            var (otherUser, desktopUser) = await Task.Run(() =>
            {
                bool other = SessionInfo.IsElevatedAsOtherUser(out var user);
                return (other, user);
            });
            if (otherUser)
            {
                text = T($"WinXTools runs under a different account than the one signed in to this desktop ({desktopUser}). " +
                         "Personal settings (Explorer, look, privacy) will be changed for the administrator account instead.",
                         $"WinXTools ทำงานด้วยบัญชีคนละบัญชีกับผู้ที่ลงชื่อเข้าใช้เดสก์ท็อปนี้ ({desktopUser}) " +
                         "การตั้งค่าส่วนตัว (Explorer หน้าตา ความเป็นส่วนตัว) จะถูกเปลี่ยนในบัญชีผู้ดูแลระบบแทน");
            }
        }

        if (text == null) return;
        EnvironmentBannerText.Text = text;
        EnvironmentBanner.Visibility = Visibility.Visible;
    }

    #endregion

    #region Tabs, cards and search

    private void BuildCategoryTabs()
    {
        CategoryTabs.Children.Clear();
        AddTab(AllCategory, T("All", "ทั้งหมด"));
        foreach (var (id, name) in TrickCatalog.Categories)
        {
            if (_tricks.Any(t => t.Category == id)) AddTab(id, name.Get(_thai));
        }
        UpdateTabStyles();
    }

    private void AddTab(string id, string text)
    {
        var tab = new Button { Content = text, Tag = id, Style = (Style)FindResource("TrickTabButtonStyle") };
        tab.Click += (_, _) =>
        {
            _selectedCategory = id;
            UpdateTabStyles();
            ApplyFilter();
        };
        CategoryTabs.Children.Add(tab);
    }

    private void UpdateTabStyles()
    {
        foreach (var tab in CategoryTabs.Children.OfType<Button>())
        {
            bool selected = (string)tab.Tag == _selectedCategory;
            tab.Background = selected ? Res("AccentPrimaryBrush") : Brushes.Transparent;
            tab.Foreground = selected ? Brushes.White : Res("TextSecondaryBrush");
        }
    }

    private void BuildCards()
    {
        TricksContainer.Children.Clear();
        _groups.Clear();
        _cards.Clear();

        foreach (var (id, name) in TrickCatalog.Categories)
        {
            var tricks = _tricks.Where(t => t.Category == id).ToList();
            if (tricks.Count == 0) continue;

            var header = new TextBlock
            {
                Text = name.Get(_thai),
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = Res("AccentPrimaryBrush"),
                Margin = new Thickness(0, 16, 0, 12)
            };
            var panel = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var trick in tricks)
            {
                var card = CreateCard(trick, name);
                _cards.Add(card);
                panel.Children.Add(card.Root);
            }

            TricksContainer.Children.Add(header);
            TricksContainer.Children.Add(panel);
            _groups.Add((id, header, panel));
        }
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim();
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        int visible = 0;
        foreach (var (category, header, panel) in _groups)
        {
            bool inTab = _selectedCategory == AllCategory || _selectedCategory == category;
            int shown = 0;
            foreach (var root in panel.Children.OfType<Border>())
            {
                var card = (CardView)root.Tag;
                bool match = inTab && (query.Length == 0 ||
                                       card.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase));
                root.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                if (match) shown++;
            }
            header.Visibility = panel.Visibility = shown > 0 ? Visibility.Visible : Visibility.Collapsed;
            visible += shown;
        }

        EmptyText.Visibility = _built && _tricks.Count > 0 && visible == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        SearchBox.Clear();
        e.Handled = true;
    }

    #endregion

    #region Card

    private CardView CreateCard(TrickDefinition t, LocText categoryName)
    {
        var card = new CardView(t);
        var root = new Border
        {
            Background = Res("BgSecondaryBrush"),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 12, 12),
            Width = 380,
            MinHeight = 150,
            Tag = card
        };
        var stack = new StackPanel();

        // Header: icon, name, risk dot
        var header = new Grid { Background = Brushes.Transparent, ToolTip = BuildTooltip(t) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Path
        {
            Data = Icon(t.Icon),
            Fill = Res("AccentPrimaryBrush"),
            Width = 20,
            Height = 20,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        header.Children.Add(icon);

        var name = new TextBlock
        {
            Text = t.Name.Get(_thai),
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Foreground = Res("TextPrimaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(name, 1);
        header.Children.Add(name);

        var riskDot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = RiskBrush(t.Risk),
            ToolTip = RiskText(t.Risk),
            Margin = new Thickness(8, 6, 0, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetColumn(riskDot, 2);
        header.Children.Add(riskDot);
        stack.Children.Add(header);

        // State (filled in by RenderState)
        var stateRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(30, 6, 0, 0) };
        card.StateDot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        card.StateText = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 300 };
        stateRow.Children.Add(card.StateDot);
        stateRow.Children.Add(card.StateText);
        card.StateRow = stateRow;
        stateRow.Visibility = t.Detect != null ? Visibility.Visible : Visibility.Collapsed;
        stack.Children.Add(stateRow);

        card.NoteText = new TextBlock
        {
            FontSize = 11,
            Foreground = Res("TextTertiaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(30, 2, 0, 0),
            Visibility = Visibility.Collapsed
        };
        stack.Children.Add(card.NoteText);

        stack.Children.Add(new TextBlock
        {
            Text = t.Description.Get(_thai),
            FontSize = 12,
            Foreground = Res("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0)
        });

        if (t.Warning != null)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "⚠ " + t.Warning.Get(_thai),
                FontSize = 11,
                Foreground = Res(t.Risk == TrickRisk.Dangerous ? "DangerBrush" : "WarningBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            });
        }

        if (t.Tip != null)
        {
            stack.Children.Add(new TextBlock
            {
                Text = T("Tip: ", "เคล็ดลับ: ") + t.Tip.Get(_thai),
                FontSize = 11,
                Foreground = Res("TextTertiaryBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            });
        }

        if (t.Restart != RestartScope.None)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "↻ " + RestartHint(t.Restart),
                FontSize = 11,
                Foreground = Res("InfoBrush"),
                Margin = new Thickness(0, 6, 0, 0)
            });
        }

        if (!string.IsNullOrWhiteSpace(t.Technical))
        {
            var firstLine = t.Technical.Split('\n')[0];
            var technical = new Border
            {
                Background = Res("BgTertiaryBrush"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 10, 0, 0),
                ToolTip = t.Technical
            };
            technical.Child = new TextBlock
            {
                Text = t.Technical.Contains('\n') ? firstLine + " …" : firstLine,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                Foreground = Res("TextTertiaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            stack.Children.Add(technical);
        }

        card.ResultText = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed
        };
        stack.Children.Add(card.ResultText);

        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        AddButtons(card, buttons);
        stack.Children.Add(buttons);

        root.Child = stack;
        card.Root = root;
        card.SearchText = string.Join("\n",
            t.Name.En, t.Name.Th, t.Description.En, t.Description.Th, t.Technical ?? "",
            categoryName.En, categoryName.Th);

        RenderState(card);
        return card;
    }

    private void AddButtons(CardView card, WrapPanel panel)
    {
        var t = card.Trick;
        bool gamerMode = t.Id == "gamer-mode";

        if (t.Apply != null)
        {
            var label = t.ApplyLabel?.Get(_thai)
                        ?? (gamerMode ? TryFindResource("GameMode_Apply") as string : null)
                        ?? T("Apply", "ใช้งาน");
            card.Apply = CardButton(label, "ToggleOnIcon", primary: true, () => Execute(card, ActionKind.Apply));
            panel.Children.Add(card.Apply);
        }

        if (t.Revert != null)
        {
            var label = t.RevertLabel?.Get(_thai)
                        ?? (gamerMode ? TryFindResource("GameMode_Revert") as string : null)
                        ?? T("Restore default", "คืนค่าเริ่มต้น");
            card.Revert = CardButton(label, "RestoreIcon", primary: false, () => Execute(card, ActionKind.Revert));
            panel.Children.Add(card.Revert);
        }

        if (t.Run != null)
        {
            card.Run = CardButton(t.RunLabel?.Get(_thai) ?? T("Run", "เรียกใช้"), "RunIcon", primary: true,
                () => Execute(card, ActionKind.Run));
            panel.Children.Add(card.Run);
        }

        if (t.Open != null)
        {
            panel.Children.Add(CardButton(t.OpenLabel?.Get(_thai) ?? T("Open", "เปิด"), "OpenIcon", primary: true,
                () => OpenTool(card)));
        }

        if (t.NavigateTo != null)
        {
            panel.Children.Add(CardButton(T("Open WinXTools Cleaner", "เปิดหน้า Cleaner"), "OpenIcon", primary: true,
                () => NavigateToPage(t.NavigateTo)));
        }

        if (t.SettingsUri != null)
        {
            panel.Children.Add(CardButton(t.SettingsLabel?.Get(_thai) ?? T("Open Settings", "เปิดการตั้งค่า"), "SettingsIcon",
                primary: t.Apply == null && t.Run == null && t.Open == null && t.NavigateTo == null,
                () => OpenSettings(card)));
        }

        if (t.CopyText != null)
        {
            panel.Children.Add(CardButton(T("Copy command", "คัดลอกคำสั่ง"), "CopyIcon", primary: false,
                () => CopyCommand(card)));
        }
    }

    private Button CardButton(string text, string iconKey, bool primary, Action onClick)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new Path
        {
            Data = Icon(iconKey),
            Fill = Res(primary ? "AccentLightBrush" : "TextSecondaryBrush"),
            Width = 12,
            Height = 12,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        content.Children.Add(new TextBlock { Text = text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });

        var button = new Button
        {
            Content = content,
            Style = (Style)FindResource(primary ? "TrickCardPrimaryButtonStyle" : "TrickCardButtonStyle")
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private object BuildTooltip(TrickDefinition t)
    {
        var sb = new StringBuilder();
        sb.AppendLine(t.Name.Get(_thai)).AppendLine();
        sb.AppendLine(t.Description.Get(_thai));
        if (t.Warning != null) sb.AppendLine().AppendLine("⚠ " + t.Warning.Get(_thai));
        if (t.Tip != null) sb.AppendLine().AppendLine(T("Tip: ", "เคล็ดลับ: ") + t.Tip.Get(_thai));
        sb.AppendLine().Append(T("Risk: ", "ความเสี่ยง: ")).AppendLine(RiskText(t.Risk));
        if (t.Restart != RestartScope.None) sb.AppendLine("↻ " + RestartHint(t.Restart));
        if (!string.IsNullOrWhiteSpace(t.Technical)) sb.AppendLine().Append(t.Technical);

        return new ToolTip
        {
            Content = new TextBlock { Text = sb.ToString().TrimEnd(), TextWrapping = TextWrapping.Wrap },
            Background = Res("BgTertiaryBrush"),
            Foreground = Res("TextPrimaryBrush"),
            BorderBrush = Res("BorderBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            MaxWidth = 440
        };
    }

    private Brush RiskBrush(TrickRisk risk) => risk switch
    {
        TrickRisk.Safe => Res("SuccessBrush"),
        TrickRisk.Moderate => Res("WarningBrush"),
        _ => Res("DangerBrush")
    };

    private string RiskText(TrickRisk risk) => risk switch
    {
        TrickRisk.Safe => T("Safe", "ปลอดภัย"),
        TrickRisk.Moderate => T("Use with care", "ควรระวัง"),
        _ => T("Risky — read the warning first", "มีความเสี่ยง — อ่านคำเตือนก่อน")
    };

    private string RestartHint(RestartScope scope) => scope switch
    {
        RestartScope.Explorer => T("Explorer restart needed to see it", "ต้องรีสตาร์ท Explorer จึงจะเห็นผล"),
        RestartScope.SignOut => T("Sign out and back in to apply", "ต้องออกจากระบบแล้วเข้าใหม่จึงจะมีผล"),
        RestartScope.Reboot => T("PC restart needed to apply", "ต้องรีสตาร์ทเครื่องจึงจะมีผล"),
        _ => ""
    };

    private void RenderState(CardView card)
    {
        var t = card.Trick;
        if (t.Detect != null)
        {
            bool isToggle = t.Apply != null || t.Revert != null;
            if (card.Status == null || card.Checking)
            {
                card.StateDot.Fill = Res("TextDisabledBrush");
                card.StateText.Text = T("Checking…", "กำลังตรวจสอบ…");
                card.StateText.Foreground = Res("TextTertiaryBrush");
                if (card.Status == null) card.NoteText.Visibility = Visibility.Collapsed;
            }
            else if (!isToggle)
            {
                // Read-only status (e.g. Widgets, Memory integrity): show just the note.
                card.StateDot.Fill = Res("InfoBrush");
                card.StateText.Text = card.Status.Note?.Get(_thai) ?? StateLabel(card.Status.State);
                card.StateText.Foreground = Res("TextSecondaryBrush");
                card.NoteText.Visibility = Visibility.Collapsed;
            }
            else
            {
                var (label, brush) = card.Status.State switch
                {
                    TrickState.Applied => ("✓ " + (t.AppliedLabel?.Get(_thai) ?? StateLabel(TrickState.Applied)), "SuccessBrush"),
                    TrickState.Default => (t.DefaultLabel?.Get(_thai) ?? StateLabel(TrickState.Default), "TextTertiaryBrush"),
                    TrickState.Custom => (t.CustomLabel?.Get(_thai) ?? StateLabel(TrickState.Custom), "WarningBrush"),
                    TrickState.NotSupported => (StateLabel(TrickState.NotSupported), "TextDisabledBrush"),
                    _ => (StateLabel(TrickState.Unknown), "TextTertiaryBrush")
                };
                card.StateDot.Fill = Res(brush == "TextTertiaryBrush" ? "SteelMidBrush" : brush);
                card.StateText.Text = label;
                card.StateText.Foreground = Res(brush);

                var note = card.Status.Note?.Get(_thai);
                card.NoteText.Text = note ?? "";
                card.NoteText.Visibility = string.IsNullOrEmpty(note) ? Visibility.Collapsed : Visibility.Visible;
            }
        }
        UpdateButtons(card);
    }

    private string StateLabel(TrickState state) => state switch
    {
        TrickState.Applied => T("Applied", "ปรับแต่งแล้ว"),
        TrickState.Default => T("Windows default", "ค่าเริ่มต้นของ Windows"),
        TrickState.Custom => T("Partly applied / other value", "ปรับแต่งบางส่วน / เป็นค่าอื่น"),
        TrickState.NotSupported => T("Not available on this PC", "ไม่รองรับบนเครื่องนี้"),
        _ => T("State unknown", "ตรวจสอบสถานะไม่ได้")
    };

    private void UpdateButtons(CardView card)
    {
        var t = card.Trick;
        var state = card.Status?.State;
        bool known = t.Detect == null || (card.Status != null && !card.Checking);
        bool idle = !_busy;

        if (card.Apply != null)
            card.Apply.IsEnabled = idle && known && state != TrickState.Applied && state != TrickState.NotSupported;
        if (card.Revert != null)
            card.Revert.IsEnabled = idle && known && state != TrickState.Default && state != TrickState.NotSupported;
        if (card.Run != null)
            card.Run.IsEnabled = idle && state != TrickState.NotSupported;
    }

    private void UpdateAllButtons()
    {
        foreach (var card in _cards) UpdateButtons(card);
        RestartBannerButton.IsEnabled = !_busy;
    }

    private void SetResult(CardView card, string text, string brushKey)
    {
        card.ResultText.Text = text;
        card.ResultText.Foreground = Res(brushKey);
        card.ResultText.Visibility = Visibility.Visible;
    }

    #endregion

    #region State detection

    /// <summary>
    /// Runs every Detect() off the UI thread (a few at a time) and updates the
    /// badges. Only the first load shows "Checking…" everywhere; later refreshes
    /// keep the current badges (no flashing) and mark just the card that changed.
    /// </summary>
    private async Task RefreshStatesAsync(CardView? changed = null)
    {
        _detectCts?.Cancel();
        var cts = new CancellationTokenSource();
        _detectCts = cts;
        var token = cts.Token;

        var targets = _cards.Where(c => c.Trick.Detect != null).ToList();
        foreach (var card in targets)
        {
            if (card.Status != null && card != changed) continue;
            card.Checking = true;
            RenderState(card);
        }

        var gate = new SemaphoreSlim(4);
        var tasks = targets.Select(async card =>
        {
            await gate.WaitAsync();
            try
            {
                if (token.IsCancellationRequested) return;
                var status = await Task.Run(() => SafeDetect(card.Trick));
                if (token.IsCancellationRequested) return;
                card.Status = status;
                card.Checking = false;
                RenderState(card);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks);
    }

    private static TrickStatus SafeDetect(TrickDefinition trick)
    {
        try
        {
            return trick.Detect!();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Detect '{trick.Id}' failed: {ex.Message}");
            return TrickStatus.Unknown();
        }
    }

    #endregion

    #region Actions

    private async void Execute(CardView card, ActionKind kind)
    {
        if (_busy) return;
        var t = card.Trick;
        var work = kind switch
        {
            ActionKind.Apply => t.Apply,
            ActionKind.Revert => t.Revert,
            _ => t.Run
        };
        if (work == null) return;

        var owner = Window.GetWindow(this);
        string? input = null;
        if (kind == ActionKind.Run && t.InputPrompt != null)
        {
            var prompt = new InputDialog(t.Name.Get(_thai), t.InputPrompt.Get(_thai), t.InputDefault ?? "") { Owner = owner };
            if (prompt.ShowDialog() != true) return;
            input = prompt.ResponseText;
        }

        if (!Confirm(t, kind)) return;

        _busy = true;
        SetResult(card, T("Working…", "กำลังดำเนินการ…"), "TextSecondaryBrush");
        UpdateAllButtons();

        TrickResult result;
        try
        {
            if (t.ShowsOutput)
            {
                var dialog = new CommandOutputDialog(t.Name.Get(_thai), work, t.Cancellable, _thai, input) { Owner = owner };
                dialog.ShowDialog();
                result = dialog.Result ?? TrickResult.Canceled();
            }
            else
            {
                var context = new TrickRunContext(_thai, CancellationToken.None, input);
                result = await Task.Run(() => work(context));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Trick '{t.Id}' failed: {ex}");
            result = TrickResult.Fail(TweakErrors.Friendly(ex));
        }
        finally
        {
            _busy = false;
        }

        ShowResult(card, kind, result, shownInDialog: t.ShowsOutput);

        if (result.Success && !string.IsNullOrEmpty(result.OpenAfter))
        {
            var open = ShellLauncher.OpenUnelevated(result.OpenAfter);
            if (!open.Success && open.Message != null)
                ShowMessage(open.Message.Get(_thai), t.Name.Get(_thai), MessageBoxImage.Warning);
        }

        var restart = result.Restart;
        if (result.Success && kind != ActionKind.Run && t.Restart > restart) restart = t.Restart;
        if (restart != RestartScope.None && !result.Cancelled) NoteRestart(restart);

        UpdateAllButtons();
        await RefreshStatesAsync(card);
    }

    /// <summary>Safe → no question; Use with care → Yes/No; Risky → warning with "No" as default.</summary>
    private bool Confirm(TrickDefinition t, ActionKind kind)
    {
        if (t.Risk == TrickRisk.Safe) return true;
        var name = t.Name.Get(_thai);

        if (kind == ActionKind.Revert)
        {
            return Ask(T($"Restore the Windows default for \"{name}\"?", $"คืนค่าเริ่มต้นของ Windows สำหรับ \"{name}\" หรือไม่?"),
                name, MessageBoxImage.Question, MessageBoxResult.Yes);
        }

        bool risky = t.Risk == TrickRisk.Dangerous;
        var sb = new StringBuilder();
        if (risky)
            sb.AppendLine(T("This change can cause problems — please read carefully.", "การเปลี่ยนแปลงนี้อาจทำให้เกิดปัญหา — กรุณาอ่านให้ละเอียด")).AppendLine();
        sb.AppendLine(name).AppendLine();
        sb.AppendLine(t.Description.Get(_thai));
        if (t.Warning != null) sb.AppendLine().AppendLine("⚠ " + t.Warning.Get(_thai));
        sb.AppendLine().Append(risky
            ? T("Are you sure you want to continue?", "แน่ใจหรือไม่ว่าจะดำเนินการต่อ?")
            : T("Continue?", "ดำเนินการต่อหรือไม่?"));

        return Ask(sb.ToString(), name,
            risky ? MessageBoxImage.Warning : MessageBoxImage.Question,
            risky ? MessageBoxResult.No : MessageBoxResult.Yes);
    }

    private void ShowResult(CardView card, ActionKind kind, TrickResult result, bool shownInDialog)
    {
        var caption = card.Trick.Name.Get(_thai);
        var message = result.Message?.Get(_thai)
                      ?? (result.Success ? T("Done.", "เสร็จแล้ว") : T("It didn't work.", "ไม่สำเร็จ"));

        if (result.Cancelled)
        {
            SetResult(card, "■ " + T("Cancelled.", "ยกเลิกแล้ว"), "WarningBrush");
            return;
        }

        if (result.Success)
        {
            SetResult(card, "✓ " + FirstLine(message), "SuccessBrush");
            // A Run's message is the answer the user asked for (Secure Boot state, timer set…);
            // multi-line summaries (Gamer Mode) are worth showing in full too.
            if (!shownInDialog && (kind == ActionKind.Run || message.Contains('\n')))
                ShowMessage(message, caption, MessageBoxImage.Information);
            return;
        }

        SetResult(card, "✗ " + FirstLine(message), "DangerBrush");
        if (!shownInDialog)
        {
            var details = string.IsNullOrWhiteSpace(result.Output) ? "" : "\n\n" + Truncate(result.Output.Trim(), 1500);
            ShowMessage(message + details, caption,
                result.Restart != RestartScope.None ? MessageBoxImage.Warning : MessageBoxImage.Error);
        }
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n')[0].Trim();
        return Truncate(line, 180);
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + " …";

    private async void OpenTool(CardView card)
    {
        var open = card.Trick.Open!;
        TrickResult result;
        try
        {
            result = await Task.Run(open);
        }
        catch (Exception ex)
        {
            result = TrickResult.Fail(TweakErrors.Friendly(ex));
        }
        if (!result.Success)
            ShowMessage(result.Message?.Get(_thai) ?? T("It couldn't be opened.", "เปิดไม่ได้"), card.Trick.Name.Get(_thai), MessageBoxImage.Error);
    }

    private void OpenSettings(CardView card)
    {
        var result = ShellLauncher.OpenUnelevated(card.Trick.SettingsUri!);
        if (!result.Success)
            ShowMessage(result.Message?.Get(_thai) ?? T("It couldn't be opened.", "เปิดไม่ได้"), card.Trick.Name.Get(_thai), MessageBoxImage.Error);
    }

    private void CopyCommand(CardView card)
    {
        if (CommandOutputDialog.TryCopyToClipboard(card.Trick.CopyText!))
            SetResult(card, "✓ " + T("Command copied — paste it into an administrator terminal.", "คัดลอกคำสั่งแล้ว — นำไปวางในเทอร์มินัลแบบผู้ดูแลระบบได้"), "SuccessBrush");
        else
            ShowMessage(T("The clipboard is busy (another app is using it). Please try again.", "คลิปบอร์ดกำลังถูกใช้งานโดยแอปอื่น กรุณาลองใหม่อีกครั้ง"),
                card.Trick.Name.Get(_thai), MessageBoxImage.Warning);
    }

    /// <summary>Clicks the matching sidebar button so MainWindow updates its title and active item as usual.</summary>
    private void NavigateToPage(string tag)
    {
        var window = Window.GetWindow(this);
        var navButton = window == null ? null : FindNavButton(window, tag);
        if (navButton != null)
        {
            navButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, navButton));
            return;
        }
        ShowMessage(T("Open \"Cleaner\" from the menu on the left.", "เปิดหน้า \"Cleaner\" จากเมนูด้านซ้าย"),
            TitleText.Text, MessageBoxImage.Information);
    }

    private static Button? FindNavButton(DependencyObject root, string tag)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is Button b && b.Tag as string == tag && b.Name.StartsWith("Nav", StringComparison.Ordinal))
                return b;
            var found = FindNavButton(child, tag);
            if (found != null) return found;
        }
        return null;
    }

    #endregion

    #region Restart banner

    private void NoteRestart(RestartScope scope)
    {
        if (scope > _pendingRestart) _pendingRestart = scope;
        RenderRestartBanner();
    }

    private void RenderRestartBanner()
    {
        switch (_pendingRestart)
        {
            case RestartScope.Explorer:
                RestartBannerText.Text = T("Restart Explorer to see the change.", "รีสตาร์ท Explorer เพื่อให้เห็นการเปลี่ยนแปลง");
                RestartBannerButton.Content = T("Restart Explorer now", "รีสตาร์ท Explorer ตอนนี้");
                RestartBannerButton.Visibility = Visibility.Visible;
                break;
            case RestartScope.SignOut:
                RestartBannerText.Text = T("Sign out and back in to finish the change.", "ออกจากระบบแล้วเข้าสู่ระบบใหม่เพื่อให้การเปลี่ยนแปลงมีผล");
                RestartBannerButton.Visibility = Visibility.Collapsed;
                break;
            case RestartScope.Reboot:
                RestartBannerText.Text = T("Restart the PC to finish the change.", "รีสตาร์ทเครื่องเพื่อให้การเปลี่ยนแปลงมีผล");
                RestartBannerButton.Content = T("Restart now", "รีสตาร์ทตอนนี้");
                RestartBannerButton.Visibility = Visibility.Visible;
                break;
            default:
                RestartBanner.Visibility = Visibility.Collapsed;
                return;
        }
        RestartBanner.Visibility = Visibility.Visible;
    }

    private async void RestartBannerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (_pendingRestart == RestartScope.Explorer)
        {
            if (!Ask(T("Restart Explorer now? Open File Explorer windows will close — wait for file copies to finish first.",
                       "รีสตาร์ท Explorer ตอนนี้หรือไม่? หน้าต่าง File Explorer ที่เปิดอยู่จะถูกปิด ควรรอให้การคัดลอกไฟล์เสร็จก่อน"),
                    T("Restart Explorer", "รีสตาร์ท Explorer"), MessageBoxImage.Question, MessageBoxResult.Yes))
                return;

            _busy = true;
            UpdateAllButtons();
            TrickResult result;
            try
            {
                result = await Task.Run(ExplorerRestarter.Restart);
            }
            finally
            {
                _busy = false;
                UpdateAllButtons();
            }

            if (result.Success)
            {
                _pendingRestart = RestartScope.None;
                RenderRestartBanner();
                await RefreshStatesAsync();
            }
            else
            {
                ShowMessage(result.Message?.Get(_thai) ?? T("Explorer couldn't be restarted.", "รีสตาร์ท Explorer ไม่สำเร็จ"),
                    T("Restart Explorer", "รีสตาร์ท Explorer"), MessageBoxImage.Warning);
            }
        }
        else if (_pendingRestart == RestartScope.Reboot)
        {
            if (!Ask(T("Restart the PC now? Save your work first — open apps will be asked to close.",
                       "รีสตาร์ทเครื่องตอนนี้หรือไม่? กรุณาบันทึกงานก่อน — แอปที่เปิดอยู่จะถูกขอให้ปิด"),
                    T("Restart", "รีสตาร์ท"), MessageBoxImage.Warning, MessageBoxResult.No))
                return;

            // "/t 0" without "/f": apps with unsaved work may still ask to save.
            var r = await CommandRunner.RunToolAsync("shutdown.exe", "/r /t 0", TimeSpan.FromSeconds(30));
            if (!r.Succeeded)
                ShowMessage(T("Windows didn't start the restart. Please restart from the Start menu.",
                              "Windows ไม่ได้เริ่มรีสตาร์ท กรุณารีสตาร์ทจากเมนูเริ่ม"),
                    T("Restart", "รีสตาร์ท"), MessageBoxImage.Warning);
        }
    }

    private void RestartBannerClose_Click(object sender, RoutedEventArgs e) =>
        RestartBanner.Visibility = Visibility.Collapsed;

    #endregion

    #region Message boxes

    private bool Ask(string text, string caption, MessageBoxImage icon, MessageBoxResult defaultResult)
    {
        var owner = Window.GetWindow(this);
        var answer = owner != null
            ? MessageBox.Show(owner, text, caption, MessageBoxButton.YesNo, icon, defaultResult)
            : MessageBox.Show(text, caption, MessageBoxButton.YesNo, icon, defaultResult);
        return answer == MessageBoxResult.Yes;
    }

    private void ShowMessage(string text, string caption, MessageBoxImage icon)
    {
        var owner = Window.GetWindow(this);
        if (owner != null) MessageBox.Show(owner, text, caption, MessageBoxButton.OK, icon);
        else MessageBox.Show(text, caption, MessageBoxButton.OK, icon);
    }

    #endregion
}
