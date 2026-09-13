namespace LibProsperoPkg.Gui.Services;

/// <summary>
/// Open-file offset probing used to live here. The macOS fcntl(F_GETPATH)
/// P/Invoke smashed the stack (SIGBUS) when Build started. The 02:58 UI
/// still calls this API; it now returns nothing so progress comes from
/// log lines and work-file sizes only.
/// </summary>
internal static class FileProgressProbe
{
    public readonly record struct OpenRead(string Path, long Position, long Length);

    public static OpenRead? FindLargestPartialRead(string? pathContains) => null;
}
