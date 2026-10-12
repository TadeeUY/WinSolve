using System.Text.RegularExpressions;

namespace WinSolve.Services;

/// <summary>What the rating looks at, read from the PC by <see cref="PcReport"/>.</summary>
public sealed record PcSpecs(
    string CpuName, int Threads, double RamGb,
    string SystemDiskMedia, string SystemDiskBus, HealthLevel SystemDiskHealth,
    double SystemFreePercent, IReadOnlyList<string> GpuNames, int WindowsBuild, bool IsVirtualMachine = false);

public sealed record RatingPart(string Name, int Score, string Detail);

/// <summary>An overall 0-100 score, what the PC is good for and what would improve it most.</summary>
public sealed record PcRatingResult(int Score, string Verdict, IReadOnlyList<RatingPart> Parts,
    IReadOnlyList<string> GoodFor, IReadOnlyList<string> Tips, string? Weakest);

/// <summary>
/// Plain-language rating of the hardware for the PC report. Simple, explainable rules (processor
/// class and generation, memory size, SSD or hard disk, graphics class) rather than a benchmark:
/// it says roughly how good the PC is and what to upgrade first. All text is English (translated
/// when shown).
/// </summary>
public static class PcRating
{
    public static PcRatingResult Rate(PcSpecs s)
    {
        var cpu = CpuScore(s.CpuName, s.Threads);
        var ram = RamScore(s.RamGb);
        var disk = DiskScore(s.SystemDiskMedia, s.SystemDiskBus, s.SystemDiskHealth);
        var gpu = GpuScore(s.GpuNames);

        var parts = new List<RatingPart>
        {
            new("Processor", cpu, CpuDetail(cpu)),
            new("Memory (RAM)", ram, RamDetail(s.RamGb)),
            new("Storage", disk, DiskDetail(s.SystemDiskMedia, s.SystemDiskBus, s.SystemDiskHealth)),
            new("Graphics", gpu, GpuDetail(gpu)),
        };
        var score = (int)Math.Round(cpu * 0.30 + ram * 0.25 + disk * 0.25 + gpu * 0.20);
        // A failing system drive or a nearly full one drags everything down, whatever the parts.
        if (s.SystemDiskHealth == HealthLevel.Bad) score = Math.Min(score, 35);
        if (s.SystemFreePercent is >= 0 and < 5) score -= 10;
        score = Math.Clamp(score, 0, 100);

        var goodFor = new List<string> { "Browsing, email, video calls and streaming" };
        if (cpu >= 45 && ram >= 55) goodFor.Add("Office work and studying");
        if (cpu >= 70 && ram >= 80) goodFor.Add("Heavy multitasking (many programs and tabs at once)");
        if (cpu >= 75 && ram >= 80 && disk >= 80) goodFor.Add("Photo and video editing");
        if (gpu >= 85 && ram >= 80) goodFor.Add("Demanding games at high settings");
        else if (gpu >= 65 && ram >= 55) goodFor.Add("Most games at medium settings");
        else if (gpu >= 40) goodFor.Add("Light and older games");

        var tips = new List<string>();
        if (s.SystemDiskHealth == HealthLevel.Bad)
            tips.Add("Back up your files now: the Windows drive shows signs of failing. Replace it soon.");
        else if (s.SystemDiskHealth == HealthLevel.Caution)
            tips.Add("Keep a backup of your files: the Windows drive shows early signs of wear.");
        if (IsHardDisk(s.SystemDiskMedia))
            tips.Add("Replace the hard disk with an SSD: Windows starts and programs open several times faster. It is the upgrade that makes the biggest difference.");
        if (s.RamGb > 0 && s.RamGb < 7.5)
            tips.Add("Add memory: with less than 8 GB of RAM the PC slows down with a few programs open. 16 GB is ideal.");
        else if (s.RamGb is >= 7.5 and < 15)
            tips.Add("Upgrading to 16 GB of RAM helps with many tabs, games and editing.");
        if (s.SystemFreePercent is >= 0 and < 15)
            tips.Add("The Windows drive is almost full: free up space (Disk space page) so Windows can update and stay fast.");
        if (s.WindowsBuild is > 0 and < 22000)
            tips.Add("Windows 10 no longer gets free security updates (since October 2025): move to Windows 11 if the PC supports it.");
        if (CpuGenerationPenalty(s.CpuName) >= 15)
            tips.Add("The processor is several generations old: it is the part that limits this PC the most in the long run.");
        else if (cpu < 40 && !s.IsVirtualMachine)
            tips.Add("The processor is the slowest part: a faster processor (or a newer PC) is what would make the biggest difference.");
        if (s.IsVirtualMachine)
            tips.Add("This is a virtual machine: the scores reflect the processors, memory and graphics assigned to it, not the physical computer.");
        if (tips.Count == 0) tips.Add("Nothing urgent: this PC is in good shape. Keep Windows and drivers up to date.");

        var weakest = parts.MinBy(p => p.Score)!;
        return new PcRatingResult(score, Verdict(score), parts, goodFor, tips,
            weakest.Score < 60 && parts.Max(p => p.Score) - weakest.Score >= 15 ? weakest.Name : null);
    }

