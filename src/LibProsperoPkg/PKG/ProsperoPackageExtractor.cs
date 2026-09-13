// LibProsperoPkg - A library for building and inspecting PS5 packages.
// Copyright (C) 2026 SvenGDK
//
// End-to-end package extractor. A finalized image is a plaintext header/digest region
// followed by the shared outer PFS (AES-XTS encrypted under the package EKPFS). The outer PFS holds
// a single nested file, pfs_image.dat (a PFSC container whose logical content is the inner PFS
// image), plus optional metadata. This orchestrates the whole path:
//
//   finalized image --(FIH header: pfs offset/size)--> outer PFS
//   outer PFS --(EKPFS via ProsperoPfsReader)--> decrypt + walk --> pfs_image.dat
//   pfs_image.dat --(PFSC decode)--> inner PFS image
//   inner PFS image --(ProsperoPfsReader)--> the application files
//
// reusing the proven ProsperoPfsReader / ProsperoPfscReader primitives end to end. The outer EKPFS
// is either derived from the content id + passcode (public-input schedule) or supplied by the
// caller; nothing about the key material is forged here.
#nullable enable
using LibProsperoPkg.PFS;
using LibProsperoPkg.Util;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LibProsperoPkg.PKG;

/// <summary>Options controlling a package extraction.</summary>
public sealed class ProsperoExtractionOptions
{
    /// <summary>
    /// When true, also writes the outer PFS's non-<c>pfs_image.dat</c> files (metadata that lives
    /// beside the nested image) into <see cref="OuterMetadataSubdirectory"/>. Off by default.
    /// </summary>
    public bool ExtractOuterMetadata { get; set; }

    /// <summary>Subdirectory (under the output directory) for outer metadata files.</summary>
    public string OuterMetadataSubdirectory { get; set; } = "_outer";

    /// <summary>
    /// When true (and <see cref="ExtractOuterMetadata"/> is set), also writes the nested
    /// <c>pfs_image.dat</c> itself — the raw on-disk inner image bytes, undecoded — into
    /// <see cref="OuterMetadataSubdirectory"/>. This is the raw inner-geometry payload that the
    /// standard outer-metadata pass deliberately skips (it decodes it instead). Off by default.
    /// </summary>
    public bool IncludeNestedImageRaw { get; set; }

    /// <summary>
    /// Optional progress sink (written bytes, total bytes). Called from the extract thread.
    /// </summary>
    public Action<long, long>? ReportProgress { get; set; }
}

/// <summary>What <see cref="ProsperoPackageExtractor.Inspect"/> reports about a package.</summary>
public sealed class ProsperoPackageExtractionInfo
{
    /// <summary>Detected package type.</summary>
    public required ProsperoPkgType PackageType { get; init; }

    /// <summary>True when the finalized image is signed for a retail console (signed byte 0x80).</summary>
    public required bool IsRetail { get; init; }

    /// <summary>The embedded content id, when the package carries a readable one.</summary>
    public required string? ContentId { get; init; }

    /// <summary>File offset of the shared outer PFS image.</summary>
    public required long PfsImageOffset { get; init; }

    /// <summary>Byte length of the shared outer PFS image.</summary>
    public required long PfsImageSize { get; init; }

    /// <summary>True when the outer PFS superblock marks the filesystem AES-XTS encrypted.</summary>
    public required bool OuterEncrypted { get; init; }

    /// <summary>
    /// True when extraction needs key material that cannot be derived from public inputs (a
    /// finalized retail image whose key comes from the console entitlement path).
    /// </summary>
    public required bool RequiresSuppliedKey { get; init; }
}

/// <summary>The outcome of an extraction.</summary>
public sealed class ProsperoPackageManifest
{
    /// <summary>Detected package type.</summary>
    public required ProsperoPkgType PackageType { get; init; }

    /// <summary>True when the finalized image is signed for a retail console (signed byte 0x80).</summary>
    public required bool IsRetail { get; init; }

    /// <summary>The content id used/resolved, when known.</summary>
    public required string? ContentId { get; init; }

    /// <summary>First 4 bytes (hex) of the EKPFS that decrypted the outer PFS, or <see langword="null"/> when plaintext.</summary>
    public required string? EkpfsFingerprint { get; init; }

    /// <summary>The output directory the files were written to.</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>Number of files in the outer PFS.</summary>
    public required int OuterFileCount { get; init; }

    /// <summary>True when the nested <c>pfs_image.dat</c> was stored PFSC-compressed.</summary>
    public required bool InnerImageCompressed { get; init; }

    /// <summary>The files written from the inner (application) filesystem.</summary>
    public required IReadOnlyList<ProsperoExtractedEntry> Entries { get; init; }

    /// <summary>Total number of files written (inner plus any outer metadata).</summary>
    public required int ExtractedFileCount { get; init; }
}

