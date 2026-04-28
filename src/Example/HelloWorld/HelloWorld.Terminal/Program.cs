using Avalonia;
using System;
using Avalonia;
using Avalonia.Dialogs;
using Iciclecreek.Avalonia.TerminalFramebuffer;

namespace HelloWorld.Terminal
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
