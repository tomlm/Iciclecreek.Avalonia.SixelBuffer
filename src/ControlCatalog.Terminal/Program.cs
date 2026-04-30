using Avalonia;
using Avalonia.Dialogs;
using Iciclecreek.Avalonia.SixelBuffer;
using System.Runtime.Versioning;

namespace ControlCatalog.Terminal
{
    [SupportedOSPlatform("windows"), SupportedOSPlatform("macos"), SupportedOSPlatform("linux")]
    internal class Program
    {

        [STAThread]
        private static void Main(string[] args)
        {
            BuildAvaloniaApp()
                .StartWithConsoleLifetime(args);
        }

        public static AppBuilder BuildAvaloniaApp()
        {
            return AppBuilder.Configure<App>()
                .UseStandardRuntimePlatformSubsystem()
                .WithInterFont()
                .UseSixelBuffer()
#if DEBUG
                .WithDeveloperTools()
#endif
                .UseManagedSystemDialogs();

        }
    }
}
