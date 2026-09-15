using Avalonia;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace LibProsperoPkg.Gui;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        string baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(baseDir))
            Directory.SetCurrentDirectory(baseDir);
        TryLoadBundledLibCrypto(baseDir);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // macOS 27 aborts if the packer falls through to /usr/lib/libcrypto.dylib.
    // Ship OpenSSL 3 next to the exe so SHA3 uses libcrypto.3.dylib first.
    private static void TryLoadBundledLibCrypto(string baseDir)
    {
        if (!OperatingSystem.IsMacOS() || string.IsNullOrEmpty(baseDir))
            return;
        string path = Path.Combine(baseDir, "libcrypto.3.dylib");
        if (File.Exists(path))
            NativeLibrary.TryLoad(path, out _);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
