using WinSolve.Services;
using Xunit;

namespace WinSolve.Tests;

public class PcRatingTests
{
    private static PcSpecs Pc(string cpu, int threads, double ram, string media, string bus, string gpu,
        HealthLevel health = HealthLevel.Good, double free = 50, int build = 26100)
        => new(cpu, threads, ram, media, bus, health, free, [gpu], build);

    [Fact]
    public void A_modern_gaming_pc_is_excellent()
    {
        var r = PcRating.Rate(Pc("AMD Ryzen 7 7800X3D 8-Core Processor", 16, 32, "SSD", "NVMe", "NVIDIA GeForce RTX 4070"));
        Assert.True(r.Score >= 85, $"score {r.Score}");
        Assert.Equal("Excellent PC", r.Verdict);
        Assert.Contains(r.GoodFor, g => g.StartsWith("Demanding games"));
        Assert.Null(r.Weakest);
    }

    [Fact]
    public void An_old_office_pc_with_a_hard_disk_is_limited_and_says_ssd_first()
    {
        var r = PcRating.Rate(Pc("Intel(R) Core(TM) i3-4130 CPU @ 3.40GHz", 4, 4, "HDD", "SATA", "Intel(R) HD Graphics 4400", build: 19045));
        Assert.True(r.Score < 40, $"score {r.Score}");
        Assert.Contains(r.Tips, t => t.Contains("SSD"));
        Assert.Contains(r.Tips, t => t.StartsWith("Add memory"));
        Assert.Contains(r.Tips, t => t.StartsWith("Windows 10"));
    }

    [Fact]
    public void A_typical_laptop_is_good_and_names_its_weak_part()
    {
        var r = PcRating.Rate(Pc("11th Gen Intel(R) Core(TM) i5-1135G7 @ 2.40GHz", 8, 8, "SSD", "NVMe", "Intel(R) Iris(R) Xe Graphics"));
        Assert.InRange(r.Score, 45, 75);
        Assert.Equal("Graphics", r.Weakest);
    }

    [Fact]
    public void A_failing_system_drive_caps_the_score_and_comes_first()
    {
        var r = PcRating.Rate(Pc("Intel(R) Core(TM) i7-12700K", 20, 32, "SSD", "NVMe", "NVIDIA GeForce RTX 3080", HealthLevel.Bad));
        Assert.True(r.Score <= 35);
        Assert.StartsWith("Back up your files now", r.Tips[0]);
    }

    [Fact]
    public void A_slow_processor_is_always_mentioned_and_virtual_machines_are_explained()
    {
        var slow = PcRating.Rate(Pc("Intel(R) Celeron(R) N4020 CPU @ 1.10GHz", 2, 16, "SSD", "NVMe", "Intel(R) UHD Graphics 600"));
        Assert.Contains(slow.Tips, t => t.StartsWith("The processor is the slowest part"));
        var vm = PcRating.Rate(new PcSpecs("Intel(R) Xeon(R) Platinum 8573C", 4, 16, "SSD", "NVMe", HealthLevel.Good, 20, ["Microsoft Hyper-V Video"], 26100, IsVirtualMachine: true));
        Assert.Contains(vm.Tips, t => t.StartsWith("This is a virtual machine"));
        Assert.DoesNotContain(vm.Tips, t => t.StartsWith("Nothing urgent"));
    }

    [Theory]
    [InlineData("Intel(R) Core(TM) i7-4770 CPU", 25)]
    [InlineData("Intel(R) Core(TM) i7-870 CPU", 35)]
    [InlineData("11th Gen Intel(R) Core(TM) i5-1135G7", 3)]
    [InlineData("12th Gen Intel(R) Core(TM) i5-1235U", 0)]
    [InlineData("Intel(R) Core(TM) i5-8250U", 8)]
    [InlineData("13th Gen Intel(R) Core(TM) i9-13900K", 0)]
    [InlineData("AMD Ryzen 5 2600 Six-Core Processor", 14)]
    [InlineData("AMD Ryzen 9 7950X", 0)]
    [InlineData("Intel(R) Xeon(R) Platinum 8370C CPU", 0)]
    public void Processor_generation_is_read_from_the_model_number(string name, int penalty)
        => Assert.Equal(penalty, PcRating.CpuGenerationPenalty(name));

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4090", 98)]
    [InlineData("NVIDIA GeForce GTX 1060 6GB", 58)]
    [InlineData("AMD Radeon RX 7800 XT", 92)]
    [InlineData("Intel(R) UHD Graphics 620", 25)]
    [InlineData("Intel(R) Iris(R) Xe Graphics", 38)]
    [InlineData("AMD Radeon(TM) Graphics", 38)]
    [InlineData("Microsoft Hyper-V Video", 15)]
    [InlineData("Microsoft Basic Display Adapter", 15)]
    public void Graphics_cards_are_classed_by_name(string name, int score)
        => Assert.Equal(score, PcRating.GpuScore(name));

    [Fact]
    public void Every_text_has_a_spanish_translation()
    {
        var keys = typeof(Localization.Spanish).GetField("Table")!.GetValue(null) as Dictionary<string, string>;
        var samples = new[]
        {
            PcRating.Rate(Pc("AMD Ryzen 7 7800X3D", 16, 32, "SSD", "NVMe", "NVIDIA GeForce RTX 4070")),
            PcRating.Rate(Pc("Intel(R) Core(TM) i3-4130", 4, 4, "HDD", "SATA", "Intel(R) HD Graphics", HealthLevel.Caution, 5, 19045)),
            PcRating.Rate(Pc("Intel(R) Core(TM) i5-10400", 12, 12, "SSD", "SATA", "NVIDIA GeForce GTX 1650", HealthLevel.Bad)),
            PcRating.Rate(new PcSpecs("Intel(R) Celeron(R) N4020", 2, 4, "SSD", "SATA", HealthLevel.Good, 50, ["Microsoft Hyper-V Video"], 26100, IsVirtualMachine: true)),
            PcRating.Rate(Pc("Intel(R) Celeron(R) N4020", 2, 16, "SSD", "NVMe", "Intel(R) UHD Graphics 600")),
        };
        foreach (var r in samples)
        {
            foreach (var text in new[] { r.Verdict }.Concat(r.GoodFor).Concat(r.Tips).Concat(r.Parts.Select(p => p.Name))
                         .Concat(r.Parts.SelectMany(p => p.Detail.Split('\n'))))
                Assert.True(keys!.ContainsKey(text), $"Missing Spanish: {text}");
        }
    }
}
