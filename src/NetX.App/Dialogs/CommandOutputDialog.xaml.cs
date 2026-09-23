using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NetX.Core.System.Tweaks;

namespace NetX.App.Dialogs;

/// <summary>
/// Runs a trick action and shows its live console output inside the app
/// (read-only text + Copy) instead of a console window that closes instantly.
/// Long actions can be cancelled; actions that must not be interrupted keep
/// the dialog open until they finish.
/// </summary>
public partial class CommandOutputDialog : Window
{
    private readonly Func<TrickRunContext, Task<TrickResult>> _work;
    private readonly CancellationTokenSource _cts = new();
    private readonly TrickRunContext _context;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _elapsed = new();
    private readonly bool _cancellable;
    private readonly bool _thai;
    private int _shownVersion = -1;
    private bool _running;
    private bool _closeWhenDone;

    /// <summary>Outcome of the action (null only if the dialog never ran it).</summary>
    public TrickResult? Result { get; private set; }

    public CommandOutputDialog(string title, Func<TrickRunContext, Task<TrickResult>> work,
        bool cancellable, bool thai, string? input = null)
    {
        InitializeComponent();
        _work = work;
        _cancellable = cancellable;
        _thai = thai;
        _context = new TrickRunContext(thai, _cts.Token, input);

        Title = title;
        TitleText.Text = title;
        CopyButton.Content = T("Copy", "คัดลอก");
        CancelButton.Content = T("Cancel", "ยกเลิก");
        CloseButton.Content = T("Close", "ปิด");
        CancelButton.Visibility = cancellable ? Visibility.Visible : Visibility.Collapsed;

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher);
        Loaded += async (_, _) => await RunAsync();
        Closing += OnClosing;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };
    }

    private string T(string en, string th) => _thai ? th : en;

    private async Task RunAsync()
    {
        _running = true;
        CloseButton.IsEnabled = _cancellable;
        StartBusyAnimation();
        _elapsed.Start();
        _timer.Start();
        Tick();

        TrickResult result;
        try
        {
            result = await Task.Run(() => _work(_context));
        }
        catch (OperationCanceledException)
        {
            result = TrickResult.Canceled();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Trick action failed: {ex}");
            result = TrickResult.Fail(TweakErrors.Friendly(ex));
        }

        _running = false;
        _elapsed.Stop();
        _timer.Stop();
        StopBusyAnimation();
        Tick();

        if (string.IsNullOrWhiteSpace(OutputBox.Text) && !string.IsNullOrWhiteSpace(result.Output))
            OutputBox.Text = result.Output;

        Result = result;
        ShowFinalStatus(result);
        CancelButton.Visibility = Visibility.Collapsed;
        CloseButton.IsEnabled = true;
        CloseButton.Focus();

        if (_closeWhenDone) Close();
    }

    /// <summary>Pulls new output from the buffer (at most 4×/s) and keeps the view scrolled to the end.</summary>
    private void Tick()
    {
        if (_running)
        {
            StatusText.Text = (_cts.IsCancellationRequested ? T("Cancelling…", "กำลังยกเลิก…") : T("Running…", "กำลังทำงาน…"))
                              + $"  {_elapsed.Elapsed:mm\\:ss}";
        }

        int version = _context.Output.Version;
        if (version == _shownVersion) return;
        _shownVersion = version;

        bool atEnd = OutputBox.VerticalOffset + OutputBox.ViewportHeight >= OutputBox.ExtentHeight - 24;
        OutputBox.Text = _context.Output.GetText();
        if (atEnd) OutputBox.ScrollToEnd();
    }

    private void ShowFinalStatus(TrickResult result)
    {
        string message = result.Message?.Get(_thai) ?? (result.Success ? T("Finished.", "เสร็จแล้ว") : T("Failed.", "ไม่สำเร็จ"));
        string time = $"  ({_elapsed.Elapsed:mm\\:ss})";

        if (result.Cancelled)
        {
            StatusText.Text = "■ " + T("Cancelled.", "ยกเลิกแล้ว") + time;
            StatusText.Foreground = (Brush)FindResource("WarningBrush");
        }
        else if (result.Success)
        {
            StatusText.Text = "✓ " + message + time;
            StatusText.Foreground = (Brush)FindResource("SuccessBrush");
        }
        else
        {
            StatusText.Text = "✗ " + message + time;
            StatusText.Foreground = (Brush)FindResource("DangerBrush");
        }
    }

    private void OnClosing(object? sender, global::System.ComponentModel.CancelEventArgs e)
    {
        if (!_running) return;

        // Never leave an action running in the background without a window.
        e.Cancel = true;
        if (_cancellable)
        {
            _closeWhenDone = true;
            RequestCancel();
        }
        else
        {
            StatusText.Text = T("Please wait — this step can't be interrupted safely.",
                                "กรุณารอสักครู่ — ขั้นตอนนี้หยุดกลางคันไม่ได้เพื่อความปลอดภัย");
        }
    }

    private void RequestCancel()
    {
        if (!_running || _cts.IsCancellationRequested) return;
        _cts.Cancel();
        CancelButton.IsEnabled = false;
        Tick();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => RequestCancel();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        var text = string.IsNullOrEmpty(OutputBox.Text) ? StatusText.Text : OutputBox.Text;
        if (TryCopyToClipboard(text))
        {
            CopyButton.Content = T("Copied ✓", "คัดลอกแล้ว ✓");
            var reset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            reset.Tick += (_, _) =>
            {
                reset.Stop();
                CopyButton.Content = T("Copy", "คัดลอก");
            };
            reset.Start();
        }
        else
        {
            MessageBox.Show(this, T("The clipboard is busy (another app is using it). Please try again.",
                                    "คลิปบอร์ดกำลังถูกใช้งานโดยแอปอื่น กรุณาลองใหม่อีกครั้ง"),
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Title_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch (InvalidOperationException) { /* mouse already released */ }
    }

    private void StartBusyAnimation()
    {
        BusyTrack.Visibility = Visibility.Visible;
        double width = Math.Max(BusyTrack.ActualWidth, 400);
        var animation = new DoubleAnimation(-BusyIndicator.Width, width, TimeSpan.FromSeconds(1.4))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        BusyShift.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    private void StopBusyAnimation()
    {
        BusyShift.BeginAnimation(TranslateTransform.XProperty, null);
        BusyTrack.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Clipboard access fails with CLIPBRD_E_CANT_OPEN when another app holds
    /// it open; retry a few times instead of crashing.
    /// </summary>
    internal static bool TryCopyToClipboard(string text)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                return true;
            }
            catch (ExternalException)
            {
                Thread.Sleep(60);
            }
        }
        return false;
    }
}
