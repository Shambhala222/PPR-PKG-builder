using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;

namespace LibProsperoPkg.Gui.Services;

/// <summary>
/// Runs a Drakmor package builder. Everyday APP/Homebrew folder packs use the
/// 0.5 library (same Kraken, no layout/dedup). GP5, AC, PFS v3 and shuffle
/// analysis use 0.6.5. Each library is loaded in its own
/// <see cref="AssemblyLoadContext"/> because both assemblies share the name
/// LibProsperoPkg. On macOS the Kraken backend is BuiltIn —
/// <c>libScePubTools.dll</c> is 64-bit Windows only.
/// </summary>
internal static class Win02PackageBuilder
{
    internal const string FastLibraryFile = "LibProsperoPkg.Win05.dll";
    internal const string LayoutLibraryFile = "LibProsperoPkg.Win06.dll";

    private static readonly object Gate = new();
    private static Assembly? _fastAssembly;
    private static Assembly? _layoutAssembly;

    internal sealed class Request
    {
        public required string SourceFolder { get; init; }
        public required string OutputFolder { get; init; }
        public required string TemporaryDirectory { get; init; }
        public required string ContentId { get; init; }
        public required string Passcode { get; init; }
        public required string Title { get; init; }
        public required string Version { get; init; }
        public required string PackageMode { get; init; }
        public required string ImageMode { get; init; }
        public string SourceMode { get; init; } = "Automatic";
        public string? ProjectFilePath { get; init; }
        public byte[]? EntitlementKey { get; init; }
        public int KrakenLevel { get; init; } = 7;
        public int KrakenThreads { get; init; }
        public int PlayGoChunks { get; init; } = 64;
        public bool Deterministic { get; init; } = true;
        public ulong? SdkVersionOverride { get; init; }
        public string PfsCompressionFormat { get; init; } = "Version2";
        public bool EnableShufflePatternAnalysis { get; init; }
        public int? ShufflePredictionCompressionLevel { get; init; }
        public bool SkipPfsInputDataAllowedCheck { get; init; }
        public bool EnableOuterBlockCoalescing { get; init; } = true;
        public bool EnableRelocationAlignmentAdjustment { get; init; } = true;
        public bool UseLayoutLibrary { get; init; }
        public CancellationToken CancellationToken { get; init; }
        public required Action<string> Log { get; init; }
    }

    public static string Build(Request request)
    {
        Assembly asm = Load(request.UseLayoutLibrary);
        Type optionsType = asm.GetType("LibProsperoPkg.ProsperoBuildOptions", throwOnError: true)!;
        Type builderType = asm.GetType("LibProsperoPkg.ProsperoPackageBuilder", throwOnError: true)!;
        object options = Activator.CreateInstance(optionsType)!;

        Set(options, "SourceFolder", request.SourceFolder);
        Set(options, "OutputFolder", request.OutputFolder);
        Set(options, "TemporaryDirectory", request.TemporaryDirectory);
        Set(options, "ContentId", request.ContentId);
        Set(options, "PrimaryId", request.ContentId);
        if (request.ContentId.Length >= 16)
            Set(options, "TitleId", request.ContentId.Substring(7, 9));
        Set(options, "Passcode", request.Passcode);
        Set(options, "Title", request.Title);
        Set(options, "Version", request.Version);
        Set(options, "GenerateParamJsonIfMissing", true);
        Set(options, "UsePublisherPprNaps", true);
        Set(options, "KrakenCompressionLevel", request.KrakenLevel);
        Set(options, "KrakenMaxDegreeOfParallelism", request.KrakenThreads);
        Set(options, "PlayGoChunkCount", Math.Max(1, request.PlayGoChunks));
        Set(options, "DeterministicBuild", request.Deterministic);
        Set(options, "EnableShufflePatternAnalysis", request.EnableShufflePatternAnalysis);
        Set(options, "SkipPfsInputDataAllowedCheck", request.SkipPfsInputDataAllowedCheck);
        Set(options, "EnableOuterBlockCoalescing", request.EnableOuterBlockCoalescing);
        Set(options, "EnableRelocationAlignmentAdjustment", request.EnableRelocationAlignmentAdjustment);
        if (request.SdkVersionOverride.HasValue)
            Set(options, "SdkVersionOverride", request.SdkVersionOverride.Value);
        if (request.ShufflePredictionCompressionLevel.HasValue)
            Set(options, "ShufflePredictionCompressionLevel", request.ShufflePredictionCompressionLevel.Value);
        if (!string.IsNullOrEmpty(request.ProjectFilePath))
            Set(options, "ProjectFilePath", request.ProjectFilePath);
        if (request.EntitlementKey is { Length: 16 })
            Set(options, "EntitlementKey", request.EntitlementKey);
        Set(options, "CancellationToken", request.CancellationToken);
        SetEnum(options, "Mode", request.PackageMode);
        SetEnum(options, "OutputFormat", "DebugImage");
        SetEnum(options, "KrakenBackend", "BuiltIn");
        SetEnum(options, "SourceMode", request.SourceMode);
        SetEnum(options, "PfsCompressionFormat", request.PfsCompressionFormat);
        SetEnum(options, "PublisherImageMode",
            string.Equals(request.ImageMode, "plain", StringComparison.OrdinalIgnoreCase)
                ? "PlaintextNoAuth"
                : "Native");

        MethodInfo build = builderType.GetMethod("Build", [optionsType, typeof(Action<string>)])
            ?? throw new InvalidOperationException("ProsperoPackageBuilder.Build was not found in the selected library.");
        object result;
        try
        {
            result = build.Invoke(null, [options, request.Log])
                ?? throw new InvalidOperationException("The selected builder returned no result.");
        }
        catch (TargetInvocationException ex)
        {
            throw ex.InnerException ?? ex;
        }

        string? outputPath = result.GetType().GetProperty("OutputPath")?.GetValue(result) as string;
        if (result.GetType().GetProperty("Warnings")?.GetValue(result) is IEnumerable warnings)
        {
            foreach (object? warning in warnings)
            {
                if (warning is not null)
                    request.Log("WARNING: " + warning);
            }
        }

        if (string.IsNullOrEmpty(outputPath) || !File.Exists(outputPath))
            throw new InvalidOperationException("The selected builder did not create the final PKG.");
        return outputPath;
    }

