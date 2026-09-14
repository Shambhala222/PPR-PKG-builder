using System;
using System.IO;

namespace LibProsperoPkg.Gui.Services;

/// <summary>
/// Read-only exFAT mounts cannot rewrite param.json. Build a tiny tree of
/// symlinks plus one copied JSON set to "standard", then pack from that tree.
/// The mounted dump stays where it is.
/// </summary>
internal sealed class ParamMountOverlay : IDisposable
{
    private readonly string _root;
    private bool _disposed;

    private ParamMountOverlay(string root)
    {
        _root = root;
    }

    public string Root => _root;

    public static ParamMountOverlay Create(
        string mountRoot,
        string tempRoot,
        bool skipSidecars,
        Action<string> log)
    {
        string root = Path.Combine(tempRoot, "param-overlay-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        int links = 0;
        Walk(mountRoot, root, skipSidecars, ref links);
        log("exFAT image is read-only — only param.json is copied for \"standard\". The dump stays mounted (" + links.ToString("N0") + " links).");
        return new ParamMountOverlay(root);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static void Walk(string sourceDir, string destDir, bool skipSidecars, ref int links)
    {
        Directory.CreateDirectory(destDir);
        foreach (string dir in Directory.EnumerateDirectories(sourceDir))
        {
            string name = Path.GetFileName(dir);
            if (skipSidecars && name.StartsWith("._", StringComparison.Ordinal))
                continue;
            Walk(dir, Path.Combine(destDir, name), skipSidecars, ref links);
        }

        foreach (string file in Directory.EnumerateFiles(sourceDir))
        {
            string name = Path.GetFileName(file);
            if (skipSidecars && MacSidecarFilter.IsSidecar(name))
                continue;
            string dest = Path.Combine(destDir, name);
            if (IsParamJson(sourceDir, name))
            {
                if (!ParamDrmPatch.WriteStandardCopy(file, dest))
                    throw new IOException("Could not copy param.json for the read-only exFAT overlay.");
                continue;
            }

            File.CreateSymbolicLink(dest, file);
            links++;
        }
    }

    private static bool IsParamJson(string directory, string fileName)
    {
        if (!fileName.Equals("param.json", StringComparison.OrdinalIgnoreCase))
            return false;
        string parent = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return parent.Equals("sce_sys", StringComparison.OrdinalIgnoreCase);
    }
}
