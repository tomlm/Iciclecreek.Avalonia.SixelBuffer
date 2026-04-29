using Avalonia;
using Avalonia.Dialogs;
using Iciclecreek.Avalonia.SixelBuffer;

namespace ControlCatalog.Terminal
{
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
#pragma warning disable CA1416 // Validate platform compatibility
            return AppBuilder.Configure<App>()
                .UseStandardRuntimePlatformSubsystem()
                .WithInterFont()
                .UseSixelBuffer()
#if DEBUG
                .WithDeveloperTools()
#endif
                .UseManagedSystemDialogs();
#pragma warning restore CA1416 // Validate platform compatibility

        }
    }
}
