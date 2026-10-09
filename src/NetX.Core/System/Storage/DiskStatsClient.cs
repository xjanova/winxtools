using System.Diagnostics;
using System.Text.Json.Nodes;
using NetX.Core.Data;

namespace NetX.Core.System.Storage;

public sealed record StatRange(double P25, double Median, double P75, double? Max = null);

/// <summary>What WinXTools users measured on the same drive model (from xman studio).</summary>
public sealed class ModelStats
{
    public int Samples { get; init; }
    public int Devices { get; init; }
    public StatRange? Score { get; init; }
    public StatRange? SeqQ8Read { get; init; }
    public StatRange? SeqQ8Write { get; init; }
    public StatRange? SeqQ1Read { get; init; }
    public StatRange? Rnd4KQ32Read { get; init; }
    public StatRange? Rnd4KQ1Read { get; init; }
    public StatRange? Rnd4KQ1Write { get; init; }
    public StatRange? AccessMs { get; init; }
    public double[]? SurfaceMedian { get; init; }

    public bool HasStats => Score != null;
}

/// <summary>
/// Shares speed-test results with xman studio, anonymously, so it can tell
/// every user what is normal for their drive model. Sent: the drive model,
/// type, size and firmware, the Windows build, the numbers measured and a
/// random id made for this installation. Never sent: names, files, serial
/// numbers, the license or anything that identifies the PC or the person.
/// </summary>
public static class DiskStatsClient
{
    private const string ShareSettingKey = "DiskBench.ShareResults";
    private const string InstallIdSettingKey = "DiskBench.InstallId";
    private const int SurfacePoints = 20;

    private static readonly Lazy<HttpClient> Http = new(() =>
        XmanApi.CreateClient("WinXTools-DiskStats/" + AutoUpdateService.GetCurrentVersion(), TimeSpan.FromSeconds(15)));

    private static readonly Dictionary<string, (DateTime At, ModelStats? Stats)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether results are shared (on unless the user turned it off).</summary>
    public static bool ShareEnabled
    {
        get
        {
            try { return DatabaseService.Instance.GetSetting(ShareSettingKey) != "0"; }
            catch { return false; }
        }
        set
        {
            try { DatabaseService.Instance.SetSetting(ShareSettingKey, value ? "1" : "0"); }
            catch { }
        }
    }

    private static string InstallId
    {
        get
        {
            try
            {
                var id = DatabaseService.Instance.GetSetting(InstallIdSettingKey);
                if (Guid.TryParse(id, out var parsed)) return parsed.ToString();
                var fresh = Guid.NewGuid().ToString();
                DatabaseService.Instance.SetSetting(InstallIdSettingKey, fresh);
                return fresh;
            }
            catch
            {
                return Guid.NewGuid().ToString();
            }
        }
    }

    /// <summary>Sends one finished test. Never throws; returns false when it wasn't stored.</summary>
    public static async Task<bool> UploadAsync(BenchRun run, PhysicalDisk? disk, CancellationToken token = default)
    {
        if (!ShareEnabled || disk == null || string.IsNullOrWhiteSpace(disk.Model)) return false;
        var score = BenchScore.Compute(run);
        if (score == null) return false;

        double? Mbps(string id) => run.Find(id) is { } r ? Math.Round(r.MBps, 2) : null;
        var body = new JsonObject
        {
            ["install_id"] = InstallId,
            ["app_version"] = Trim(AutoUpdateService.GetCurrentVersion(), 20),
            ["os_build"] = Environment.OSVersion.Version.Build,
            ["drive"] = new JsonObject
            {
                ["model"] = SafeModel(disk.Model),
                ["kind"] = KindOf(disk),
                ["bus"] = disk.Bus.ToString(),
                ["media"] = disk.Media.ToString(),
                ["capacity_gb"] = Math.Max(1, (int)Math.Round(disk.SizeBytes / 1e9)),
                ["firmware"] = Trim(disk.Firmware, 32),
                ["rpm"] = disk.Rpm is > 0 ? disk.Rpm : null
            },
            ["test"] = new JsonObject
            {
                ["file_mb"] = run.FileMB,
                ["seconds_per_test"] = run.SecondsPerTest,
                ["label"] = run.Label is "before" or "after" ? run.Label : "",
                ["free_pct"] = run.VolumeTotalBytes > 0 ? Math.Round(run.VolumeFreeBytes * 100.0 / run.VolumeTotalBytes, 2) : null
            },
            ["results"] = new JsonObject
            {
                ["seq1m_q8_read"] = Mbps(DiskBenchmark.Seq1MQ8.Id),
                ["seq1m_q8_write"] = Mbps(DiskBenchmark.Seq1MQ8W.Id),
                ["seq1m_q1_read"] = Mbps(DiskBenchmark.Seq1MQ1.Id),
                ["seq1m_q1_write"] = Mbps(DiskBenchmark.Seq1MQ1W.Id),
                ["rnd4k_q32_read"] = Mbps(DiskBenchmark.Rnd4KQ32.Id),
                ["rnd4k_q32_write"] = Mbps(DiskBenchmark.Rnd4KQ32W.Id),
                ["rnd4k_q1_read"] = Mbps(DiskBenchmark.Rnd4KQ1.Id),
                ["rnd4k_q1_write"] = Mbps(DiskBenchmark.Rnd4KQ1W.Id),
                ["rnd4k_q32_read_iops"] = run.Find(DiskBenchmark.Rnd4KQ32.Id) is { } q32 ? (long)q32.Iops : null,
                ["rnd4k_q1_read_iops"] = run.Find(DiskBenchmark.Rnd4KQ1.Id) is { } q1 ? (long)q1.Iops : null,
                ["access_ms"] = run.AccessTimeMs is double a ? Math.Round(a, 3) : null
            },
            ["surface"] = SurfaceJson(run),
            ["score"] = new JsonObject { ["total"] = score.Total, ["read"] = score.Read, ["write"] = score.Write }
        };

        try
        {
            var reply = await XmanApi.PostAsync(Http.Value, "/disk-benchmarks", body).WaitAsync(token).ConfigureAwait(false);
            if (reply.Success)
            {
                lock (Cache) Cache.Remove(CacheKey(disk));
                return true;
            }
            Debug.WriteLine($"[DiskStatsClient] Upload refused: {reply.Status} {reply.ErrorCode}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DiskStatsClient] Upload failed: {ex.Message}");
        }
        return false;
    }