/// <summary>
/// Extracts the application filesystem from a finalized PS5 package. See the file header for the
/// pipeline. Debug/keyed packages whose EKPFS is derivable or supplied extract fully; a finalized
/// retail image without a supplied key throws a clear <see cref="ProsperoExtractionException"/>.
/// </summary>
public static class ProsperoPackageExtractor
{
    private const int SuperblockPeekSize = 0x400;

    /// <summary>
    /// Reads a package's finalized-image header and outer PFS superblock and reports what
    /// extraction would require, without needing (or using) any key.
    /// </summary>
    /// <param name="packagePath">Path to the finalized package.</param>
    public static ProsperoPackageExtractionInfo Inspect(string packagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        if (!File.Exists(packagePath))
            throw new FileNotFoundException("Package not found.", packagePath);

        var type = ProsperoPkgReader.DetectType(packagePath)
            ?? throw new ProsperoExtractionException("Not a recognisable PS5 package (unknown magic).");
        if (type == ProsperoPkgType.Meta)
            throw new ProsperoExtractionException(
                "This is a metadata-only container; a finalized image is required to extract its filesystem.");

        using var stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        long fileLength = stream.Length;
        var (signedByte, pfsOffset, pfsSize) = ReadFihFields(stream, fileLength);

        var state = PeekOuterState(new LibProsperoPkg.Util.StreamReader(stream, pfsOffset));
        string? contentId = TryReadContentId(packagePath);
        bool isRetail = signedByte == 0x80;
        bool encrypted = state != OuterPfsState.Plaintext
            && !IsPlaintextNoAuthOuter(stream, pfsOffset, pfsSize);

        return new ProsperoPackageExtractionInfo
        {
            PackageType = type,
            IsRetail = isRetail,
            ContentId = contentId,
            PfsImageOffset = pfsOffset,
            PfsImageSize = pfsSize,
            OuterEncrypted = encrypted,
            // A retail image with an encrypted outer PFS and no embedded/derivable key needs a
            // supplied image key; a debug image's key is derivable from its passcode.
            RequiresSuppliedKey = encrypted && isRetail,
        };
    }

    /// <summary>
    /// Extracts a package to <paramref name="outputDirectory"/>, deriving the outer EKPFS from
    /// <paramref name="passcode"/> (and the package's own content id).
    /// </summary>
    public static ProsperoPackageManifest Extract(
        string packagePath, string outputDirectory, string passcode, Action<string>? logger = null)
        => Extract(packagePath, outputDirectory, ProsperoExtractionKey.FromPasscode(passcode), null, logger);

