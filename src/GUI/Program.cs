using Avalonia;
using System;
using System.IO;

namespace LibProsperoPkg.Gui;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        string baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(baseDir))
            Directory.SetCurrentDirectory(baseDir);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
