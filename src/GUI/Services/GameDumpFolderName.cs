using LibProsperoPkg;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LibProsperoPkg.Gui.Services;

internal static class GameDumpFolderName
{
    private static readonly Regex TitleIdInContentId =
        new(@"-([A-Z]{4}\d{5})_00-", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly char[] Apostrophes = ['\'', '\u2018', '\u2019', '`'];

    public static string Build(string? title, string? version, string? titleId, string? contentId)
    {
        string id = ResolveTitleId(titleId, contentId);
        string ver = SanitizeVersion(version);
        string name = SanitizeTitle(title);
        if (name.Length == 0 && id.Length == 0)
            return "Unpacked";
        if (name.Length == 0)
            return ver.Length == 0 ? id : id + "_" + ver;
        if (id.Length == 0)
            return ver.Length == 0 ? name : name + "_" + ver;
        return ver.Length == 0 ? name + "_" + id : name + "_" + ver + "_" + id;
    }

    public static string ResolveTitleId(string? titleId, string? contentId)
    {
        if (ProsperoPackageBuilder.IsValidTitleId(titleId))
            return titleId!.ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(contentId))
        {
            Match match = TitleIdInContentId.Match(contentId.Trim());
            if (match.Success)
                return match.Groups[1].Value;
            if (contentId.Length >= 16)
            {
                string mid = contentId.Substring(7, 9);
                if (ProsperoPackageBuilder.IsValidTitleId(mid))
                    return mid.ToUpperInvariant();
            }
        }
        return "";
    }

    public static string ResolveOutputDirectory(string selectedFolder, string folderName)
    {
        string root = Path.GetFullPath(selectedFolder.Trim());
        string leaf = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.Equals(leaf, folderName, StringComparison.OrdinalIgnoreCase))
            return root;
        return Path.Combine(root, folderName);
    }

    internal static string SanitizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return "";
        var chars = title.Trim().Where(c => !Apostrophes.Contains(c)).ToArray();
        var text = new string(chars);
        var built = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (built.Length > 0 && built[^1] != '_')
                    built.Append('_');
                continue;
            }
            if (c is '-' or '&')
            {
                built.Append(c);
                continue;
            }
            if (char.IsLetterOrDigit(c))
            {
                built.Append(c);
                continue;
            }
            if (built.Length > 0 && built[^1] != '_')
                built.Append('_');
        }
        string cleaned = built.ToString().Trim('_');
        while (cleaned.Contains("__", StringComparison.Ordinal))
            cleaned = cleaned.Replace("__", "_", StringComparison.Ordinal);
        if (cleaned.Length > 60)
            cleaned = cleaned[..60].Trim('_');
        return cleaned;
    }

    private static string SanitizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return "";
        var built = new StringBuilder(version.Length);
        foreach (char c in version.Trim())
        {
            if (char.IsDigit(c) || c == '.')
                built.Append(c);
        }
        return built.ToString().Trim('.');
    }
}
