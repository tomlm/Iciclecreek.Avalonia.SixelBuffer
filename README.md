[![Build and Test](https://github.com/tomlm/Iciclecreek.Avalonia.SixelBuffer/actions/workflows/BuildAndRunTests.yml/badge.svg)](https://github.com/tomlm/Iciclecreek.Avalonia.SixelBuffer/actions/workflows/BuildAndRunTests.yml)[![NuGet](https://img.shields.io/nuget/v/Iciclecreek.Avalonia.SixelBuffer.svg)](https://www.nuget.org/packages/Iciclecreek.Avalonia.SixelBuffer)

![Logo](https://raw.githubusercontent.com/tomlm/Iciclecreek.Avalonia.SixelBuffer/refs/heads/main/icon.png)


# Iciclecreek.Avalonia.SixelBuffer
![Screenshot](https://raw.githubusercontent.com/tomlm/Iciclecreek.Avalonia.SixelBuffer/refs/heads/main/screenshot.gif)

Run your Avalonia applications directly in a terminal using [Sixel](https://en.wikipedia.org/wiki/Sixel) graphics. This library provides a custom windowing and rendering subsystem that converts Avalonia's Skia output into Sixel image sequences, enabling full GUI applications to run inside a terminal emulator that supports Sixel graphics.

## Overview

Iciclecreek.Avalonia.SixelBuffer replaces Avalonia's native windowing platform with a terminal-based implementation:

- **Sixel rendering** -- Avalonia controls are rendered via Skia, quantized to at most 256 colors (exactly when the changed pixels hold 256 colors or fewer, Wu's quantizer otherwise), and output as Sixel escape sequences that define only the colors they use.
- **Kitty graphics** -- When the terminal answers the kitty graphics query, frames are sent with the kitty protocol instead: exact colors, zlib-compressed RGB, as a grid of 8x4-cell blocks each under a fixed image id, so a changed block is simply sent again and replaces itself. `UseSixelBuffer(protocol: ImageProtocol.Sixel)` or `ImageProtocol.Kitty` forces one, as does setting `FRAMEBUFFER_GRAPHICS=sixel` or `FRAMEBUFFER_GRAPHICS=kitty` in the environment.
- **Input handling** -- Keyboard and mouse input are read from stdin using VT/ANSI escape sequence parsing (including SGR extended mouse tracking, and SGR-Pixels / DECSET 1016 for pixel-resolution mouse positions where supported).
- **Frame diffing** -- Each frame is compared cell by cell with what the terminal shows. Only changed cells are sent, as one Sixel per horizontal run of changed cells, with SIMD-optimized sixel band construction. Runs the terminal has already been sent replay from a content-keyed cache (a closed popup or a reverted hover costs no re-encoding), and each frame goes out as a single write inside a DEC 2026 synchronized update where the terminal supports it.
- **Mouse pointer** -- At startup the library queries the terminal for OSC 22 pointer shape support (the kitty pointer-shapes protocol). When supported, Avalonia cursor changes are passed through to the terminal's native mouse pointer. Otherwise a composited software cursor is drawn into the frame.

Requires a terminal emulator with Sixel or kitty graphics, such as Windows Terminal, WezTerm, kitty, Ghostty, iTerm2, or foot. See https://www.arewesixelyet.com/ for a list of Sixel-capable terminals.

## Install

```
dotnet add package Iciclecreek.Avalonia.SixelBuffer 
```


## Configuring Program.cs

Create a new target console application and replace the contents of `Program.cs` with the following code to initialize Avalonia with the SixelBuffer platform:

```csharp
using Avalonia;
using Avalonia.Terminal;

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
            .UseSixelBuffer();
    }
}
```

## Notes
- **Terminal compatibility** -- Your terminal must support Sixel graphics. Tested with Windows Terminal, WezTerm, 
- **Render rate** -- The default render timer runs at 10 FPS to balance responsiveness with terminal throughput. NOTE: If you set too high input will be starved.
- **No native Menus** -- Don't use NativeMenu/NativeMenuItem as they won't work in terminal mode. Use Avalonia's Menu control instead.
- **No native popups** -- Popups and dropdown overlays are managed within the single terminal frame rather than as separate OS windows.
- **Avalonia version pinning** -- The library uses Avalonia private APIs and must be pinned to the exact Avalonia version (currently 12.0.2).
- **Mouse resolution** -- At startup the library queries the terminal (DECRQM) for SGR-Pixels mouse mode (DECSET 1016). If supported, mouse positions are reported at pixel resolution and the software cursor (when used) follows the pointer pixel by pixel. Otherwise mouse input falls back to the character cell grid, so positions snap to the top-left pixel of the cell under the pointer.
