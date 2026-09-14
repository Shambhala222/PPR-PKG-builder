using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace LibProsperoPkg.Gui.Services;

/// <summary>
/// Moves macOS AppleDouble (<c>._*</c>) and desktop junk out of the source
/// tree for the duration of a pack. Those files are not game data. Hard-links
/// are not used — exFAT (the PS5 SSD) cannot create them.
/// </summary>
internal static class MacSidecarFilter
{
    public static bool IsSidecar(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return false;
        return fileName.StartsWith("._", StringComparison.Ordinal)
            || fileName.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShouldFilter(string source)
    {
        int count = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(source, "._*", SearchOption.TopDirectoryOnly))
            {
                if (IsSidecar(Path.GetFileName(file)) && ++count >= 5)
                    return true;
            }

            foreach (string dir in Directory.EnumerateDirectories(source))
            {
                foreach (string file in Directory.EnumerateFiles(dir, "._*", SearchOption.TopDirectoryOnly))
                {
                    if (IsSidecar(Path.GetFileName(file)) && ++count >= 5)
                        return true;
                }
            }
        }
        catch (IOException)
        {
            return count >= 5;
        }

        return false;
    }

    public static int Park(
        string source,
        string park,
        CancellationToken token,
        Action<string> log,
        Action<int, long>? progress)
    {
        Directory.CreateDirectory(park);
        int moved = 0;
        DateTime lastLog = DateTime.UtcNow;
        var files = new List<string>();

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            if (IsUnder(file, park))
                continue;
            if (!IsSidecar(Path.GetFileName(file)))
                continue;
            files.Add(file);
        }

        foreach (string file in files)
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(file))
                continue;

            string rel = Path.GetRelativePath(source, file);
            string dest = Path.Combine(park, rel);
            string? dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            try
            {
                if (File.Exists(dest))
                    File.Delete(dest);
                File.Move(file, dest);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                continue;
            }

            moved++;
            if ((DateTime.UtcNow - lastLog).TotalMilliseconds < 350)
                continue;
            lastLog = DateTime.UtcNow;
            progress?.Invoke(moved, 0);
            log("Progress 1/7 — Preparing parked " + moved.ToString("N0") + " Mac sidecar files");
        }

        log("Parked " + moved.ToString("N0") + " Mac sidecar files (._* / .DS_Store). They are not packaged.");
        return moved;
    }

    public static void Restore(string park, string source, Action<string> log)
    {
        if (string.IsNullOrEmpty(park) || !Directory.Exists(park))
            return;

        int restored = 0;
        foreach (string file in Directory.EnumerateFiles(park, "*", SearchOption.AllDirectories))
        {
            if (!File.Exists(file))
                continue;
            string rel = Path.GetRelativePath(park, file);
            string dest = Path.Combine(source, rel);
            string? dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            try
            {
                if (File.Exists(dest))
                    File.Delete(dest);
                File.Move(file, dest);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                continue;
            }
            restored++;
        }

        try { Directory.Delete(park, recursive: true); }
        catch (IOException) { }

        if (restored > 0)
            log("Restored " + restored.ToString("N0") + " Mac sidecar files to the dump.");
    }

    private static bool IsUnder(string path, string root)
    {
        string p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        string r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        return p.Equals(r, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
