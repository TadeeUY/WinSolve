using System.Xml.Linq;
using WinSolve.Services;
using Xunit;

namespace WinSolve.Tests;

public class BatteryReportTests
{
    private const string Sample = """
        <?xml version="1.0" encoding="utf-8"?>
        <BatteryReport xmlns="http://schemas.microsoft.com/battery/2012">
          <Batteries>
            <Battery>
              <Id>DELL 7FHHV09</Id><Manufacturer>SMP</Manufacturer><Chemistry>LiP</Chemistry>
              <DesignCapacity>56000</DesignCapacity><FullChargeCapacity>44800</FullChargeCapacity><CycleCount>312</CycleCount>
            </Battery>
          </Batteries>
          <RuntimeEstimates>
            <FullChargeCapacity><ActiveRuntime>PT4H30M</ActiveRuntime></FullChargeCapacity>
            <DesignCapacity><ActiveRuntime>PT5H37M30S</ActiveRuntime></DesignCapacity>
          </RuntimeEstimates>
          <History>
            <HistoryEntry StartDate="2025-01-01T00:00:00" EndDate="2025-01-08T00:00:00" DesignCapacity="56000" FullChargeCapacity="55000" />
            <HistoryEntry StartDate="2026-01-01T00:00:00" EndDate="2026-01-08T00:00:00" DesignCapacity="56000" FullChargeCapacity="44800" />
          </History>
        </BatteryReport>
        """;

    [Fact]
    public void Parses_capacity_cycles_runtime_and_history()
    {
        var b = Assert.Single(BatteryReport.Parse(XDocument.Parse(Sample)));
        Assert.Equal(80, b.HealthPercent, 1);
        Assert.Equal(312, b.CycleCount);
        Assert.Equal(TimeSpan.FromMinutes(270), b.RuntimeNow);
        Assert.Equal(new TimeSpan(5, 37, 30), b.RuntimeWhenNew);
        Assert.Equal(2, b.History.Count);
        Assert.Equal("LiP", b.Chemistry);
    }

    [Fact]
    public void No_batteries_gives_an_empty_list()
        => Assert.Empty(BatteryReport.Parse(XDocument.Parse("<BatteryReport><Batteries /></BatteryReport>")));
}
