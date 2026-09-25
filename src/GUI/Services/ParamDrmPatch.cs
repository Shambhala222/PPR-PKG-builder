using System;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LibProsperoPkg.Gui.Services;

/// <summary>
/// If <c>sce_sys/param.json</c> has <c>applicationDrmType</c> other than
/// <c>standard</c> (free, upgradable, and anything else), rewrite it to
/// <c>standard</c> for the pack so the PS5 does not show a lock. The original file
/// is written back afterwards, byte for byte.
/// </summary>
internal sealed class ParamDrmPatch : IDisposable
{
    private readonly string _path;
    private readonly byte[] _original;
    private bool _restored;

    private ParamDrmPatch(string path, byte[] original)
    {
        _path = path;
        _original = original;
    }

    public static bool NeedsStandard(string sourceRoot)
    {
        string? path = FindParamJson(sourceRoot);
        return path is not null && TryReadToken(path, out string? token)
            && NeedsForceStandard(token);
    }

    public static ParamDrmPatch? ApplyIfFree(string sourceRoot, Action<string> log)
        => ApplyIfNeeded(sourceRoot, log);

    public static ParamDrmPatch? ApplyIfNeeded(string sourceRoot, Action<string> log)
    {
        string? path = FindParamJson(sourceRoot);
        if (path is null)
            return null;

        if (!TryReadToken(path, out string? token) || !NeedsForceStandard(token))
            return null;

        string shown = string.IsNullOrWhiteSpace(token) ? "(missing)" : token.Trim();
        if (!CanWrite(path))
        {
            log("param.json applicationDrmType is \"" + shown + "\", but the file is not writable - packing it as-is.");
            return null;
        }

        byte[] original = File.ReadAllBytes(path);
        if (!TryWriteStandard(path, original))
        {
            log("param.json applicationDrmType is \"" + shown + "\", but it could not be rewritten - packing it as-is.");
            return null;
        }

        log("param.json applicationDrmType is \"" + shown + "\" - packing as \"standard\" so the game is not locked on PS5.");
        return new ParamDrmPatch(path, original);
    }

    public static bool WriteStandardCopy(string sourceParam, string destParam)
    {
        byte[] original = File.ReadAllBytes(sourceParam);
        string? dir = Path.GetDirectoryName(destParam);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        return TryWriteStandard(destParam, original);
    }

    public void Dispose()
    {
        if (_restored)
            return;
        _restored = true;
        try
        {
            File.WriteAllBytes(_path, _original);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The pack already finished or failed; the dump should still get its original JSON back.
        }
    }

    internal static string? FindParamJson(string sourceRoot)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
            return null;

        string sceSys = Path.Combine(sourceRoot, "sce_sys");
        if (!Directory.Exists(sceSys))
        {
            foreach (string child in Directory.EnumerateDirectories(sourceRoot))
            {
                string nested = Path.Combine(child, "sce_sys");
                if (Directory.Exists(nested))
                {
                    sceSys = nested;
                    break;
                }
            }
        }

        if (!Directory.Exists(sceSys))
            return null;

        string direct = Path.Combine(sceSys, "param.json");
        if (File.Exists(direct))
            return direct;

        foreach (string file in Directory.EnumerateFiles(sceSys))
        {
            if (string.Equals(Path.GetFileName(file), "param.json", StringComparison.OrdinalIgnoreCase))
                return file;
        }

        return null;
    }

    private static bool TryReadToken(string path, out string? token)
    {
        token = null;
        try
        {
            JsonNode? node = JsonNode.Parse(File.ReadAllText(path));
            token = node?["applicationDrmType"]?.GetValue<string>();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static bool IsStandard(string? token)
        => string.Equals(token?.Trim(), "standard", StringComparison.OrdinalIgnoreCase);

    private static bool NeedsForceStandard(string? token)
        => !IsStandard(token);

    private static bool CanWrite(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryWriteStandard(string path, byte[] original)
    {
        try
        {
            string text = Encoding.UTF8.GetString(original);
            if (text.Length > 0 && text[0] == '\uFEFF')
                text = text[1..];
            JsonNode? node = JsonNode.Parse(text);
            if (node is not JsonObject obj)
                return false;
            obj["applicationDrmType"] = "standard";
            var options = new JsonSerializerOptions
            {
                WriteIndented = text.Contains('\n'),
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            File.WriteAllText(path, obj.ToJsonString(options), new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }
}