    /// <summary>
    /// Extracts a package to <paramref name="outputDirectory"/> using the supplied key material.
    /// </summary>
    /// <param name="packagePath">Path to the finalized package.</param>
    /// <param name="outputDirectory">Destination directory (created if missing).</param>
    /// <param name="key">How to obtain the outer EKPFS.</param>
    /// <param name="options">Extraction options (optional).</param>
    /// <param name="logger">Optional progress sink.</param>
    public static ProsperoPackageManifest Extract(
        string packagePath, string outputDirectory, ProsperoExtractionKey key,
        ProsperoExtractionOptions? options = null, Action<string>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(key);
        options ??= new ProsperoExtractionOptions();
        var log = logger ?? (_ => { });

        if (!File.Exists(packagePath))
            throw new FileNotFoundException("Package not found.", packagePath);

        var type = ProsperoPkgReader.DetectType(packagePath)
            ?? throw new ProsperoExtractionException("Not a recognisable PS5 package (unknown magic).");
        if (type == ProsperoPkgType.Meta)
            throw new ProsperoExtractionException(
                "This is a metadata-only container; a finalized image is required to extract its filesystem.");

        bool isRetail = false;
        string? contentId = TryReadContentId(packagePath);
        Directory.CreateDirectory(outputDirectory);

        using var pkgStream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        long fileLength = pkgStream.Length;
        var (signedByte, pfsOffset, pfsSize) = ReadFihFields(pkgStream, fileLength);
        long superblockAbs = ReadFihSuperblockOffset(pkgStream);
        isRetail = signedByte == 0x80;

        var outerState = PeekOuterState(new LibProsperoPkg.Util.StreamReader(pkgStream, pfsOffset));
        var candidates = key.ResolveEkpfsCandidates(contentId);

        byte[]? usedEkpfs = null;
        ProsperoPfsReader outer;
        if (outerState == OuterPfsState.Plaintext)
        {
            log("Opening outer PFS (plaintext)...");
            outer = new ProsperoPfsReader(new LibProsperoPkg.Util.StreamReader(pkgStream, pfsOffset), 0);
        }
        else if (candidates.Count == 0)
        {
            throw new ProsperoExtractionException(isRetail
                ? "The outer filesystem is encrypted and this is a finalized retail image: its image key " +
                  "is delivered through the console entitlement path and cannot be derived from a passcode. " +
                  "Supply the image key with ProsperoExtractionKey.FromEkpfs(...) to extract."
                : "The outer filesystem is encrypted. Supply a passcode with " +
                  "ProsperoExtractionKey.FromPasscode(...) or an image key with FromEkpfs(...).");
        }
        else
        {
            log($"Opening outer PFS (encrypted; trying {candidates.Count} key candidate(s))...");
            // Superblock-first (classic) path first; then the PS5 nwonly "data-first" outer PFS (superblock near
            // the end at FIH[0x20], per-block plain/signed XTS) which the classic reader can't parse.
            outer = OpenOuterWithCandidates(pkgStream, pfsOffset, candidates, out usedEkpfs)
                ?? OpenDataFirstOuter(pkgStream, pfsOffset, pfsSize, superblockAbs, candidates, out usedEkpfs)
                ?? throw new ProsperoExtractionException(isRetail
                    ? "None of the supplied keys decrypted the outer filesystem. A finalized retail image " +
                      "requires the console-provisioned image key (ProsperoExtractionKey.FromEkpfs)."
                    : "None of the derived/supplied keys decrypted the outer filesystem. Confirm the content " +
                      "id and passcode, or supply the image key directly.");
            log(usedEkpfs is null
                ? "Outer filesystem opened (plaintext / no-auth)."
                : "Outer filesystem opened.");
        }

        int outerFileCount = outer.GetAllFiles().Count();
        var innerOuterFile = outer.GetFile("pfs_image.dat") ?? FindByName(outer, "pfs_image.dat");

        List<ProsperoExtractedEntry> entries;
        bool innerCompressed = false;
        int extractedCount;

        if (innerOuterFile is null)
        {
            // No nested image: the outer filesystem is the content. Extract it directly.
            log("No nested pfs_image.dat; extracting the outer filesystem directly...");
            entries = new List<ProsperoExtractedEntry>(ProsperoPfsExtractor.Extract(outer, outputDirectory, log));
            extractedCount = entries.Count;
        }
        else
        {
            innerCompressed = innerOuterFile.flags.HasFlag(ProsperoInodeFlags.compressed);
            IMemoryReader onDisk = innerOuterFile.GetView();

            // PS5 nwonly packages store the inner pfs_image.dat "data-first": a raw concatenation of
            // per-file payloads (raw or headerless Kraken) with NO PFSC wrapper and NO superblock at
            // offset 0. Its block geometry lives in the sibling naps_pkg_layout.dat, so it is decoded with
            // ProsperoPs5InnerImageReader rather than the standard PFS reader.
            var napsFile = outer.GetFile("naps_pkg_layout.dat") ?? FindByName(outer, "naps_pkg_layout.dat");
            byte[] head16 = new byte[16];
            onDisk.Read(0, head16, 0, 16);
            bool headIsPfsc = head16.Length >= 4 && head16[0] == (byte)'P' && head16[1] == (byte)'F'
                              && head16[2] == (byte)'S' && head16[3] == (byte)'C';
            // A nwonly data-first inner is neither a PFSC container nor a superblock-first PFS: it begins
            // with raw file data and its geometry is described entirely by the sibling naps.
            bool innerIsDataFirst = napsFile is not null && !headIsPfsc && !HasInnerSuperblock(head16);

            if (innerIsDataFirst)
            {
                log("Opening nested pfs_image.dat (nwonly data-first; decoding via naps_pkg_layout.dat)...");
                entries = ExtractNwonlyInner(onDisk, innerOuterFile, napsFile!, outputDirectory, log, options.ReportProgress);
                extractedCount = entries.Count;
            }
            else
            {
                log($"Opening nested pfs_image.dat ({(innerCompressed ? "PFSC container" : "raw")})...");

                IMemoryReader innerImage;
                if (innerCompressed)
                {
                    // The zlib PFSC image and the PS5 PFSv2/PFSv3 Kraken container share the 'PFSC' magic but are
                    // disambiguated by the format-version field at offset 0x04 (0 = zlib, 2/3 = Kraken). Decompress
                    // the Kraken container up-front into a plaintext image the reader walks.
                    byte[] head = new byte[8];
                    onDisk.Read(0, head, 0, 8);
                    int pfscVersion = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(4));
                    if (pfscVersion == 2 || pfscVersion == 3)
                    {
                        long onDiskSize = innerOuterFile.compressed_size > 0 ? innerOuterFile.compressed_size : innerOuterFile.size;
                        byte[] container = new byte[onDiskSize];
                        onDisk.Read(0, container, 0, (int)onDiskSize);
                        byte[] plainInner = LibProsperoPkg.PFS.Compression.ProsperoCompressedPfsFile.Parse(container).Decompress();
                        innerImage = new LibProsperoPkg.Util.StreamReader(new MemoryStream(plainInner), 0, takeOwnership: true);
                    }
                    else
                    {
                        innerImage = new ProsperoPfscReader(onDisk);
                    }
                }
                else
                {
                    innerImage = onDisk;
                }

                ProsperoPfsReader inner;
                try
                {
                    // The inner image carries its own superblock; the reader auto-detects whether it is
                    // encrypted and uses the EKPFS only if the inner superblock says so.
                    inner = new ProsperoPfsReader(innerImage, 0, usedEkpfs);
                }
                catch (Exception ex)
                {
                    throw new ProsperoExtractionException(
                        "Failed to open the nested filesystem image (pfs_image.dat).", ex);
                }

                log("Extracting the application filesystem...");
                entries = new List<ProsperoExtractedEntry>(ProsperoPfsExtractor.Extract(inner, outputDirectory, log));
                extractedCount = entries.Count;
            }

            if (options.ExtractOuterMetadata)
                extractedCount += ExtractOuterMetadata(
                    outer, outputDirectory, options.OuterMetadataSubdirectory, options.IncludeNestedImageRaw, log);
        }

