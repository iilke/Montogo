using System.ComponentModel;
using System.Diagnostics;

namespace Montogo.Driver;

/// <summary>
/// Sets up the virtual monitor through an <b>elevated helper process</b>
/// (<c>Montogo.DriverHelper.exe</c>) so the main app can stay non-elevated.
///
/// The virtual-display-rs v0.3.1 driver's control pipe is admin-only, so a normal-user
/// process cannot add a monitor. Rather than run the whole (network-facing) app as
/// administrator, we launch a tiny helper elevated once: it adds the monitor on start and
/// removes it when this process exits.
/// </summary>
public sealed class VirtualDisplayManager : IDisposable
{
    public const string HelperExeName = "Montogo.DriverHelper.exe";

    /// <summary>
    /// Launches the elevated helper (one UAC prompt) to add a <paramref name="width"/>×
    /// <paramref name="height"/>@<paramref name="refreshRate"/> virtual monitor. The helper
    /// removes it again when this process exits. Does not wait for the monitor to appear —
    /// the caller polls the OS for that.
    /// </summary>
    public Task StartAsync(int width, int height, int refreshRate, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        string exe = Path.Combine(AppContext.BaseDirectory, HelperExeName);
        if (!File.Exists(exe))
            throw new FileNotFoundException(
                $"{HelperExeName} was not found next to the app; it sets up the virtual display with elevated rights.",
                exe);

        var psi = new ProcessStartInfo
        {
            FileName        = exe,
            Arguments       = $"{width} {height} {refreshRate} {Environment.ProcessId}",
            UseShellExecute = true,                       // required for elevation
            Verb            = "runas",                    // prompt for elevation (UAC)
            WindowStyle     = ProcessWindowStyle.Hidden,
        };

        try
        {
            Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            throw new InvalidOperationException(
                "Montogo needs a one-time administrator approval to set up the virtual display, " +
                "but the elevation prompt was declined.");
        }

        return Task.CompletedTask;
    }

    // Nothing to release here: the elevated helper watches this process and removes the
    // virtual monitor when we exit.
    public void Dispose() { }
}
