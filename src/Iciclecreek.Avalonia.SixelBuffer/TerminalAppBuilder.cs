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
        /// </summary>
        /// <param name="builder">The app builder.</param>
        /// <param name="fps">Render frames per second (default 10). Higher values give
        /// smoother animations but use more CPU; lower values are gentler on the terminal.</param>
        public static AppBuilder UseSixelBuffer(this AppBuilder builder, int fps = 10)
        {
            TerminalPlatform.TargetFps = fps;
            builder.UseHarfBuzz();
            builder.UseSkia();
            builder.UseHarfBuzz();

            var terminal = new AnsiTerminal();
            Action? initialize = builder.RenderingSubsystemInitializer;

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
                    var sixelRenderInterface = new SixelRenderInterface(fallback!);

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
