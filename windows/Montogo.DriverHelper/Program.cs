using System.Diagnostics;
using Montogo.Driver;

namespace Montogo.DriverHelper;

/// <summary>
/// Runs ELEVATED. The virtual-display-rs v0.3.1 driver hosts an admin-only control pipe,
/// which a non-elevated app cannot open. This helper does it on the app's behalf: it adds
/// the virtual monitor on launch and removes it when the parent app exits — so the main,
/// network-facing app never has to run with administrator rights.
///
/// Usage: Montogo.DriverHelper.exe &lt;width&gt; &lt;height&gt; &lt;fps&gt; &lt;parentPid&gt;
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Log("started: " + string.Join(' ', args));

        if (args.Length < 4 ||
            !int.TryParse(args[0], out int width)  || !int.TryParse(args[1], out int height) ||
            !int.TryParse(args[2], out int fps)    || !int.TryParse(args[3], out int parentPid))
        {
            Log("invalid arguments");
            return 2;
        }

        // Set up: clear any leftover virtual monitors, then add ours.
        try
        {
            VirtualDisplayProtocol.SendToDriver(VirtualDisplayProtocol.RemoveAllCommand);
            VirtualDisplayProtocol.SendToDriver(VirtualDisplayProtocol.BuildAddCommand(width, height, fps));
            Log($"added {width}x{height}@{fps}");
        }
        catch (Exception ex)
        {
            Log("setup failed: " + ex.Message);
            return 1;
        }

        // Stay alive tied to the app: when Montogo exits, remove the virtual monitor.
        try
        {
            Process.GetProcessById(parentPid).WaitForExit();
            Log("parent exited");
        }
        catch (Exception ex)
        {
            Log("parent watch ended: " + ex.Message);
        }

        try
        {
            VirtualDisplayProtocol.SendToDriver(VirtualDisplayProtocol.RemoveAllCommand);
            Log("removed monitor, exiting");
        }
        catch (Exception ex)
        {
            Log("teardown failed: " + ex.Message);
        }
        return 0;
    }

    // Lightweight file log — the helper is windowless, so this is the only way to see what
    // it did. Handy while bringing the v0.3.1 driver up; safe to ignore in normal use.
    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "montogo-helper.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch { /* logging is best-effort */ }
    }
}
