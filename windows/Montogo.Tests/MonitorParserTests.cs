using Montogo.Driver;

namespace Montogo.Tests;

public class MonitorParserTests
{
    // Exact bytes the CLI wrote to stdout, with ANSI escape codes intact.
    // \x1B = ESC character (0x1B). Captured from:
    //   virtual-display-driver-cli.exe list
    private const string SingleMonitorRaw =
        "\x1B[4mVirtual monitors\x1B[0m\r\n" +
        "Monitor \x1B[32m0\x1B[39m:\r\n" +
        "\x1B[2m-\x1B[0m \x1B[32m1920\x1B[39m\x1B[2mx\x1B[0m\x1B[32m1080\x1B[39m\x1B[2m@\x1B[0m\x1B[34m60\x1B[39m\r\n";

    [Fact]
    public void SingleMonitor_ParsedCount_IsOne()
    {
        var result = MonitorInfo.ParseCliOutput(SingleMonitorRaw);
        Assert.Single(result);
    }

    [Fact]
    public void SingleMonitor_Fields_AreCorrect()
    {
        var m = MonitorInfo.ParseCliOutput(SingleMonitorRaw)[0];
        Assert.Equal("0",  m.Id);
        Assert.Equal(1920, m.Width);
        Assert.Equal(1080, m.Height);
        Assert.Equal(60,   m.RefreshRate);
    }

    [Fact]
    public void EmptyOutput_ReturnsEmpty()
    {
        Assert.Empty(MonitorInfo.ParseCliOutput(""));
    }

    [Fact]
    public void HeaderOnly_NoMonitors_ReturnsEmpty()
    {
        Assert.Empty(MonitorInfo.ParseCliOutput("\x1B[4mVirtual monitors\x1B[0m\r\n"));
    }

    [Fact]
    public void TwoMonitors_BothParsed()
    {
        // Simulated output for two monitors — verifies the state machine resets correctly.
        var two =
            "\x1B[4mVirtual monitors\x1B[0m\r\n" +
            "Monitor \x1B[32m0\x1B[39m:\r\n" +
            "\x1B[2m-\x1B[0m \x1B[32m1920\x1B[39m\x1B[2mx\x1B[0m\x1B[32m1080\x1B[39m\x1B[2m@\x1B[0m\x1B[34m60\x1B[39m\r\n" +
            "Monitor \x1B[32m1\x1B[39m:\r\n" +
            "\x1B[2m-\x1B[0m \x1B[32m2560\x1B[39m\x1B[2mx\x1B[0m\x1B[32m1440\x1B[39m\x1B[2m@\x1B[0m\x1B[34m144\x1B[39m\r\n";

        var result = MonitorInfo.ParseCliOutput(two);
        Assert.Equal(2, result.Count);
        Assert.Equal("1",  result[1].Id);
        Assert.Equal(2560, result[1].Width);
        Assert.Equal(144,  result[1].RefreshRate);
    }

    [Fact]
    public void ToString_FormatsCorrectly()
    {
        var m = new MonitorInfo("0", 1920, 1080, 60);
        Assert.Equal("0: 1920x1080@60", m.ToString());
    }
}
