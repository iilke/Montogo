using System.Diagnostics;

namespace Montogo.Driver;

/// <summary>
/// Manages virtual monitors by shelling out to virtual-display-driver-cli.exe.
/// The VddUserSession service must already be running — this class only controls monitors, not the service.
/// </summary>
public sealed class VirtualDisplayManager : IDisposable
{
    private readonly string _cliPath;

    public VirtualDisplayManager(string cliPath)
    {
        if (!File.Exists(cliPath))
            throw new FileNotFoundException($"{DriverLocator.CliName} not found at the given path.", cliPath);
        _cliPath = cliPath;
    }

    public Task AddMonitorAsync(int width, int height, int refreshRate, CancellationToken ct = default)
        => RunAsync($"add {width}x{height}@{refreshRate}", ct);

    // monitorId comes from ListMonitorsAsync → MonitorInfo.Id
    public Task RemoveMonitorAsync(string monitorId, CancellationToken ct = default)
        => RunAsync($"remove {monitorId}", ct);

    public Task RemoveAllAsync(CancellationToken ct = default)
        => RunAsync("remove-all", ct);

    public Task PersistAsync(CancellationToken ct = default)
        => RunAsync("persist", ct);

    public async Task<IReadOnlyList<MonitorInfo>> ListMonitorsAsync(CancellationToken ct = default)
    {
        var output = await RunAsync("list", ct);
        return MonitorInfo.ParseCliOutput(output);
    }

    private async Task<string> RunAsync(string arguments, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _cliPath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute  = false,
                CreateNoWindow   = true,
            }
        };

        process.Start();

        // Read both streams concurrently before waiting to avoid deadlock if
        // either buffer fills up while the process is blocked writing the other.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"virtual-display-driver-cli exited {process.ExitCode}: {stderr.Trim()}");

        return stdout;
    }

    public void Dispose() { }
}