    public static string Verdict(int score) => score switch
    {
        >= 85 => "Excellent PC",
        >= 70 => "Very good PC",
        >= 55 => "Good PC",
        >= 40 => "Basic PC",
        _ => "Limited PC",
    };

    // ───────────── Processor ─────────────

    internal static int CpuScore(string name, int threads)
    {
        var n = name ?? "";
        var threadScore = Math.Clamp(threads * 6.25, 10, 100); // 16 threads = 100
        int? tier =
            Regex.IsMatch(n, @"\b(Atom|Celeron|Pentium|Athlon|Sempron|Duron)\b|\bA(4|6|8|9|10)-\d", RegexOptions.IgnoreCase) ? 25 :
            Regex.IsMatch(n, @"\bi9\b|Ryzen\s*9|Threadripper|Ultra\s*9", RegexOptions.IgnoreCase) ? 96 :
            Regex.IsMatch(n, @"\bi7\b|Ryzen\s*7|Ultra\s*7", RegexOptions.IgnoreCase) ? 86 :
            Regex.IsMatch(n, @"\bi5\b|Ryzen\s*5|Ultra\s*5", RegexOptions.IgnoreCase) ? 72 :
            Regex.IsMatch(n, @"\bi3\b|Ryzen\s*3", RegexOptions.IgnoreCase) ? 52 :
            Regex.IsMatch(n, @"\b(Core\s*[3579]|Snapdragon\s*X)\b", RegexOptions.IgnoreCase) ? 70 :
            null; // Xeon, EPYC and unknown names: judged by their threads
        var score = tier is { } t ? t * 0.7 + threadScore * 0.3 : threadScore * 0.9;
        score -= CpuGenerationPenalty(n);
        // Low-power laptop chips ("U"/"Y" models) are slower than desktop chips of the same name.
        if (Regex.IsMatch(n, @"\d{4,5}[UY]\b", RegexOptions.IgnoreCase)) score -= 5;
        return (int)Math.Clamp(Math.Round(score), 5, 100);
    }

    /// <summary>Points off for older Intel Core and AMD Ryzen generations, read from the model number.</summary>
    internal static int CpuGenerationPenalty(string name)
    {
        var intel = Regex.Match(name ?? "", @"\bi[3579]-(\d{3,5})", RegexOptions.IgnoreCase);
        if (intel.Success)
        {
            // i7-870 = 1st gen; i5-4590 = 4th; i5-10400 = 10th; laptop chips from the 10th
            // generation on have four digits starting with 1 (i5-1135G7 = 11th, i5-1235U = 12th).
            var digits = intel.Groups[1].Value;
            var gen = digits.Length == 3 ? 1
                : digits.Length == 5 || digits[0] == '1' ? int.Parse(digits[..2])
                : int.Parse(digits[..1]);
            return gen switch { <= 3 => 35, <= 5 => 25, <= 7 => 15, <= 9 => 8, <= 11 => 3, _ => 0 };
        }
        var ryzen = Regex.Match(name ?? "", @"Ryzen\s*\d\s*(?:PRO\s*)?(\d)\d{3}", RegexOptions.IgnoreCase);
        if (ryzen.Success)
            return ryzen.Groups[1].Value switch { "1" => 18, "2" => 14, "3" => 7, "4" => 4, _ => 0 };
        return 0;
    }

    private static string CpuDetail(int score) => score switch
    {
        >= 80 => "Fast processor: handles anything you throw at it.",
        >= 60 => "Good processor for everyday use and most tasks.",
        >= 40 => "Enough for everyday tasks; heavy programs will feel slow.",
        _ => "Slow processor: it limits what the PC can do.",
    };

    // ───────────── Memory ─────────────

    internal static int RamScore(double gb) => gb switch
    {
        <= 0 => 50,
        < 3.5 => 10,
        < 5 => 22,
        < 7.5 => 38,
        < 11 => 58,
        < 15 => 70,
        < 24 => 85,
        < 48 => 96,
        _ => 100,
    };

    private static string RamDetail(double gb) => gb switch
    {
        <= 0 => "Couldn't read the amount of memory.",
        < 7.5 => "Little memory: Windows and a browser already use most of it.",
        < 15 => "Enough for everyday use; it fills up with many tabs or games.",
        < 24 => "Plenty of memory for almost everything.",
        _ => "Lots of memory: ready for editing, virtual machines and heavy games.",
    };

