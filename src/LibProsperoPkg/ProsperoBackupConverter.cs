// LibProsperoPkg - A library for building and inspecting PS5 packages.
// Copyright (C) 2026 SvenGDK
//
// Turns a decrypted application backup into an installable debug package (fPKG). A decrypted backup
// carries the application's data and metadata in the clear alongside its executable modules in two
// forms: the on-disk signed/encrypted modules at their normal paths, and raw ELF copies of the same
// modules under a "decrypted" subtree. This converter assembles a single source tree that keeps the
// plaintext data and metadata, substitutes each raw ELF for its signed/encrypted counterpart, then
// hands the tree to <see cref="ProsperoPackageBuilder"/> with fake-signing enabled. The result is a
// finalized debug image whose mount key is derived from the content id and passcode, so it installs
// and runs on a debug-mode console with no license record.

using LibProsperoPkg.Content;
using LibProsperoPkg.License;
using LibProsperoPkg.Metadata;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LibProsperoPkg;

/// <summary>
/// Options for <see cref="ProsperoBackupConverter.Convert"/>. Only <see cref="BackupFolder"/> and
/// <see cref="OutputFolder"/> are required; the content id, passcode and version default from the
/// backup's <c>sce_sys/param.json</c>.
/// </summary>
public sealed class ProsperoBackupConversionOptions
{
    /// <summary>Root folder of the decrypted backup (the folder that holds <c>sce_sys/</c>).</summary>
    public string BackupFolder { get; set; } = "";

    /// <summary>Folder the finished <c>*.pkg</c> is written to.</summary>
    public string OutputFolder { get; set; } = "";

    /// <summary>
    /// Name of the subtree that holds the raw ELF copies of the executable modules, relative to
    /// <see cref="BackupFolder"/>. Defaults to <c>decrypted</c>.
    /// </summary>
    public string DecryptedSubfolder { get; set; } = "decrypted";

    /// <summary>
    /// 36-character content id. When left empty it is read from the backup's
    /// <c>sce_sys/param.json</c> <c>contentId</c> field.
    /// </summary>
    public string ContentId { get; set; } = "";

    /// <summary>32-character passcode. Defaults to the all-zero debug passcode.</summary>
    public string Passcode { get; set; } = new string('0', 32);

    /// <summary>
    /// Content/master version formatted <c>NN.NN</c>. When left empty it is derived from the backup's
    /// <c>param.json</c>, falling back to <c>01.00</c>.
    /// </summary>
    public string Version { get; set; } = "";

    /// <summary>
    /// Folder used to assemble the merged source tree. When left empty a temporary folder is created
    /// under <see cref="OutputFolder"/> and removed after the build unless <see cref="KeepStaging"/>
    /// is set.
    /// </summary>
    public string StagingFolder { get; set; } = "";

    /// <summary>When true the assembled source tree is kept after the build. Off by default.</summary>
    public bool KeepStaging { get; set; }

    /// <summary>
    /// When true the backup's <c>sce_sys/about/right.sprx</c> is dropped so the builder injects its
    /// embedded debug module instead of fake-signing the backup's own module. Off by default (the
    /// backup's module is substituted and fake-signed like every other executable).
    /// </summary>
    public bool UseEmbeddedRightSprx { get; set; }

    /// <summary>
    /// Fake-self options (app/firmware version, authority-id override) applied to every module. When
    /// <see langword="null"/> the defaults are used.
    /// </summary>
    public FselfOptions? FselfOptions { get; set; }
}

/// <summary>The result of a backup conversion.</summary>
public sealed class ProsperoBackupConversionResult
{
    /// <summary>Path to the finished debug package.</summary>
    public required string OutputPath { get; init; }

    /// <summary>
    /// The debug grant for the package: the content id and passcode whose EKPFS the mount path
    /// recomputes. <see cref="ProsperoDebugLicense.RequiresRif"/> is always false.
    /// </summary>
    public required ProsperoDebugLicense DebugLicense { get; init; }

    /// <summary>
    /// Relative paths of the executable modules that were substituted with their raw ELF copy from the
    /// decrypted subtree (and then fake-signed).
    /// </summary>
    public required IReadOnlyList<string> SubstitutedModules { get; init; }

