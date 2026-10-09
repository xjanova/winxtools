namespace NetX.Core.System.Storage;

/// <summary>A test turned into one number, so drives (and before/after) are easy to compare.</summary>
public sealed record BenchScoreResult(int Read, int? Write, int Total, string TierKey);

/// <summary>
/// WinXTools disk score. Each MB/s counts with a weight that reflects how much
/// it is felt in everyday use: small random reads/writes one at a time (what
/// makes Windows and apps open fast) weigh the most, big sequential transfers
/// the least.
///
///   part  = 0.10 × SEQ1M Q8T1 + 0.25 × SEQ1M Q1T1 + 1 × RND4K Q32T1 + 8 × RND4K Q1T1   (MB/s)
///   score = read part + write part
///
/// Typical results: hard disk ≈ 100, SATA SSD ≈ 1,500–2,000, NVMe ≈ 3,000–8,000.
/// </summary>
public static class BenchScore
{
    private static readonly (string Pattern, double Weight)[] ReadWeights =
    [
        ("SEQ1M Q8T1", 0.10), ("SEQ1M Q1T1", 0.25), ("RND4K Q32T1", 1.0), ("RND4K Q1T1", 8.0)
    ];

    public static BenchScoreResult? Compute(BenchRun run)
    {
        double? read = Part(run, "");
        if (read == null) return null;
        double? write = Part(run, " W");
        int total = (int)Math.Round(read.Value + (write ?? read.Value));
        return new BenchScoreResult((int)Math.Round(read.Value), write is double w ? (int)Math.Round(w) : null, total, TierOf(total));
    }

    private static double? Part(BenchRun run, string suffix)
    {
        double sum = 0;
        foreach (var (pattern, weight) in ReadWeights)
        {
            var result = run.Find(pattern + suffix);
            if (result == null) return null;
            sum += result.MBps * weight;
        }
        return sum;
    }

    /// <summary>WinXTools' own scale (language key suffix).</summary>
    public static string TierOf(int total) => total switch
    {
        < 300 => "Hdd",
        < 1200 => "Entry",
        < 2500 => "Sata",
        < 5000 => "Nvme",
        < 8000 => "FastNvme",
        _ => "TopNvme"
    };
}
