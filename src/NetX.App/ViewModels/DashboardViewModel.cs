using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using NetX.Core.Network;
using NetX.Core.Data;

namespace NetX.App.ViewModels;

public class DashboardViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly System.Threading.Timer _updateTimer;
    private readonly Queue<double> _downloadHistory = new();
    private readonly Queue<double> _uploadHistory = new();
    private const int MaxDataPoints = 60;

    private string _downloadSpeed = "0 B/s";
    private string _uploadSpeed = "0 B/s";
    private string _activeApps = "0";
    private string _totalConnections = "0";
    private string _todayDownload = "0 B";
    private string _todayUpload = "0 B";
    private ObservableCollection<TopConsumerItem> _topConsumers = new();
    private ISeries[] _bandwidthSeries = Array.Empty<ISeries>();
    private Axis[] _xAxes = null!;
    private Axis[] _yAxes = null!;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string DownloadSpeed
    {
        get => _downloadSpeed;
        set { _downloadSpeed = value; OnPropertyChanged(); }
    }

    public string UploadSpeed
    {
        get => _uploadSpeed;
        set { _uploadSpeed = value; OnPropertyChanged(); }
    }

    public string ActiveApps
    {
        get => _activeApps;
        set { _activeApps = value; OnPropertyChanged(); }
    }

    public string TotalConnections
    {
        get => _totalConnections;
        set { _totalConnections = value; OnPropertyChanged(); }
    }

    public string TodayDownload
    {
        get => _todayDownload;
        set { _todayDownload = value; OnPropertyChanged(); }
    }

    public string TodayUpload
    {
        get => _todayUpload;
        set { _todayUpload = value; OnPropertyChanged(); }
    }

    public ObservableCollection<TopConsumerItem> TopConsumers
    {
        get => _topConsumers;
        set { _topConsumers = value; OnPropertyChanged(); }
    }

    public ISeries[] BandwidthSeries
    {
        get => _bandwidthSeries;
        set { _bandwidthSeries = value; OnPropertyChanged(); }
    }

    public Axis[] XAxes
    {
        get => _xAxes;
        set { _xAxes = value; OnPropertyChanged(); }
    }

    public Axis[] YAxes
    {
        get => _yAxes;
        set { _yAxes = value; OnPropertyChanged(); }
    }

    public ICommand BlockAllCommand { get; }
    public ICommand ResumeAllCommand { get; }
    public ICommand QuickCleanCommand { get; }

    public DashboardViewModel()
    {
        // Initialize commands
        BlockAllCommand = new RelayCommand(BlockAll);
        ResumeAllCommand = new RelayCommand(ResumeAll);
        QuickCleanCommand = new RelayCommand(QuickClean);

        // Initialize chart axes
        XAxes = new Axis[]
        {
            new Axis
            {
                Labels = null,
                LabelsPaint = new SolidColorPaint(SKColors.Transparent),
                SeparatorsPaint = new SolidColorPaint(new SKColor(30, 30, 40)),
                ShowSeparatorLines = true
            }
        };

        YAxes = new Axis[]
        {
            new Axis
            {
                LabelsPaint = new SolidColorPaint(new SKColor(150, 150, 150)),
                SeparatorsPaint = new SolidColorPaint(new SKColor(30, 30, 40)),
                ShowSeparatorLines = true,
                Labeler = value => FormatBytes((long)value) + "/s"
            }
        };

        // Initialize data
        for (int i = 0; i < MaxDataPoints; i++)
        {
            _downloadHistory.Enqueue(0);
            _uploadHistory.Enqueue(0);
        }

        UpdateChartSeries();

        // Start update timer (NetworkMonitor initializes automatically)
        _updateTimer = new System.Threading.Timer(UpdateStats, null, 0, 1000);

        // Load today's stats
        LoadTodayStats();
    }

    private void UpdateStats(object? state)
    {
        try
        {
            var stats = NetworkMonitor.Instance.GetAllProcessStats();
            var (totalDown, totalUp) = NetworkMonitor.Instance.GetTotalBandwidth();

            // Update on UI thread
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                DownloadSpeed = FormatBytes(totalDown) + "/s";
                UploadSpeed = FormatBytes(totalUp) + "/s";
                ActiveApps = stats.Count.ToString();

                int connections = stats.Sum(s => s.Connections.Count);
                TotalConnections = connections.ToString();

                // Update history
                _downloadHistory.Dequeue();
                _downloadHistory.Enqueue(totalDown);
                _uploadHistory.Dequeue();
                _uploadHistory.Enqueue(totalUp);

                UpdateChartSeries();

                // Update top consumers
                UpdateTopConsumers(stats);
            });
        }
        catch
        {
            // Ignore errors
        }
    }

    private void UpdateChartSeries()
    {
        var downloadValues = _downloadHistory.ToArray();
        var uploadValues = _uploadHistory.ToArray();

        BandwidthSeries = new ISeries[]
        {
            new LineSeries<double>
            {
                Values = downloadValues,
                Fill = new SolidColorPaint(new SKColor(0, 212, 255, 30)),
                Stroke = new SolidColorPaint(new SKColor(0, 212, 255), 2),
                GeometryFill = null,
                GeometryStroke = null,
                LineSmoothness = 0.5
            },
            new LineSeries<double>
            {
                Values = uploadValues,
                Fill = new SolidColorPaint(new SKColor(0, 255, 136, 30)),
                Stroke = new SolidColorPaint(new SKColor(0, 255, 136), 2),
                GeometryFill = null,
                GeometryStroke = null,
                LineSmoothness = 0.5
            }
        };
    }

    private void UpdateTopConsumers(List<ProcessNetworkInfo> stats)
    {
        var top5 = stats
            .OrderByDescending(s => s.DownloadSpeed + s.UploadSpeed)
            .Take(5)
            .Select(s => new TopConsumerItem
            {
                ProcessName = s.ProcessName,
                Speed = FormatBytes(s.DownloadSpeed + s.UploadSpeed) + "/s",
                Percentage = CalculatePercentage(s.DownloadSpeed + s.UploadSpeed, stats)
            })
            .ToList();

        TopConsumers.Clear();
        foreach (var item in top5)
        {
            TopConsumers.Add(item);
        }
    }

    private double CalculatePercentage(long speed, List<ProcessNetworkInfo> allStats)
    {
        var total = allStats.Sum(s => s.DownloadSpeed + s.UploadSpeed);
        if (total == 0) return 0;
        return (double)speed / total * 100;
    }

    private void LoadTodayStats()
    {
        try
        {
            var (sent, received) = DatabaseService.Instance.GetTodayBandwidth();
            TodayDownload = FormatBytes(received);
            TodayUpload = FormatBytes(sent);
        }
        catch
        {
            // Ignore
        }
    }

    private void BlockAll()
    {
        System.Windows.MessageBox.Show("Block All - Coming soon!", "NetX",
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }

    private void ResumeAll()
    {
        System.Windows.MessageBox.Show("Resume All - Coming soon!", "NetX",
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }

    private void QuickClean()
    {
        System.Windows.MessageBox.Show("Quick Clean - Coming soon!", "NetX",
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {sizes[order]}";
    }

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void Dispose()
    {
        _updateTimer?.Dispose();
    }
}

public class TopConsumerItem
{
    public string ProcessName { get; set; } = string.Empty;
    public string Speed { get; set; } = "0 B/s";
    public double Percentage { get; set; }
}

public class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => _execute();
}
