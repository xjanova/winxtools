using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetX.Core.System.Storage;

/// <summary>A drive model's official ratings, from the maker's data sheet or product page.</summary>
public sealed class DriveSpec
{
    public string Id { get; set; } = "";
    public string Brand { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>"hdd", "sata-ssd", "nvme", "usb-hdd" or "usb-ssd".</summary>
    public string Kind { get; set; } = "";
    public string[] Match { get; set; } = Array.Empty<string>();
    public int? CapacityGB { get; set; }
    public string? Interface { get; set; }
    public double? SeqReadMBs { get; set; }
    public double? SeqWriteMBs { get; set; }
    public double? RndReadIops { get; set; }
    public double? RndWriteIops { get; set; }
    public string? RndQueueDepth { get; set; }
    public int? Rpm { get; set; }
    public int? CacheMB { get; set; }

    /// <summary>Hard disks: maximum sustained transfer rate (outer tracks).</summary>
    public double? SustainedMBs { get; set; }

    /// <summary>Hard disks: sustained rate at the inner tracks, when the maker publishes it.</summary>
    public double? SustainedIdMBs { get; set; }
    public double? AvgSeekMs { get; set; }
    public double? AvgLatencyMs { get; set; }

    /// <summary>Hard disks: "SMR" (overlapping, shingled tracks) or "CMR", when the maker says.</summary>
    public string? Recording { get; set; }
    public string? Notes { get; set; }
    public string Source { get; set; } = "";
    public string SourceUrl { get; set; } = "";

    public bool IsHdd => Kind is "hdd" or "usb-hdd";
}

/// <summary>A link's theoretical ceiling (standard bodies' figures).</summary>
public sealed class InterfaceSpec
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public double MaxMBs { get; set; }
    public string Source { get; set; } = "";
    public string SourceUrl { get; set; } = "";
}

/// <summary>What a drive should be able to do, and where that number comes from.</summary>
public sealed class DriveExpectation
{
    /// <summary>The exact model's data-sheet figures; null when the model isn't in the table.</summary>
    public DriveSpec? Spec { get; init; }

    /// <summary>Rated sequential read (SSD) or maximum sustained rate (HDD), MB/s.</summary>
    public double? RatedRead { get; init; }
    public double? RatedWrite { get; init; }

    /// <summary>Range of ratings of drives of the same kind in the table, when the model itself isn't known.</summary>
    public (double Low, double High, int Models)? TypicalRead { get; init; }

    /// <summary>HDD transfer-rate curve: expected start (outer tracks) and end (inner tracks), MB/s.</summary>
    public double? CurveStart { get; init; }
    public double? CurveEndLow { get; init; }
    public double? CurveEndHigh { get; init; }
    public bool CurveEndFromDataSheet { get; init; }

    public InterfaceSpec? Link { get; init; }
    public string KindKey { get; init; } = "";
}

/// <summary>One conclusion about a test result; the UI turns the code into words.</summary>
public sealed record BenchFinding(string Code, double? Value = null, double? Value2 = null);

/// <summary>
/// Looks up how fast a drive model is rated (data built into WinXTools from
/// the makers' own data sheets, each with its source) and judges a test
/// result against it.
/// </summary>
public static class DriveReference
{
    private sealed class Database
    {
        public List<DriveSpec> Models { get; set; } = new();
        public List<InterfaceSpec> Interfaces { get; set; } = new();
    }

    private static readonly Lazy<Database> Data = new(Load);

    public static IReadOnlyList<DriveSpec> Models => Data.Value.Models;

