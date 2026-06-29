using Montogo.Driver;

namespace Montogo.Tests;

// Integration tests — these shell out to the real virtual-display-driver-cli.exe.
// They are skipped automatically when the CLI is not present so they won't break CI.
public class VirtualDisplayManagerTests
{
    private const string DevCliPath =
        @"C:\Users\ilke\Documents\virtual-display-rs Latest Dev\driver\virtual-display-driver-cli.exe";

    // Returns null when the CLI can't be found, which causes each test to skip.
    private static string? FindCli() =>
        File.Exists(DevCliPath) ? DevCliPath : DriverLocator.TryFindRelative();

    // -------------------------------------------------------------------------
    // List
    // -------------------------------------------------------------------------

    [Fact]
    public async Task List_DoesNotThrow()
    {
        var cli = FindCli();
        if (cli is null) return; // CLI not present — skip

        using var mgr = new VirtualDisplayManager(cli);
        var monitors = await mgr.ListMonitorsAsync();
        Assert.NotNull(monitors);
    }

    // -------------------------------------------------------------------------
    // Add → List → RemoveAll
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AddMonitor_AppearsInList()
    {
        var cli = FindCli();
        if (cli is null) return;

        using var mgr = new VirtualDisplayManager(cli);

        await mgr.RemoveAllAsync();                        // clean slate
        await mgr.AddMonitorAsync(1920, 1080, 60);

        var monitors = await mgr.ListMonitorsAsync();
        Assert.Contains(monitors, m =>
            m.Width == 1920 && m.Height == 1080 && m.RefreshRate == 60);

        await mgr.RemoveAllAsync();                        // clean up
    }

    [Fact]
    public async Task RemoveAll_LeavesNoMonitors()
    {
        var cli = FindCli();
        if (cli is null) return;

        using var mgr = new VirtualDisplayManager(cli);

        await mgr.AddMonitorAsync(1920, 1080, 60);        // ensure at least one exists
        await mgr.RemoveAllAsync();

        var monitors = await mgr.ListMonitorsAsync();
        Assert.Empty(monitors);
    }

    // -------------------------------------------------------------------------
    // Constructor guard
    // -------------------------------------------------------------------------

    [Fact]
    public void Constructor_ThrowsWhenCliMissing()
    {
        Assert.Throws<FileNotFoundException>(() =>
            new VirtualDisplayManager(@"C:\nonexistent\virtual-display-driver-cli.exe"));
    }
}
