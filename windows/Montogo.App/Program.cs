using System.Runtime.Versioning;

namespace Montogo.App;

static class Program
{
    [STAThread]
    [SupportedOSPlatform("windows8.0")]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }
}