    // ───────────── Storage ─────────────

    private static bool IsHardDisk(string media) => media.Contains("HDD", StringComparison.OrdinalIgnoreCase);

    internal static int DiskScore(string media, string bus, HealthLevel health)
    {
        var score = bus.Contains("NVMe", StringComparison.OrdinalIgnoreCase) ? 100
            : media.Contains("SSD", StringComparison.OrdinalIgnoreCase) ? 85
            : IsHardDisk(media) ? 30
            : 60; // unknown (virtual disks, odd controllers)
        return health switch { HealthLevel.Bad => Math.Min(score, 15), HealthLevel.Caution => score - 25, _ => score };
    }

    private static string DiskDetail(string media, string bus, HealthLevel health)
    {
        if (health == HealthLevel.Bad) return "The Windows drive shows signs of failing.";
        var kind = bus.Contains("NVMe", StringComparison.OrdinalIgnoreCase) ? "Very fast NVMe SSD."
            : media.Contains("SSD", StringComparison.OrdinalIgnoreCase) ? "SSD: fast startup and programs."
            : IsHardDisk(media) ? "Mechanical hard disk: the slowest part of this PC."
            : "Drive type unknown.";
        // A line each, so both halves are translated.
        return health == HealthLevel.Caution ? kind + "\n" + "Shows early signs of wear." : kind;
    }

    // ───────────── Graphics ─────────────

    internal static int GpuScore(IReadOnlyList<string> names) => names.Count == 0 ? 15 : names.Max(GpuScore);

    internal static int GpuScore(string name)
    {
        var n = name ?? "";
        var nvidia = Regex.Match(n, @"\b(RTX|GTX|GT)\s*(?:A)?(\d{3,4})", RegexOptions.IgnoreCase);
        if (nvidia.Success)
        {
            var num = int.Parse(nvidia.Groups[2].Value);
            var cls = num % 100; // 50, 60, 70, 80, 90
            var series = num / 100;  // 10, 16, 20, 30, 40, 50 (or 9 for 9xx)
            if (nvidia.Groups[1].Value.Equals("GT", StringComparison.OrdinalIgnoreCase)) return 30;
            var baseScore = cls switch { >= 90 => 98, >= 80 => 94, >= 70 => 88, >= 60 => 80, >= 50 => 70, _ => 60 };
            var age = series switch { >= 40 => 0, >= 30 => 4, >= 20 => 10, >= 16 => 18, >= 10 => 22, _ => 35 };
            return Math.Clamp(baseScore - age, 30, 100);
        }
        var radeon = Regex.Match(n, @"\bRX\s*(\d{3,4})", RegexOptions.IgnoreCase);
        if (radeon.Success)
        {
            var num = int.Parse(radeon.Groups[1].Value);
            if (num < 1000) return num >= 570 ? 55 : 40; // RX 4xx/5xx
            var cls = num % 1000 / 100;                   // 5, 6, 7, 8, 9
            var gen = num / 1000;                          // 5, 6, 7, 9
            var baseScore = cls switch { >= 9 => 97, 8 => 92, 7 => 87, 6 => 78, _ => 66 };
            return Math.Clamp(baseScore - (gen >= 7 ? 0 : gen == 6 ? 4 : 10), 30, 100);
        }
        if (Regex.IsMatch(n, @"\bArc\s*(A|B)\d{3}", RegexOptions.IgnoreCase)) return 74;
        if (Regex.IsMatch(n, @"Radeon.*\b(7[6-9]0M|8[0-9]0M)\b|Arc\b", RegexOptions.IgnoreCase)) return 50; // strong integrated
        if (Regex.IsMatch(n, @"Iris(\(R\))?\s*Xe|Radeon(\(TM\))?\s*(Graphics|Vega|\d{3}M)", RegexOptions.IgnoreCase)) return 38;
        if (Regex.IsMatch(n, @"Intel.*(UHD|HD|Iris)|Radeon\s*R\d", RegexOptions.IgnoreCase)) return 25;
        if (Regex.IsMatch(n, @"Quadro|RTX\s*A\d|Radeon\s*Pro|FirePro", RegexOptions.IgnoreCase)) return 75;
        return 15; // Microsoft Basic Display, virtual machines, unknown
    }

    private static string GpuDetail(int score) => score switch
    {
        >= 85 => "Powerful graphics card for modern games and 3D.",
        >= 65 => "Dedicated graphics card: good for games at medium settings.",
        >= 35 => "Integrated graphics: fine for video and light games.",
        _ => "Basic graphics: video and office work only.",
    };
}
