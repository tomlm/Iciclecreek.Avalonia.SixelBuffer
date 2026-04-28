using System;
using Avalonia;
using Avalonia.Dialogs;
using Avalonia.Terminal;
using ControlCatalog;

namespace ControlCatalog
{
    internal static class Program
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
                .UseTerminal();
        }
    }
}
