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
        ///     Configure Avalonia to render to a terminal using Sixel or kitty graphics.
        /// </summary>
        /// <param name="builder">The app builder.</param>
        /// <param name="fps">Render frames per second (default 60, Avalonia's usual desktop rate). Only frames with something
        /// changed are sent, so an idle app costs nothing at any rate; higher rates give smoother
        /// scrolling and animation at the price of more bytes to the terminal while things move.
        /// The <c>SIXELBUFFER_FPS</c> environment variable overrides it.</param>
        /// <param name="protocol">The image protocol to use. <see cref="ImageProtocol.Auto"/> takes
        /// kitty graphics when the terminal answers the kitty query and Sixel otherwise.</param>
        public static AppBuilder UseSixelBuffer(this AppBuilder builder, int fps = 60,
            ImageProtocol protocol = ImageProtocol.Auto)
        {
            // SIXELBUFFER_FPS overrides the rate without a rebuild, for trying rates against a
            // particular terminal.
            if (int.TryParse(Environment.GetEnvironmentVariable("SIXELBUFFER_FPS"), out int envFps) && envFps > 0)
                fps = envFps;
            TerminalPlatform.TargetFps = fps;
            builder.UseHarfBuzz();
            builder.UseSkia();
            builder.UseHarfBuzz();

            var terminal = new AnsiTerminal { PreferredProtocol = protocol };
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
            try
            {
                var lifetime = new TerminalLifetime { Args = args };
                builder.SetupWithLifetime(lifetime);
                return lifetime.Start(args);
            }
            catch (NotSupportedException ex)
            {
                // Restore the console so the error message is visible
                var terminal = AvaloniaLocator.Current.GetService<ITerminal>();
                if (terminal is IDisposable disposable)
                    disposable.Dispose();

                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }
    }
}
