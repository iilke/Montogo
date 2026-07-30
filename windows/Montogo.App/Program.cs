using System.Runtime.Versioning;

namespace Montogo.App;

static class Program
{
    [STAThread]
    [SupportedOSPlatform("windows8.0")]
    static void Main()
    {
        // Multiple instances fight over UDP port 47921 and the virtual display,
        // and the loser sits in the tray looking connected while doing nothing.
        using var mutex = new Mutex(initiallyOwned: true, @"Local\MontogoApp", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("Montogo is already running — check the system tray.",
                "Montogo", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }
}