    private static void Set(object target, string name, object? value)
    {
        PropertyInfo? property = target.GetType().GetProperty(name);
        if (property is null || value is null)
            return;
        if (property.PropertyType == value.GetType()
            || Nullable.GetUnderlyingType(property.PropertyType) == value.GetType()
            || property.PropertyType.IsAssignableFrom(value.GetType()))
        {
            property.SetValue(target, value);
            return;
        }

        if (property.PropertyType == typeof(int?) && value is int number)
            property.SetValue(target, (int?)number);
    }

    private static void SetEnum(object target, string name, string value)
    {
        PropertyInfo? property = target.GetType().GetProperty(name);
        if (property is null)
            return;
        Type enumType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        object parsed = Enum.Parse(enumType, value);
        property.SetValue(target, parsed);
    }

    public static ulong EncodeSdkMajor(int major, bool layoutLibrary = false)
    {
        Assembly asm = Load(layoutLibrary);
        Type versions = asm.GetType("LibProsperoPkg.Content.ProsperoSdkVersions", throwOnError: true)!;
        object release = versions.GetMethod("GetByMajor")!.Invoke(null, [major])
            ?? throw new InvalidOperationException("SDK major " + major + " was not found in the selected library.");
        return (ulong)release.GetType().GetProperty("ExecutableVersion")!.GetValue(release)!;
    }

    private static Assembly Load(bool layoutLibrary)
    {
        Assembly? cached = layoutLibrary ? _layoutAssembly : _fastAssembly;
        if (cached is not null)
            return cached;

        lock (Gate)
        {
            cached = layoutLibrary ? _layoutAssembly : _fastAssembly;
            if (cached is not null)
                return cached;

            string file = layoutLibrary ? LayoutLibraryFile : FastLibraryFile;
            string path = Path.Combine(AppContext.BaseDirectory, file);
            if (!File.Exists(path))
                throw new FileNotFoundException("The selected LibProsperoPkg library was not found.", path);

            var context = new AssemblyLoadContext(layoutLibrary ? "LibProsperoPkg.Win06" : "LibProsperoPkg.Win05", isCollectible: false);
            context.Resolving += (_, name) =>
            {
                string candidate = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
                if (File.Exists(candidate))
                    return context.LoadFromAssemblyPath(candidate);
                return null;
            };
            Assembly loaded = context.LoadFromAssemblyPath(path);
            if (layoutLibrary)
                _layoutAssembly = loaded;
            else
                _fastAssembly = loaded;
            return loaded;
        }
    }
}