    private static Database Load()
    {
        try
        {
            using var stream = typeof(DriveReference).Assembly.GetManifestResourceStream("NetX.Core.DriveSpecs.json");
            if (stream == null) return new Database();
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString
            };
            var db = JsonSerializer.Deserialize<Database>(stream, options) ?? new Database();
            db.Models.RemoveAll(m => m.Match.Length == 0 || string.IsNullOrWhiteSpace(m.SourceUrl));
            return db;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DriveReference] {ex.Message}");
            return new Database();
        }
    }

    /// <summary>The table entry for a drive, or null.</summary>
    public static DriveSpec? FindModel(PhysicalDisk? disk)
    {
        if (disk == null) return null;
        // A USB case reports its own product name; the disk inside (when the bridge says) is what the table knows.
        var model = Normalize(disk.InnerModel + " " + disk.Model);
        if (model.Length == 0) return null;

        DriveSpec? best = null;
        int bestLength = 0;
        foreach (var spec in Models)
        {
            foreach (var pattern in spec.Match)
            {
                var p = Normalize(pattern);
                if (p.Length < 4 || p.Length <= bestLength || !model.Contains(p, StringComparison.Ordinal)) continue;
                // A capacity that doesn't fit means the pattern is too loose (500 GB vs 1 TB).
                if (spec.CapacityGB is int gb && disk.SizeBytes > 0)
                {
                    double ratio = disk.SizeBytes / 1e9 / gb;
                    if (ratio < 0.85 || ratio > 1.15) continue;
                }
                best = spec;
                bestLength = p.Length;
            }
        }
        return best;
    }

    private static string Normalize(string? text) =>
        new((text ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>
    /// True when the hard disk records in overlapping (shingled, SMR) tracks: it
    /// slows down sharply while writing a lot, which Windows' optimizer doesn't
    /// take into account. From the maker's own list, else a strong hint: hard
    /// disks only implement TRIM when they are SMR. Null when unknown.
    /// </summary>
    public static bool? IsSmr(PhysicalDisk? disk)
    {
        if (disk is not { IsHdd: true }) return null;
        var recording = FindModel(disk)?.Recording;
        if (string.Equals(recording, "SMR", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(recording, "CMR", StringComparison.OrdinalIgnoreCase)) return false;
        return disk.TrimEnabled == true ? true : null;
    }

    /// <summary>
    /// Hard disk or SSD by the maker's own product line (a USB case named
    /// "One Touch HDD"), when nothing else could tell. Names saying "SSD" are
    /// never taken for a hard disk: some lines come in both.
    /// </summary>
    public static DriveMedia MediaFromModel(string model, string? innerModel, long sizeBytes)
    {
        var spec = FindModel(new PhysicalDisk { Model = model, InnerModel = innerModel, SizeBytes = sizeBytes });
        if (spec == null) return DriveMedia.Unknown;
        bool saysSsd = (model + " " + innerModel).Contains("SSD", StringComparison.OrdinalIgnoreCase);
        if (spec.IsHdd) return saysSsd ? DriveMedia.Unknown : DriveMedia.Hdd;
        return spec.Kind is "usb-ssd" or "sata-ssd" or "nvme" ? DriveMedia.Ssd : DriveMedia.Unknown;
    }

    public static DriveExpectation Expect(PhysicalDisk? disk)
    {
        var spec = FindModel(disk);
        string kind = spec?.Kind ?? KindOf(disk);
        var link = LinkFor(disk, spec);

        // A model the table knows but whose maker publishes no speed (most USB hard
        // disks): name it, and compare with the official ratings of its kind.
        if (spec != null && (spec.IsHdd ? spec.SustainedMBs ?? spec.SeqReadMBs : spec.SeqReadMBs) == null)
        {
            var typical = TypicalOf(kind == "usb-hdd" ? "hdd" : kind, spec.Rpm ?? disk?.Rpm);
            return new DriveExpectation { Spec = spec, TypicalRead = typical, Link = link, KindKey = kind };
        }

        if (spec != null)
        {
            double? start = spec.IsHdd ? spec.SustainedMBs ?? spec.SeqReadMBs : null;
            return new DriveExpectation
            {
                Spec = spec,
                RatedRead = spec.IsHdd ? start : spec.SeqReadMBs,
                RatedWrite = spec.IsHdd ? start : spec.SeqWriteMBs,
                CurveStart = start,
                CurveEndLow = spec.IsHdd && start != null ? spec.SustainedIdMBs ?? start * 0.45 : null,
                CurveEndHigh = spec.IsHdd && start != null ? spec.SustainedIdMBs ?? start * 0.6 : null,
                CurveEndFromDataSheet = spec.SustainedIdMBs != null,
                Link = link,
                KindKey = kind
            };
        }

        // No exact model: the range of official ratings of the same kind of drive.
        return new DriveExpectation
        {
            TypicalRead = TypicalOf(kind, disk?.Rpm),
            Link = link,
            KindKey = kind
        };
    }

    private static (double Low, double High, int Models)? TypicalOf(string kind, int? rpm)
    {
        var peers = Models.Where(m => m.Kind == kind).ToList();
        if (kind == "hdd" && rpm is > 0) peers = peers.Where(m => m.Rpm == rpm).ToList();
        var reads = peers.Select(m => m.IsHdd ? m.SustainedMBs ?? m.SeqReadMBs : m.SeqReadMBs)
            .Where(v => v is > 0).Select(v => v!.Value).ToList();
        return reads.Count >= 3 ? (reads.Min(), reads.Max(), reads.Count) : null;
    }

    private static string KindOf(PhysicalDisk? disk)
    {
        if (disk == null) return "";
        if (disk.Bus == DriveBus.Usb) return disk.IsSsd ? "usb-ssd" : disk.IsHdd ? "usb-hdd" : "usb";
        if (disk.Bus == DriveBus.Nvme) return "nvme";
        if (disk.IsSsd) return "sata-ssd";
        if (disk.IsHdd) return "hdd";
        return "";
    }

    private static InterfaceSpec? LinkFor(PhysicalDisk? disk, DriveSpec? spec)
    {
        var all = Data.Value.Interfaces;
        if (all.Count == 0) return null;
        string? id = null;
        var text = spec?.Interface?.ToUpperInvariant() ?? "";
        if (text.Contains("USB4")) id = "usb4";
        else if (text.Contains("2X2") || text.Contains("20GBPS")) id = "usb3g2x2";
        else if (text.Contains("USB") && (text.Contains("GEN 2") || text.Contains("10GBPS"))) id = "usb3g2";
        else if (text.Contains("USB 3") || text.Contains("5GBPS")) id = "usb3g1";
        else if (text.Contains("PCIE 5") || text.Contains("GEN5") || text.Contains("GEN 5")) id = "pcie5x4";
        else if (text.Contains("PCIE 4") || text.Contains("GEN4") || text.Contains("GEN 4")) id = "pcie4x4";
        else if (text.Contains("PCIE 3") || text.Contains("GEN3") || text.Contains("GEN 3")) id = "pcie3x4";
        else if (text.Contains("SATA") || disk?.Bus is DriveBus.Sata) id = "sata3";
        else if (disk?.Bus == DriveBus.Raid && spec?.Kind is "sata-ssd" or "hdd") id = "sata3";
        if (spec?.Interface != null && text.Contains("X2")) id = id?.Replace("x4", "x2");
        return id == null ? null : all.FirstOrDefault(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    #region Judging a result

    /// <summary>Conclusions for a finished test, most important first.</summary>
    public static List<BenchFinding> Judge(BenchRun run, DriveExpectation expect, PhysicalDisk? disk)
    {
        var findings = new List<BenchFinding>();
        var seqRead = run.Find(DiskBenchmark.Seq1MQ8.Id)?.MBps;
        bool hdd = expect.Spec?.IsHdd ?? disk?.IsHdd ?? false;

        // HDD: the outer-track speed of the surface curve is what the data sheet rates.
        double? measured = hdd && run.Surface.Count > 4
            ? run.Surface.Take(Math.Max(3, run.Surface.Count / 20)).Max(p => p.MBps)
            : seqRead;

        if (measured is double m && expect.RatedRead is double rated && rated > 0)
        {
            double pct = m / rated * 100;
            findings.Add(new BenchFinding(pct >= 80 ? "Normal" : pct >= 55 ? "BelowSpec" : "FarBelowSpec", pct, rated));
        }
        else if (measured is double m2 && expect.TypicalRead is { } range)
        {
            findings.Add(new BenchFinding(m2 >= range.Low * 0.8 ? "WithinTypical" : "BelowTypical", range.Low, range.High));
        }
        else
        {
            findings.Add(new BenchFinding("NoRating"));
        }

        if (seqRead is double s)
        {
            bool sata = disk?.Bus is DriveBus.Sata or DriveBus.Raid && disk.IsSsd;
            if (sata && s is > 200 and < 300 && (expect.RatedRead ?? 500) > 400)
                findings.Add(new BenchFinding("Sata2Suspected", s));
            if (disk?.Bus == DriveBus.Usb && s is > 15 and < 46)
                findings.Add(new BenchFinding("Usb2Suspected", s));
            if (disk?.Bus == DriveBus.Nvme && expect.RatedRead is double r)
            {
                // A PCIe 4.0 drive in a PCIe 3.0 x4 slot stops at about 3.2–3.6 GB/s.
                if (r >= 4500 && s is >= 2800 and <= 3700)
                    findings.Add(new BenchFinding("PcieGen3Suspected", s, r));
                else if (r > 2500 && s < r * 0.55 && s > 1000)
                    findings.Add(new BenchFinding("PcieLimited", s, r));
            }
        }

        if (hdd && run.Surface.Count >= 20)
        {
            int dips = CountDips(run.Surface);
            if (dips > 0) findings.Add(new BenchFinding("SurfaceDips", dips));
        }
        else if (!hdd && run.Surface.Count >= 20)
        {
            double median = Median(run.Surface.Select(p => p.MBps));
            int slow = run.Surface.Count(p => p.MBps < median * 0.5);
            if (slow > 0) findings.Add(new BenchFinding("SsdSlowSpots", slow));
        }

        if (hdd && run.AccessTimeMs is double access && access > 25)
            findings.Add(new BenchFinding("SlowAccess", access));

        // SSDs need free space for their fast write cache and housekeeping.
        if (!hdd && run.VolumeTotalBytes > 0)
        {
            double freePct = run.VolumeFreeBytes * 100.0 / run.VolumeTotalBytes;
            if (freePct < 10) findings.Add(new BenchFinding("SsdNearlyFull", freePct, run.VolumeFreeBytes / 1e9));
        }
        return findings;
    }

    /// <summary>Points well below their neighbours — on a hard disk that often means weak or remapped sectors.</summary>
    public static int CountDips(IReadOnlyList<SurfacePoint> curve)
    {
        int dips = 0;
        for (int i = 0; i < curve.Count; i++)
        {
            var window = curve.Skip(Math.Max(0, i - 3)).Take(7).Where((p, k) => p != curve[i]).Select(p => p.MBps).ToList();
            if (window.Count < 3) continue;
            if (curve[i].MBps < Median(window) * 0.6) dips++;
        }
        return dips;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return 0;
        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }

    #endregion
}
