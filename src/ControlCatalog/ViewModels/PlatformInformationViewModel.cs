using System.Runtime.InteropServices;
using MiniMvvm;

namespace ControlCatalog.ViewModels;

public class PlatformInformationViewModel : ViewModelBase
{
    public PlatformInformationViewModel()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            PlatformInfo = "Platform: Windows (Terminal/Sixel)";
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            PlatformInfo = "Platform: Linux (Terminal/Sixel)";
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            PlatformInfo = "Platform: macOS (Terminal/Sixel)";
        else
            PlatformInfo = "Platform: Unknown (Terminal/Sixel)";
    }

    public string? PlatformInfo { get; }
}
