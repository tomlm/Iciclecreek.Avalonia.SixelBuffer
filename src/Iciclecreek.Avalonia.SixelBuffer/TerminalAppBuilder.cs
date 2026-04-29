using System;
using Avalonia;
using Avalonia.Platform;
using Iciclecreek.Avalonia.SixelBuffer.Platform;
using Iciclecreek.Avalonia.SixelBuffer.Rendering;
using Iciclecreek.Avalonia.SixelBuffer.Terminal;

namespace Iciclecreek.Avalonia.SixelBuffer
{
    public static class TerminalAppBuilder
    {
        /// <summary>
        ///     Configure Avalonia to render to a terminal using Sixel graphics.
        ///     Requires .UseSkia() to be called first.
        /// </summary>
        public static AppBuilder UseTerminal(this AppBuilder builder)
        {
            builder.UseHarfBuzz();
            return builder
                .UseSkia()
                .UseHarfBuzz()
                .UseTerminalInternal(new AnsiTerminal());
        }

        /// <summary>
        ///     Configure Avalonia to render to a terminal using Sixel graphics
        ///     with a custom ITerminal implementation.
        /// </summary>
        private static AppBuilder UseTerminalInternal(this AppBuilder builder, ITerminal terminal)
        {
            Action initialize = builder.RenderingSubsystemInitializer;

            return builder
                .UseWindowingSubsystem(() =>
                {
                    // Register terminal before platform init so ConsoleWindow can find it
                    AvaloniaLocator.CurrentMutable.Bind<ITerminal>().ToConstant(terminal);

                    // Save Skia's font manager and text shaper before platform init
                    var platformFontManager = AvaloniaLocator.Current.GetService<IFontManagerImpl>();
                    var platformTextShaper = AvaloniaLocator.Current.GetService<ITextShaperImpl>();

                    new TerminalPlatform().Initialize();

                    // Restore Skia's font manager and text shaper (platform may have overwritten them)
                    if (platformFontManager != null)
                        AvaloniaLocator.CurrentMutable.Bind<IFontManagerImpl>().ToConstant(platformFontManager);
                    if (platformTextShaper != null)
                        AvaloniaLocator.CurrentMutable.Bind<ITextShaperImpl>().ToConstant(platformTextShaper);

                    // Prepare the terminal (alternate screen, detect cell size, enable mouse)
                    terminal.PrepareConsole();
                }, nameof(TerminalPlatform))
                .UseRenderingSubsystem(() =>
                {
                    if (initialize != null) initialize();

                    var fallback = AvaloniaLocator.Current.GetService<IPlatformRenderInterface>();
                    var sixelRenderInterface = new SixelRenderInterface(fallback);

                    AvaloniaLocator.CurrentMutable
                        .Bind<IPlatformRenderInterface>().ToConstant(sixelRenderInterface);
                }, nameof(SixelRenderInterface));
        }
        
        /// <summary>
        ///     Start the application with a console lifetime.
        /// </summary>
        public static int StartWithConsoleLifetime(this AppBuilder builder, string[] args)
        {
            var lifetime = new TerminalLifetime { Args = args };
            builder.SetupWithLifetime(lifetime);
            return lifetime.Start(args);
        }
    }
}