    /// <summary>
    /// Relative paths of executable modules that were already raw ELF at their normal path and were
    /// fake-signed in place.
    /// </summary>
    public required IReadOnlyList<string> PlaintextModules { get; init; }

    /// <summary>
    /// Relative paths of signed/encrypted executable modules with no raw ELF copy in the decrypted
    /// subtree. They are packed unchanged and will not run on a debug-mode console; each also raises a
    /// warning.
    /// </summary>
    public required IReadOnlyList<string> UnresolvedModules { get; init; }

    /// <summary>Non-fatal warnings from the assembly and the underlying build.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>
    /// Launch-readiness of the assembled source tree: how each executable module classifies and whether
    /// the tree meets the debug-mode console launch conditions.
    /// </summary>
    public required ProsperoLaunchReadinessReport LaunchReadiness { get; init; }

    /// <summary>The assembled source tree, when <see cref="ProsperoBackupConversionOptions.KeepStaging"/> is set; otherwise empty.</summary>
    public required string StagingFolder { get; init; }
}

/// <summary>
/// A merged backup tree ready to pack. The 2.5.0 in-memory builder is not used; the GUI packs this
/// folder with the file-backed 1.2.0 library.
/// </summary>
public sealed class ProsperoBackupAssemblyResult
{
    public required string StagingFolder { get; init; }
    public required string ContentId { get; init; }
    public required string Passcode { get; init; }
    public required string Version { get; init; }
    public required IReadOnlyList<string> SubstitutedModules { get; init; }
    public required IReadOnlyList<string> PlaintextModules { get; init; }
    public required IReadOnlyList<string> UnresolvedModules { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>
/// Converts a decrypted application backup into an installable debug package. See the file header for
/// the assembly model.
/// </summary>
public static class ProsperoBackupConverter
{
    private const uint ElfMagic = 0x464C457FU;   // 0x7F 'E' 'L' 'F'
    private const uint SelfMagic = 0x1D3D154FU;  // SELF container magic on disk (fake and genuine alike)
    private const ulong AuthorityMask = 0xFF00000000000000UL;
    private const ulong FakeAuthorityPrefix = 0x3100000000000000UL;

    /// <summary>
    /// Assembles a merged source tree from <paramref name="options"/>' backup and builds a finalized
    /// debug package.
    /// </summary>
    /// <param name="options">The conversion inputs. See <see cref="ProsperoBackupConversionOptions"/>.</param>
    /// <param name="logger">Optional progress callback.</param>
    /// <returns>The output path plus the module classification and any warnings.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">The backup folder or a required field is invalid.</exception>
    public static ProsperoBackupConversionResult Convert(
        ProsperoBackupConversionOptions options, Action<string>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var log = logger ?? (_ => { });
        var assembled = Assemble(options, log);
        var debugLicense = ProsperoDebugLicense.Create(assembled.ContentId, assembled.Passcode);
        var warnings = new List<string>(assembled.Warnings);

        try
        {
            var buildOptions = new ProsperoBuildOptions
            {
                Mode = ProsperoPackageMode.Application,
                OutputFormat = ProsperoOutputFormat.DebugImage,
                SourceFolder = assembled.StagingFolder,
                OutputFolder = options.OutputFolder,
                ContentId = assembled.ContentId,
                Passcode = assembled.Passcode,
                Version = assembled.Version,
                FakeSignSelfModules = true,
                FselfOptions = options.FselfOptions,
                GenerateParamJsonIfMissing = true,
            };

            log($"Building debug package for {assembled.ContentId} from the assembled tree.");
            var buildResult = ProsperoPackageBuilder.Build(buildOptions, log);
            warnings.AddRange(buildResult.Warnings);
            var readiness = ProsperoLaunchReadiness.InspectAppRoot(assembled.StagingFolder);

            return new ProsperoBackupConversionResult
            {
                OutputPath = buildResult.OutputPath,
                DebugLicense = debugLicense,
                SubstitutedModules = assembled.SubstitutedModules,
                PlaintextModules = assembled.PlaintextModules,
                UnresolvedModules = assembled.UnresolvedModules,
                Warnings = warnings,
                LaunchReadiness = readiness,
                StagingFolder = options.KeepStaging ? assembled.StagingFolder : "",
            };
        }
        finally
        {
            if (!options.KeepStaging)
            {
                try
                {
                    if (Directory.Exists(assembled.StagingFolder))
                        Directory.Delete(assembled.StagingFolder, recursive: true);
                }
                catch (IOException)
                {
                    log($"Warning: could not remove the temporary tree '{assembled.StagingFolder}'.");
                }
            }
        }
    }

    /// <summary>
    /// Builds the merged source tree (hard-links data, copies substituted modules) and stops before
    /// packing. The GUI then fake-signs the modules and packs with the file-backed 1.2.0 builder.
    /// </summary>
    public static ProsperoBackupAssemblyResult Assemble(
        ProsperoBackupConversionOptions options, Action<string>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var log = logger ?? (_ => { });
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(options.BackupFolder) || !Directory.Exists(options.BackupFolder))
            throw new ArgumentException("Backup folder does not exist.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.OutputFolder) && string.IsNullOrWhiteSpace(options.StagingFolder))
            throw new ArgumentException("Output folder or staging folder was not specified.", nameof(options));

        string backup = ResolveBackupRoot(Path.GetFullPath(options.BackupFolder));
        string? sceSys = FindNamedDirectory(backup, "sce_sys");
        if (sceSys is null)
            throw new ArgumentException("Backup folder does not contain a sce_sys directory.", nameof(options));

        string decryptedRoot = Path.Combine(backup, options.DecryptedSubfolder);
        bool hasDecrypted = Directory.Exists(decryptedRoot);
        if (!hasDecrypted)
        {
            decryptedRoot = FindNamedDirectory(backup, options.DecryptedSubfolder)
                ?? FindNamedDirectoryRecursive(backup, options.DecryptedSubfolder, depth: 2)
                ?? decryptedRoot;
            hasDecrypted = Directory.Exists(decryptedRoot);
        }
        if (!hasDecrypted)
            warnings.Add($"No '{options.DecryptedSubfolder}' subtree was found; signed modules cannot be substituted and the package will not run.");

        string paramPath = FindFileIgnoreCase(sceSys, "param.json") ?? Path.Combine(sceSys, "param.json");
        var paramMeta = ReadParamMeta(paramPath);
        string contentId = !string.IsNullOrWhiteSpace(options.ContentId) ? options.ContentId.Trim() : paramMeta.ContentId;
        if (string.IsNullOrWhiteSpace(contentId))
            throw new ArgumentException("Content id was not supplied and could not be read from param.json.", nameof(options));

        string passcode = string.IsNullOrEmpty(options.Passcode) ? ProsperoDebugLicense.DefaultPasscode : options.Passcode;
        string version = !string.IsNullOrWhiteSpace(options.Version) ? options.Version
            : (!string.IsNullOrWhiteSpace(paramMeta.Version) ? paramMeta.Version : "01.00");

        string staging = string.IsNullOrWhiteSpace(options.StagingFolder)
            ? Path.Combine(options.OutputFolder, "." + SafeName(contentId) + ".convert.tmp")
            : Path.GetFullPath(options.StagingFolder);

        var substituted = new List<string>();
        var plaintext = new List<string>();
        var unresolved = new List<string>();

        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);

        AssembleTree(backup, decryptedRoot, hasDecrypted, staging, options, log, warnings,
            substituted, plaintext, unresolved);

        foreach (var rel in unresolved)
            warnings.Add($"Module '{rel}' is signed with no decrypted copy; it is packed unchanged and will not run.");

        if (unresolved.Count > 0)
        {
            throw new InvalidOperationException(
                "The backup cannot be converted into a runnable package. These signed modules have no decrypted ELF copy and would be packed unchanged; the console then reports the image as corrupted:"
                + Environment.NewLine + string.Join(Environment.NewLine, unresolved)
                + Environment.NewLine
                + "Place a raw ELF (or already fake-signed SELF) under the decrypted subtree, using the same relative path or file name, and retry.");
        }

        var preflight = ProsperoLaunchReadiness.InspectAppRoot(staging);
        if (!preflight.HasEboot || !preflight.HasParamJson)
        {
            throw new InvalidOperationException(
                "The assembled tree is missing files the launch service needs:"
                + Environment.NewLine + string.Join(Environment.NewLine, preflight.Issues));
        }

        log($"Assembled backup tree at {staging} ({substituted.Count} substituted, {plaintext.Count} plaintext modules).");
        return new ProsperoBackupAssemblyResult
        {
            StagingFolder = staging,
            ContentId = contentId,
            Passcode = passcode,
            Version = version,
            SubstitutedModules = substituted,
            PlaintextModules = plaintext,
            UnresolvedModules = unresolved,
            Warnings = warnings,
        };
    }

    // Copies the backup into the staging tree, excluding the decrypted subtree, substituting each
    // signed executable module with its raw ELF copy where one exists.
    private static void AssembleTree(
        string backup, string decryptedRoot, bool hasDecrypted, string staging,
        ProsperoBackupConversionOptions options, Action<string> log, List<string> warnings,
        List<string> substituted, List<string> plaintext, List<string> unresolved)
    {
        string decryptedFull = hasDecrypted ? Path.GetFullPath(decryptedRoot) : "";
        int files = 0;

        foreach (var file in Directory.EnumerateFiles(backup, "*", SearchOption.AllDirectories))
        {
            string full = Path.GetFullPath(file);
            // Skip the decrypted subtree itself; it is a source of substitutions, not a tree member.
            if (hasDecrypted && IsUnder(full, decryptedFull))
                continue;

            string rel = Path.GetRelativePath(backup, full);
            string relNorm = rel.Replace('\\', '/');
            if (ShouldSkipBackupFile(relNorm))
                continue;

            // Drop the backup's own right.sprx so the builder injects its embedded debug module.
            if (options.UseEmbeddedRightSprx
                && relNorm.Equals("sce_sys/about/right.sprx", StringComparison.OrdinalIgnoreCase))
            {
                log("Dropping the backup right.sprx; the embedded debug module will be used.");
                continue;
            }

            // Leftover PS4 metadata next to param.json makes the launch service refuse the title.
            if (relNorm.Equals("sce_sys/param.sfo", StringComparison.OrdinalIgnoreCase))
            {
                log("Skipping leftover sce_sys/param.sfo; param.json is the PS5 metadata form.");
                continue;
            }

            string target = Path.Combine(staging, rel);
            files++;

            if (IsModuleCandidate(Path.GetFileName(rel)))
            {
                uint magic = ReadMagic(full);
                if (magic == ElfMagic)
                {
                    // Copy so a later fake-sign does not rewrite the backup's own ELF.
                    PlaceFile(full, target, copyRequired: true);
                    plaintext.Add(relNorm);
                    MaybeLogAssemble(log, files);
                    continue;
                }

                if (magic == SelfMagic)
                {
                    bool decryptedIsElf = false;
                    string? decrypted = hasDecrypted ? FindDecryptedModule(decryptedRoot, rel, out decryptedIsElf) : null;
                    if (decrypted is not null)
                    {
                        PlaceFile(decrypted, target, copyRequired: true);
                        if (decryptedIsElf)
                        {
                            substituted.Add(relNorm);
                            log($"Substituted decrypted module for {relNorm}.");
                        }
                        else
                        {
                            plaintext.Add(relNorm);
                            log($"Substituted already fake-signed module for {relNorm}.");
                        }
                        MaybeLogAssemble(log, files);
                        continue;
                    }

                    PlaceFile(full, target, copyRequired: true);
                    if (IsFakeAuthoritySelf(full))
                    {
                        plaintext.Add(relNorm);
                        log($"Kept already fake-signed module {relNorm}.");
                    }
                    else
                    {
                        unresolved.Add(relNorm);
                    }
                    MaybeLogAssemble(log, files);
                    continue;
                }
            }

            // Data: hard-link on the same volume so a 200+ GB dump is not copied into RAM or a second tree.
            PlaceFile(full, target, copyRequired: false);
            MaybeLogAssemble(log, files);
        }

        log($"Assembling backup tree: {files:N0} files linked.");
    }

    private static void MaybeLogAssemble(Action<string> log, int files)
    {
        if (files == 1 || files % 2500 == 0)
            log($"Assembling backup tree: {files:N0} files linked.");
    }

    private static void PlaceFile(string source, string dest, bool copyRequired)
    {
        string? dir = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        if (File.Exists(dest))
            File.Delete(dest);

        if (copyRequired)
        {
            File.Copy(source, dest);
            return;
        }

        try
        {
            if (!TryCreateHardLink(dest, source))
                throw new IOException($"Hard-link failed for '{source}'.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            long length = 0;
            try { length = new FileInfo(source).Length; } catch (IOException) { }

            if (length <= 8L * 1024 * 1024)
            {
                File.Copy(source, dest);
                return;
            }

            throw new IOException(
                $"Cannot hard-link '{source}' onto '{dest}'. Put the staging folder on the same disk as the backup so the tree is not copied. {ex.Message}",
                ex);
        }
    }

    // Executable-module file names/extensions that participate in substitution + fake-signing.
    private static bool IsModuleCandidate(string fileName) =>
        fileName.Equals("eboot.bin", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".elf", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".self", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".prx", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".sprx", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldSkipBackupFile(string relNorm)
    {
        string name = Path.GetFileName(relNorm);
        if (name.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("._", StringComparison.Ordinal))
            return true;
        return relNorm.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase)
            || relNorm.Contains("/__MACOSX/", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindNamedDirectory(string parent, string name)
    {
        foreach (string dir in Directory.EnumerateDirectories(parent))
        {
            if (string.Equals(Path.GetFileName(dir), name, StringComparison.OrdinalIgnoreCase))
                return dir;
        }
        return null;
    }

    private static string? FindNamedDirectoryRecursive(string parent, string name, int depth)
    {
        string? direct = FindNamedDirectory(parent, name);
        if (direct is not null)
            return direct;
        if (depth <= 0)
            return null;
        foreach (string dir in Directory.EnumerateDirectories(parent))
        {
            if (ShouldSkipBackupFile(Path.GetFileName(dir) + "/"))
                continue;
            string? nested = FindNamedDirectoryRecursive(dir, name, depth - 1);
            if (nested is not null)
                return nested;
        }
        return null;
    }

    // If the chosen folder is the dump parent, take the single child that actually holds sce_sys.
    private static string ResolveBackupRoot(string backup)
    {
        if (FindNamedDirectory(backup, "sce_sys") is not null)
            return backup;

        string? found = null;
        foreach (string dir in Directory.EnumerateDirectories(backup))
        {
            if (ShouldSkipBackupFile(Path.GetFileName(dir) + "/"))
                continue;
            if (FindNamedDirectory(dir, "sce_sys") is null)
                continue;
            if (found is not null)
                return backup;
            found = dir;
        }
        return found ?? backup;
    }

    private static string? FindFileIgnoreCase(string directory, string fileName)
    {
        string direct = Path.Combine(directory, fileName);
        if (File.Exists(direct))
            return direct;
        if (!Directory.Exists(directory))
            return null;
        foreach (string file in Directory.EnumerateFiles(directory))
        {
            if (string.Equals(Path.GetFileName(file), fileName, StringComparison.OrdinalIgnoreCase))
                return file;
        }
        return null;
    }

    // Dumpers sometimes keep the original relative path under decrypted/, sometimes only the file name,
    // and sometimes an ELF alias such as eboot.elf / eboot.bin.elf.
    private static string? FindDecryptedModule(string decryptedRoot, string rel, out bool isRawElf)
    {
        foreach (string name in DecryptedNameAliases(Path.GetFileName(rel)))
        {
            string beside = Path.Combine(decryptedRoot, Path.GetDirectoryName(rel) ?? "", name);
            if (TryClassifyDecrypted(beside, out isRawElf))
                return beside;
        }

        var aliases = new HashSet<string>(DecryptedNameAliases(Path.GetFileName(rel)), StringComparer.OrdinalIgnoreCase);
        foreach (string candidate in Directory.EnumerateFiles(decryptedRoot, "*", SearchOption.AllDirectories))
        {
            if (!aliases.Contains(Path.GetFileName(candidate)))
                continue;
            if (TryClassifyDecrypted(candidate, out isRawElf))
                return candidate;
        }

        isRawElf = false;
        return null;
    }

    private static IEnumerable<string> DecryptedNameAliases(string fileName)
    {
        yield return fileName;
        if (fileName.Equals("eboot.bin", StringComparison.OrdinalIgnoreCase))
        {
            yield return "eboot.elf";
            yield return "eboot.bin.elf";
            yield return "eboot.self";
            yield break;
        }

        yield return fileName + ".elf";
        string stem = Path.GetFileNameWithoutExtension(fileName);
        if (!string.IsNullOrEmpty(stem) && !stem.Equals(fileName, StringComparison.OrdinalIgnoreCase))
            yield return stem + ".elf";
    }

    private static bool TryClassifyDecrypted(string path, out bool isRawElf)
    {
        isRawElf = false;
        if (!File.Exists(path))
            return false;
        uint magic = ReadMagic(path);
        if (magic == ElfMagic)
        {
            isRawElf = true;
            return true;
        }
        return magic == SelfMagic && IsFakeAuthoritySelf(path);
    }

    private static uint ReadMagic(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[4];
            if (fs.ReadAtLeast(head, 4, throwOnEndOfStream: false) < 4)
                return 0;
            return (uint)(head[0] | (head[1] << 8) | (head[2] << 16) | (head[3] << 24));
        }
        catch (IOException) { return 0; }
    }

    // True when the file is a SELF whose extended-info authority id carries the fake-authority prefix,
    // i.e. a module that already runs on a debug-mode console without substitution.
    private static bool IsFakeAuthoritySelf(string path)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            var image = LibProsperoPkg.Content.ProsperoFself.Parse(bytes);
            return image.ExtInfo is not null && (image.ExtInfo.AuthorityId & AuthorityMask) == FakeAuthorityPrefix;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return false;
        }
    }

    private static bool TryCreateHardLink(string dest, string source)
    {
        if (OperatingSystem.IsWindows())
            return NativeCreateHardLink(dest, source, IntPtr.Zero);
        return NativeLink(source, dest) == 0;
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int NativeLink(string existingPath, string newPath);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool NativeCreateHardLink(string newPath, string existingPath, IntPtr securityAttributes);

    private static bool IsUnder(string path, string root)
    {
        string p = path.TrimEnd(Path.DirectorySeparatorChar);
        string r = root.TrimEnd(Path.DirectorySeparatorChar);
        return p.Equals(r, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string SafeName(string contentId)
    {
        Span<char> buffer = stackalloc char[contentId.Length];
        for (int i = 0; i < contentId.Length; i++)
        {
            char c = contentId[i];
            buffer[i] = char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_';
        }
        return new string(buffer);
    }

    private readonly record struct ParamMeta(string ContentId, string Version);

    private static ParamMeta ReadParamMeta(string paramPath)
    {
        if (!File.Exists(paramPath))
            return new ParamMeta("", "");
        try
        {
            var param = ProsperoParam.Load(paramPath);
            string cid = param.ContentId
                ?? ReadJsonString(param.Root, "contentId", "content_id", "CONTENT_ID");
            string ver = param.MasterVersion
                ?? ReadJsonString(param.Root, "masterVersion", "master_version", "APP_VER");
            return new ParamMeta(cid ?? "", ver ?? "");
        }
        catch (JsonException) { return new ParamMeta("", ""); }
        catch (InvalidDataException) { return new ParamMeta("", ""); }
        catch (IOException) { return new ParamMeta("", ""); }
    }

    private static string ReadJsonString(JsonObject root, params string[] names)
    {
        foreach (string name in names)
        {
            if (root.TryGetPropertyValue(name, out var node) && node is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text))
                return text;
        }
        return "";
    }
}