        extractedCount += RestoreCntSceSysFiles(packagePath, outputDirectory, key.Passcode, log);

        string? fingerprint = usedEkpfs is null ? null : Convert.ToHexString(usedEkpfs.AsSpan(0, 4));
        log($"Done: extracted {extractedCount} file(s) to {outputDirectory}.");

        return new ProsperoPackageManifest
        {
            PackageType = type,
            IsRetail = isRetail,
            ContentId = contentId,
            EkpfsFingerprint = fingerprint,
            OutputDirectory = Path.GetFullPath(outputDirectory),
            OuterFileCount = outerFileCount,
            InnerImageCompressed = innerCompressed,
            Entries = entries,
            ExtractedFileCount = extractedCount,
        };
    }

    /// <summary>
    /// Lists the application files a package would extract, without writing them.
    /// </summary>
    public static IReadOnlyList<ProsperoExtractedEntry> ListFiles(
        string packagePath, ProsperoExtractionKey key, Action<string>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentNullException.ThrowIfNull(key);
        var log = logger ?? (_ => { });

        var type = ProsperoPkgReader.DetectType(packagePath)
            ?? throw new ProsperoExtractionException("Not a recognisable PS5 package (unknown magic).");
        if (type == ProsperoPkgType.Meta)
            throw new ProsperoExtractionException(
                "This is a metadata-only container; a finalized image is required to list its filesystem.");

        string? contentId = TryReadContentId(packagePath);

        using var pkgStream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        long fileLength = pkgStream.Length;
        var (signedByte, pfsOffset, pfsSize) = ReadFihFields(pkgStream, fileLength);
        long superblockAbs = ReadFihSuperblockOffset(pkgStream);
        bool isRetail = signedByte == 0x80;

        var outerState = PeekOuterState(new LibProsperoPkg.Util.StreamReader(pkgStream, pfsOffset));
        var candidates = key.ResolveEkpfsCandidates(contentId);

        byte[]? usedEkpfs = null;
        ProsperoPfsReader outer;
        if (outerState == OuterPfsState.Plaintext)
            outer = new ProsperoPfsReader(new LibProsperoPkg.Util.StreamReader(pkgStream, pfsOffset), 0);
        else if (candidates.Count == 0)
            throw new ProsperoExtractionException(isRetail
                ? "The outer filesystem is encrypted (finalized retail image); supply the image key with FromEkpfs(...)."
                : "The outer filesystem is encrypted; supply a passcode or image key.");
        else
            // Superblock-first (classic) path first; then the "data-first" outer PFS, matching Extract.
            outer = OpenOuterWithCandidates(pkgStream, pfsOffset, candidates, out usedEkpfs)
                ?? OpenDataFirstOuter(pkgStream, pfsOffset, pfsSize, superblockAbs, candidates, out usedEkpfs)
                ?? throw new ProsperoExtractionException("None of the derived/supplied keys decrypted the outer filesystem.");

        var innerOuterFile = outer.GetFile("pfs_image.dat") ?? FindByName(outer, "pfs_image.dat");
        if (innerOuterFile is null)
            return ProsperoPfsExtractor.ListEntries(outer);

        IMemoryReader onDisk = innerOuterFile.GetView();

        // A "data-first" inner (a raw file concatenation described by naps_pkg_layout.dat) has no PFSC
        // magic and no superblock at offset 0; it is listed from the reconstructed mount, matching Extract.
        var napsFile = outer.GetFile("naps_pkg_layout.dat") ?? FindByName(outer, "naps_pkg_layout.dat");
        byte[] head16 = new byte[16];
        onDisk.Read(0, head16, 0, 16);
        bool headIsPfsc = head16[0] == (byte)'P' && head16[1] == (byte)'F' && head16[2] == (byte)'S' && head16[3] == (byte)'C';
        if (napsFile is not null && !headIsPfsc && !HasInnerSuperblock(head16))
            return ListNwonlyInner(onDisk, innerOuterFile, napsFile);

        IMemoryReader innerImage;
        if (innerOuterFile.flags.HasFlag(ProsperoInodeFlags.compressed))
        {
            // 'PFSC' magic covers both the zlib image (version 0) and the Kraken container (version 2/3);
            // the Kraken container is decompressed up-front, matching Extract.
            byte[] head = new byte[8];
            onDisk.Read(0, head, 0, 8);
            int pfscVersion = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(4));
            if (pfscVersion == 2 || pfscVersion == 3)
            {
                long onDiskSize = innerOuterFile.compressed_size > 0 ? innerOuterFile.compressed_size : innerOuterFile.size;
                byte[] container = new byte[onDiskSize];
                onDisk.Read(0, container, 0, (int)onDiskSize);
                byte[] plainInner = LibProsperoPkg.PFS.Compression.ProsperoCompressedPfsFile.Parse(container).Decompress();
                innerImage = new LibProsperoPkg.Util.StreamReader(new MemoryStream(plainInner), 0, takeOwnership: true);
            }
            else
            {
                innerImage = new ProsperoPfscReader(onDisk);
            }
        }
        else
        {
            innerImage = onDisk;
        }

        var inner = new ProsperoPfsReader(innerImage, 0, usedEkpfs);
        return ProsperoPfsExtractor.ListEntries(inner);
    }

    // Lists a PS5 "data-first" inner pfs_image.dat from naps + the metadata region only.
    // The full inner image is not loaded; Extract still reconstructs when writing files.
    private static List<ProsperoExtractedEntry> ListNwonlyInner(
        IMemoryReader innerOnDisk, ProsperoPfsReader.File innerFile, ProsperoPfsReader.File napsFile)
    {
        long napsSize = napsFile.size;
        if (napsSize <= 0 || napsSize > 64L * 1024 * 1024)
            throw new ProsperoExtractionException("naps_pkg_layout.dat is missing or implausibly large.");
        byte[] napsBytes = new byte[napsSize];
        napsFile.GetView().Read(0, napsBytes, 0, (int)napsSize);

        IReadOnlyList<ProsperoPs5InnerFileEntry> tree;
        try
        {
            tree = ProsperoPs5InnerImageReader.ListFileTree(innerOnDisk, innerFile.size, napsBytes);
        }
        catch (Exception ex)
        {
            throw new ProsperoExtractionException(
                "Failed to decode the data-first inner image via naps_pkg_layout.dat.", ex);
        }

        var list = new List<ProsperoExtractedEntry>(tree.Count);
        foreach (var f in tree)
            list.Add(new ProsperoExtractedEntry { RelativePath = f.Path.Replace('\\', '/'), Size = f.Size, IsCompressed = false });
        return list;
    }

    private static ProsperoPfsReader? OpenOuterWithCandidates(
        Stream pkgStream, long pfsOffset, IReadOnlyList<byte[]> candidates, out byte[]? used)
    {
        // The outer PFS AES-XTS key is PfsGenEncKey(EKPFS, seed, newCrypt). The newCrypt bit lives in the
        // superblock's pfs_flags (0x2000000000000000) which the ProsperoPfsReader consumes as a parameter, so we
        // must try BOTH schemes: PS5 finalized outer images derive with newCrypt=TRUE (HMAC(EKPFS,seed) first),
        // while some older/plaintext-seed images use the classic path. Trying both here — alongside the SHA-256
        // and SHA-3 EKPFS candidates — lets the reader round-trip its own build output and compatible images.
        ulong[] pfsFlagCandidates = { 0x2000000000000000UL, 0UL };
        foreach (var candidate in candidates)
        {
            foreach (var flags in pfsFlagCandidates)
            {
                try
                {
                    var reader = new ProsperoPfsReader(new LibProsperoPkg.Util.StreamReader(pkgStream, pfsOffset), flags, candidate);
                    // Sanity-check the decrypt actually produced a coherent filesystem (a wrong key/newCrypt
                    // combination usually throws while parsing dinodes, but confirm at least one file is listed).
                    _ = reader.GetAllFiles().Any();
                    used = candidate;
                    return reader;
                }
                catch
                {
                    // Wrong key or newCrypt scheme: the decrypted superblock/dinodes fail to parse. Try the next.
                }
            }
        }

        used = null;
        return null;
    }

    /// <summary>Reads the FIH-recorded absolute offset of the outer-PFS superblock (field 0x20). For a PS5
    /// "data-first" outer PFS the superblock sits near the END of the image, not at the image start, so this
    /// offset (not <see cref="ProsperoPkgLayout.FihPfsImageOffsetField"/>) locates it.</summary>
    private static long ReadFihSuperblockOffset(Stream stream)
    {
        byte[] header = new byte[0x100];
        stream.Position = 0;
        ReadExactly(stream, header, header.Length);
        return (long)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(0x20));
    }

    /// <summary>
    /// Opens a PS5 nwonly "data-first" outer PFS: the plaintext superblock is at <paramref name="superblockAbs"/>
    /// (block D near the end of the image), file data occupies the leading blocks, and the AES-XTS scheme is
    /// per-block — <c>pfs_image.dat</c> data blocks use sector = block index, every other file/metadata block uses
    /// sector = <c>bit47 | index</c>, and the superblock is plaintext (see <see cref="ProsperoOuterPfsImage"/>).
    /// Decrypts blocks on demand (bootstrapping the pfs_image.dat block range from an all-signed probe
    /// pass) so a 100+ GiB outer image is never copied into RAM. Returns a reader over the live
    /// decrypting view with the superblock located at block D.
    /// </summary>
    private static ProsperoPfsReader? OpenDataFirstOuter(
        Stream pkgStream, long imageOffset, long imageSize, long superblockAbs,
        IReadOnlyList<byte[]> candidates, out byte[]? used)
    {
        used = null;
        const int BS = ProsperoOuterPfsImage.DefaultBlockSize; // 0x10000
        if (imageSize <= 0 || (imageSize % BS) != 0) return null;
        long blockCount = imageSize / BS;
        if (blockCount > int.MaxValue) return null;
        long sbRel = superblockAbs - imageOffset;
        if (sbRel <= 0 || (sbRel % BS) != 0) return null;
        int total = (int)blockCount;
        int sbBlock = (int)(sbRel / BS);
        if (sbBlock >= total) return null;

        // PLAINTEXT_NOAUTH (and any other data-first image whose metadata is stored raw):
        // block 0 is file data, so PeekOuterState looks "encrypted", but the dinodes after the
        // superblock are plaintext. Trying XTS first destroys them.
        var plain = TryOpenDataFirstPlain(pkgStream, imageOffset, sbRel);
        if (plain is not null)
        {
            used = null;
            return plain;
        }

        // The superblock block is stored plaintext — read only its header for the seed.
        byte[] sbHead = new byte[0x400];
        pkgStream.Position = imageOffset + sbRel;
        ReadExactly(pkgStream, sbHead, sbHead.Length);
        ProsperoPfsHeader sb;
        try
        {
            using var ms0 = new MemoryStream(sbHead, writable: false);
            sb = ProsperoPfsHeader.ReadFromStream(ms0);
        }
        catch { return null; }
        if (sb.Seed is not { Length: 16 }) return null;
        byte[] seed = sb.Seed;

        foreach (var ekpfs in candidates)
            foreach (bool newCrypt in new[] { true, false })
            {
                try
                {
                    var (tweak, data) = Crypto.PfsGenEncKey(ekpfs, seed, newCrypt);

                    // Phase 1 (probe): treat every non-superblock as SIGNED so the directory tree + inodes
                    // parse. Only those metadata blocks are read; pfs_image.dat is not pulled into memory.
                    var kinds = new ProsperoOuterBlockKind[total];
                    Array.Fill(kinds, ProsperoOuterBlockKind.Signed);
                    kinds[sbBlock] = ProsperoOuterBlockKind.Plaintext;
                    var probe = new ProsperoDataFirstOuterReader(pkgStream, imageOffset, imageSize, tweak, data, kinds);

                    ProsperoPfsReader probeReader;
                    try
                    {
                        probeReader = new ProsperoPfsReader(
                            probe, 0, null, null, null, sbRel, skipDecryption: true);
                    }
                    catch
                    {
                        probe.Dispose();
                        continue;
                    }

                    // Phase 2: mark pfs_image.dat's data blocks as Data. The region runs from its start
                    // block up to the next outer file (naps, signed) or the superblock — not the inode
                    // compressed_size, which is the inner mount's logical size and can overshoot.
                    var pfsImg = probeReader.GetFile("pfs_image.dat") ?? FindByName(probeReader, "pfs_image.dat");
                    if (pfsImg != null)
                    {
                        long startBlk = pfsImg.offset / BS;
                        long endBlk = sbBlock;
                        foreach (var f in probeReader.GetAllFiles())
                        {
                            long fb = f.offset / BS;
                            if (fb > startBlk && fb < endBlk) endBlk = fb;
                        }
                        for (long b = startBlk; b < endBlk && b < total; b++)
                            if ((int)b != sbBlock) kinds[(int)b] = ProsperoOuterBlockKind.Data;
                    }

                    probe.InvalidateCache();
                    _ = probeReader.GetAllFiles().Any();
                    used = ekpfs;
                    return probeReader;
                }
                catch
                {
                    // Wrong key/newCrypt scheme; try the next combination.
                }
            }

        return null;
    }

    private static readonly byte[] PlaintextNoAuthSeed = "PPRPLAIN-NOAUTH!"u8.ToArray();

    private static bool IsPlaintextNoAuthOuter(Stream pkgStream, long imageOffset, long imageSize)
    {
        try
        {
            long sbAbs = ReadFihSuperblockOffset(pkgStream);
            long sbRel = sbAbs - imageOffset;
            if (sbRel <= 0 || (sbRel % ProsperoOuterPfsImage.DefaultBlockSize) != 0)
                return false;
            if (sbRel + 0x380 > imageSize)
                return false;
            byte[] head = new byte[0x380];
            pkgStream.Position = imageOffset + sbRel;
            ReadExactly(pkgStream, head, head.Length);
            return head.AsSpan(0x370, 16).SequenceEqual(PlaintextNoAuthSeed);
        }
        catch
        {
            return false;
        }
    }

    private static ProsperoPfsReader? TryOpenDataFirstPlain(Stream pkgStream, long imageOffset, long sbRel)
    {
        try
        {
            var reader = new ProsperoPfsReader(
                new LibProsperoPkg.Util.StreamReader(pkgStream, imageOffset),
                0, null, null, null, sbRel, skipDecryption: true);
            _ = reader.GetAllFiles().Any();
            return reader;
        }
        catch
        {
            return null;
        }
    }

    // The packer lifts param.json / icons / trophies / nptitle into the outer CNT so they are
    // not in the inner image. Write them back under sce_sys so a folder dump is complete.
    private static int RestoreCntSceSysFiles(
        string packagePath, string outputDirectory, string? passcode, Action<string> log)
    {
        ProsperoPkg pkg;
        try
        {
            pkg = ProsperoPkgReader.Read(packagePath);
        }
        catch
        {
            return 0;
        }

        var wanted = pkg.Entries.Where(ProsperoPkgEntryPayload.ShouldRestoreToSceSys).ToList();
        if (wanted.Count == 0)
            return 0;

        log($"Restoring {wanted.Count} sce_sys file(s) from package metadata...");
        string sceSys = Path.Combine(outputDirectory, "sce_sys");
        Directory.CreateDirectory(sceSys);
        int written = 0;
        using var stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        foreach (ProsperoPkgEntry entry in wanted)
        {
            string? resolved = ProsperoPkgEntryPayload.ResolveName(entry);
            if (string.IsNullOrEmpty(resolved))
                continue;
            string rel = resolved.Replace('\\', '/').TrimStart('/');
            string dest = Path.GetFullPath(Path.Combine(sceSys, rel));
            if (!dest.StartsWith(Path.GetFullPath(sceSys) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && dest != Path.GetFullPath(sceSys))
                continue;
            if (File.Exists(dest) && new FileInfo(dest).Length == entry.DataSize && !entry.Encrypted)
            {
                written++;
                continue;
            }

            byte[]? payload = ProsperoPkgEntryPayload.TryRead(stream, pkg, entry, passcode);
            if (payload is null)
            {
                log($"  sce_sys/{rel} (skipped, could not read)");
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, payload);
            written++;
            log($"  sce_sys/{rel} ({payload.Length:N0} bytes)");
        }

        return written;
    }

    private static int ExtractOuterMetadata(
        ProsperoPfsReader outer, string outputDirectory, string subdirectory, bool includeNestedImageRaw, Action<string> log)
    {
        string outDir = Path.Combine(outputDirectory, subdirectory);
        int count = 0;
        foreach (var file in outer.GetAllFiles())
        {
            bool isNested = string.Equals(file.name, "pfs_image.dat", StringComparison.OrdinalIgnoreCase);
            if (isNested && !includeNestedImageRaw)
                continue;

            string rel = file.name;
            string dest = Path.GetFullPath(Path.Combine(outDir, rel));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            // The nested inner image is written raw (undecoded) so its on-disk geometry can be
            // inspected byte-for-byte; all other outer files are PFSC-decoded when applicable.
            file.Save(dest, decompress: !isNested);
            count++;
            log(isNested ? $"  [outer] {rel} (raw nested image)" : $"  [outer] {rel}");
        }

        return count;
    }

    private static ProsperoPfsReader.File? FindByName(ProsperoPfsReader reader, string name)
        => reader.GetAllFiles().FirstOrDefault(f => string.Equals(f.name, name, StringComparison.OrdinalIgnoreCase));

    // True when the 16-byte head is a PS5 inner PFS superblock (version 2 + magic 20130315). A nwonly
    // data-first inner has no superblock at offset 0 (it starts with raw file data), so this is false.
    private static bool HasInnerSuperblock(byte[] head16)
        => head16.Length >= 16
           && BinaryPrimitives.ReadInt64LittleEndian(head16) == 2
           && BinaryPrimitives.ReadInt64LittleEndian(head16.AsSpan(8)) == 20130315;

    // Decodes and extracts a PS5 nwonly "data-first" inner pfs_image.dat using the sibling
    // naps_pkg_layout.dat: reconstruct the uncompressed mount, parse its PS5 metadata into a file tree,
    // and write each file (path-traversal safe). See ProsperoPs5InnerImageReader.
    private static List<ProsperoExtractedEntry> ExtractNwonlyInner(
        IMemoryReader innerOnDisk, ProsperoPfsReader.File innerFile, ProsperoPfsReader.File napsFile,
        string outputDirectory, Action<string> log, Action<long, long>? progress)
    {
        long napsSize = napsFile.size;
        if (napsSize <= 0 || napsSize > 64L * 1024 * 1024)
            throw new ProsperoExtractionException("naps_pkg_layout.dat is missing or implausibly large.");
        byte[] napsBytes = new byte[napsSize];
        napsFile.GetView().Read(0, napsBytes, 0, (int)napsSize);

        try
        {
            return ProsperoPs5InnerImageReader.ExtractFiles(
                innerOnDisk, innerFile.size, napsBytes, outputDirectory, log, progress);
        }
        catch (Exception ex) when (ex is not ProsperoExtractionException)
        {
            throw new ProsperoExtractionException(
                "Failed to decode the nwonly data-first inner image via naps_pkg_layout.dat: " + ex.Message, ex);
        }
    }

    // Reads the finalized-image header fields the extractor needs (signed byte, PFS offset/size),
    // validating the magic and applying safe fallbacks for absent offset/size fields.
    private static (byte SignedByte, long PfsOffset, long PfsSize) ReadFihFields(Stream stream, long fileLength)
    {
        byte[] header = new byte[0x100];
        stream.Position = 0;
        ReadExactly(stream, header, header.Length);
        var span = header.AsSpan();

        if (!span[..4].SequenceEqual(ProsperoPkgLayout.FihMagic))
            throw new ProsperoExtractionException("Not a finalized image (missing FIH magic).");

        byte signedByte = header[ProsperoPkgLayout.FihSignedByteOffset];
        long pfsOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(span[ProsperoPkgLayout.FihPfsImageOffsetField..]);
        long pfsSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(span[ProsperoPkgLayout.FihPfsImageSizeField..]);

        if (pfsOffset <= 0 || pfsOffset >= fileLength)
            pfsOffset = ProsperoPkgLayout.FihHeaderRegionSize; // 0x10000
        // pfsOffset is now bounded by fileLength, so compare the size against the remaining space
        // rather than pfsOffset + pfsSize, which a crafted header could overflow past the check.
        if (pfsSize <= 0 || pfsSize > fileLength - pfsOffset)
            pfsSize = fileLength - pfsOffset;

        return (signedByte, pfsOffset, pfsSize);
    }

    // Reads the outer PFS superblock (plaintext for debug/keyed images) and classifies it. A
    // finalized retail image encrypts the whole outer PFS including block 0, so its superblock does
    // not parse without the console image key; that case is reported as Unreadable (i.e. gated).
    private static OuterPfsState PeekOuterState(IMemoryReader reader)
    {
        byte[] buffer = new byte[SuperblockPeekSize];
        reader.Read(0, buffer, 0, buffer.Length);
        using var ms = new MemoryStream(buffer);
        try
        {
            var hdr = ProsperoPfsHeader.ReadFromStream(ms);
            return hdr.Mode.HasFlag(ProsperoPfsMode.Encrypted) ? OuterPfsState.Encrypted : OuterPfsState.Plaintext;
        }
        catch (Exception)
        {
            // The superblock is not a readable plaintext PFS header: the outer filesystem is
            // encrypted at block 0 (a finalized retail image) or is not a PFS. Either way it needs a
            // supplied key to proceed.
            return OuterPfsState.Unreadable;
        }
    }

    private enum OuterPfsState
    {
        Plaintext,
        Encrypted,
        Unreadable,
    }

    private static string? TryReadContentId(string packagePath)
    {
        try
        {
            return ProsperoPkgReader.Read(packagePath).Header?.ContentId;
        }
        catch
        {
            return null;
        }
    }

    private static void ReadExactly(Stream stream, byte[] buffer, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = stream.Read(buffer, read, count - read);
            if (n == 0)
                throw new ProsperoExtractionException("Unexpected end of file while reading the finalized-image header.");
            read += n;
        }
    }
}
