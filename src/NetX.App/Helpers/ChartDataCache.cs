using System.Collections.ObjectModel;

namespace NetX.App.Helpers;

/// <summary>
/// Singleton cache for chart data to persist across page navigation.
/// This ensures graphs continue smoothly when switching between pages.
/// </summary>
public sealed class ChartDataCache
{
    private static readonly Lazy<ChartDataCache> _instance = new(() => new ChartDataCache());
    public static ChartDataCache Instance => _instance.Value;

    private ChartDataCache() { }

    // Dashboard chart data
    public ObservableCollection<double> DashboardDownloadHistory { get; } = new();
    public ObservableCollection<double> DashboardUploadHistory { get; } = new();
    public bool DashboardInitialized { get; set; }

    // Network Monitor - per process chart data (selected process)
    public ObservableCollection<double> NetworkMonitorDownloadHistory { get; } = new();
    public ObservableCollection<double> NetworkMonitorUploadHistory { get; } = new();
    public int? NetworkMonitorSelectedProcessId { get; set; }
    public bool NetworkMonitorInitialized { get; set; }

    // Bandwidth Control - per interface chart data
    private readonly Dictionary<string, (ObservableCollection<double> download, ObservableCollection<double> upload)> _interfaceChartData = new();
    public bool BandwidthControlInitialized { get; set; }

    // RAM Optimizer chart data
    public ObservableCollection<double> RamOptimizerHistory { get; } = new();
    public bool RamOptimizerInitialized { get; set; }

    /// <summary>
    /// Initialize collection with zeros if not already initialized
    /// </summary>
    public void InitializeCollection(ObservableCollection<double> collection, int maxDataPoints)
    {
        if (collection.Count == 0)
        {
            for (int i = 0; i < maxDataPoints; i++)
            {
                collection.Add(0);
            }
        }
    }

    /// <summary>
    /// Get or create chart data for a network interface
    /// </summary>
    public (ObservableCollection<double> download, ObservableCollection<double> upload) GetInterfaceChartData(string interfaceId, int maxDataPoints)
    {
        if (!_interfaceChartData.TryGetValue(interfaceId, out var data))
        {
            var download = new ObservableCollection<double>();
            var upload = new ObservableCollection<double>();

            for (int i = 0; i < maxDataPoints; i++)
            {
                download.Add(0);
                upload.Add(0);
            }

            data = (download, upload);
            _interfaceChartData[interfaceId] = data;
        }

        return data;
    }

    /// <summary>
    /// Add new data point and remove oldest (sliding window)
    /// </summary>
    public void AddDataPoint(ObservableCollection<double> collection, double value)
    {
        if (collection.Count > 0)
        {
            collection.RemoveAt(0);
        }
        collection.Add(value);
    }
}