    /// <summary>Real-world numbers for this drive model, or null (offline, or too few users yet).</summary>
    public static async Task<ModelStats?> GetStatsAsync(PhysicalDisk? disk, CancellationToken token = default)
    {
        if (disk == null || string.IsNullOrWhiteSpace(disk.Model)) return null;
        var key = CacheKey(disk);
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(10)) return hit.Stats;
        }

        ModelStats? stats = null;
        try
        {
            int gb = Math.Max(1, (int)Math.Round(disk.SizeBytes / 1e9));
            var reply = await XmanApi.GetAsync(Http.Value,
                $"/disk-benchmarks/stats?model={Uri.EscapeDataString(SafeModel(disk.Model))}&capacity_gb={gb}").WaitAsync(token).ConfigureAwait(false);
            if (!reply.Success || reply.Data is not { } data) return null;

            var s = data["stats"] as JsonObject;
            stats = new ModelStats
            {
                Samples = (int)(data.Long("samples") ?? 0),
                Devices = (int)(data.Long("devices") ?? 0),
                Score = Range(s, "score_total"),
                SeqQ8Read = Range(s, "seq1m_q8_read"),
                SeqQ8Write = Range(s, "seq1m_q8_write"),
                SeqQ1Read = Range(s, "seq1m_q1_read"),
                Rnd4KQ32Read = Range(s, "rnd4k_q32_read"),
                Rnd4KQ1Read = Range(s, "rnd4k_q1_read"),
                Rnd4KQ1Write = Range(s, "rnd4k_q1_write"),
                AccessMs = Range(s, "access_ms"),
                SurfaceMedian = s?["surface_median"] is JsonArray points && points.Count == SurfacePoints
                    ? points.Select(p => (double?)p ?? 0).ToArray()
                    : null
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DiskStatsClient] Stats failed: {ex.Message}");
            return null;
        }

        lock (Cache) Cache[key] = (DateTime.UtcNow, stats);
        return stats;
    }

    private static StatRange? Range(JsonObject? stats, string name)
    {
        if (stats?[name] is not JsonObject o) return null;
        double? p25 = (double?)o["p25"], median = (double?)o["median"], p75 = (double?)o["p75"];
        if (p25 == null || median == null || p75 == null) return null;
        return new StatRange(p25.Value, median.Value, p75.Value, (double?)o["max"]);
    }

    private static JsonObject? SurfaceJson(BenchRun run)
    {
        if (run.Surface.Count < SurfacePoints) return null;
        var points = new JsonArray();
        for (int i = 0; i < SurfacePoints; i++)
        {
            int from = run.Surface.Count * i / SurfacePoints, to = run.Surface.Count * (i + 1) / SurfacePoints;
            points.Add(Math.Round(run.Surface.Skip(from).Take(Math.Max(1, to - from)).Average(p => p.MBps), 1));
        }
        return new JsonObject
        {
            ["min"] = Math.Round(run.Surface.Min(p => p.MBps), 1),
            ["avg"] = Math.Round(run.Surface.Average(p => p.MBps), 1),
            ["max"] = Math.Round(run.Surface.Max(p => p.MBps), 1),
            ["points"] = points
        };
    }

    private static string CacheKey(PhysicalDisk disk) => $"{SafeModel(disk.Model)}|{Math.Round(disk.SizeBytes / 1e10)}";

    /// <summary>Only the characters the server accepts (letters, digits, space and - _ . / ( ) +), at most 80.</summary>
    private static string SafeModel(string model)
    {
        var chars = model.Trim().Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or '/' or '(' or ')' or '+').ToArray();
        return Trim(new string(chars), 80);
    }

    private static string Trim(string? text, int max) => text == null ? "" : text.Length <= max ? text : text[..max];

    private static string KindOf(PhysicalDisk disk) => disk.Bus switch
    {
        DriveBus.Usb => disk.IsSsd ? "usb-ssd" : disk.IsHdd ? "usb-hdd" : "usb",
        DriveBus.Nvme => "nvme",
        _ => disk.IsSsd ? "sata-ssd" : disk.IsHdd ? "hdd" : "unknown"
    };
}
