# AvaloniaTerminalBuffer

Render Avalonia applications directly in your terminal using [Sixel](https://en.wikipedia.org/wiki/Sixel) graphics. This library provides a custom windowing and rendering subsystem that converts Avalonia's Skia output into Sixel image sequences, enabling full GUI applications to run inside a terminal emulator.

## Overview

AvaloniaTerminalBuffer replaces Avalonia's native windowing platform with a terminal-based implementation:

- **Sixel rendering** -- Avalonia controls are rendered via Skia, quantized to a 256-color palette, and output as Sixel escape sequences.
- **Input handling** -- Keyboard and mouse input are read from stdin using VT/ANSI escape sequence parsing (including SGR extended mouse tracking).
- **Frame diffing** -- Only changed regions are re-rendered, with dirty-rect tracking and SIMD-optimized sixel band construction for performance.
- **Software cursor** -- A composited software cursor is drawn into the frame since hardware cursors aren't available in terminal mode.

Requires a Sixel-capable terminal emulator such as WezTerm, iTerm2, mlterm, foot, or contour.

## Install

```
dotnet add package AvaloniaTerminalBuffer
```


## Configuring Program.cs

Replace the standard desktop lifetime with the terminal lifetime:

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
            .UseTerminal();
    }
}
```


## Notes
- **Terminal compatibility** -- Your terminal must support Sixel graphics. Tested with Windows Terminal, WezTerm, iTerm2, and mlterm.
- **Render rate** -- The default render timer runs at 10 FPS to balance responsiveness with terminal throughput.
- **No native Menus** -- Don't use NativeMenu/NativeMenuItem as they won't work in terminal mode. Use Avalonia's Menu control instead.
- **No native popups** -- Popups and dropdown overlays are managed within the single terminal frame rather than as separate OS windows.
- **Avalonia version pinning** -- The library uses Avalonia private APIs and must be pinned to the exact Avalonia version (currently 12.0.1).
