// LibProsperoPkg - A library for building and inspecting PS5 packages.
// Copyright (C) 2026 SvenGDK
//
// ---------------------------------------------------------------------------------------------------
// PS5 nwonly INNER pfs_image.dat READER (the inverse of ProsperoPs5InnerImageAssembler). A data-first
// inner image has NO on-disk block table: it is a raw concatenation of per-file payloads (raw or
// header-stripped Kraken chunks) followed by the Kraken-compressed metadata region. The per-block
// geometry (on-disk offset, compressed/uncompressed sizes, raw-vs-Kraken flag, even/odd split, byte
// shuffle) lives ONLY in the sibling naps_pkg_layout.dat's CblockInfo section.
//
// This reconstructs the uncompressed PFS mount from (inner image bytes + naps bytes) so the standard
// ProsperoPfsReader can walk it. The naps CblockInfo section supplies the per-block decode geometry for
// all file data and the metadata region.
// ---------------------------------------------------------------------------------------------------
#nullable enable
using LibProsperoPkg.PFS.Compression;
using LibProsperoPkg.PFS.Compression.Oodle;
using LibProsperoPkg.PKG;
using LibProsperoPkg.Util;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LibProsperoPkg.PFS;

/// <summary>A regular file discovered in a reconstructed inner mount.</summary>
public sealed class ProsperoPs5InnerFileEntry
{
    /// <summary>User-root-relative path (e.g. <c>sce_sys/keystone</c> or <c>application.ps.bundle</c>).</summary>
    public required string Path { get; init; }

    /// <summary>Byte offset of the file's (already decoded) data within the reconstructed mount.</summary>
    public required ulong LogicalOffset { get; init; }

    /// <summary>File size in bytes.</summary>
    public required long Size { get; init; }
}

/// <summary>The reconstructed inner PFS mount plus the offset of its superblock.</summary>
public sealed class ProsperoPs5InnerMountResult
{
    /// <summary>The uncompressed inner PFS mount bytes (walk with <see cref="ProsperoPfsReader"/>).</summary>
    public required byte[] Mount { get; init; }

    /// <summary>
    /// Byte offset of the PFS superblock within <see cref="Mount"/>. A nwonly inner image is data-first,
    /// so the superblock is at the metadata-region base (not offset 0).
    /// </summary>
    public required long SuperblockOffset { get; init; }
}

/// <summary>
/// Reconstructs the uncompressed inner PFS mount of a PS5 nwonly package from its data-first
/// <c>pfs_image.dat</c> and the sibling <c>naps_pkg_layout.dat</c>. The result is a plaintext PFS
/// image whose superblock is at the metadata-region base that <see cref="ProsperoPfsReader"/> can walk.
/// </summary>
public static class ProsperoPs5InnerImageReader
{
    private const int Chunk128K = 0x20000;   // even/odd sub-chunk split
    private const long Ublock256K = 0x40000; // one CblockInfo STD covers up to one 256K ublock

    /// <summary>
    /// Reconstructs the uncompressed PFS mount from a data-first inner image and its naps layout. Files
    /// and the metadata region are decoded to their logical offsets; unreferenced inter-file padding is
    /// left zero-filled.
    /// </summary>
    /// <param name="innerImage">The on-disk data-first <c>pfs_image.dat</c> bytes.</param>
    /// <param name="naps">The <c>naps_pkg_layout.dat</c> bytes (its CblockInfo drives the decode).</param>
    /// <param name="mountSize">
    /// The uncompressed mount size (Ndblock·64K). Pass 0 to derive it from the naps: the mount end is the
    /// largest 64K-aligned <c>fidx</c> boundary (trailing typed sentinels are not block-aligned).
    /// </param>
    public static ProsperoPs5InnerMountResult ReconstructMount(
        ReadOnlySpan<byte> innerImage, ReadOnlySpan<byte> naps, long mountSize = 0)
    {
        NapsLayoutDocument doc = ProsperoNapsLayout.Parse(naps);
        IReadOnlyList<NapsCblockInfoEntry> cb = doc.CblockInfos;

        long[] rawOffsets = doc.FileOffsets.Select(f => (long)f.UncompressedOffsetStart).ToArray();
        if (mountSize <= 0)
        {
            // Derive the mount size: the mount end is a 64K-aligned fidx boundary; typed/garbage sentinels
            // are not block-aligned, so the largest block-aligned boundary is Ndblock*64K.
            mountSize = rawOffsets.Where(v => v > 0 && (v & 0xFFFF) == 0).DefaultIfEmpty(0).Max();
        }
        if (mountSize <= 0 || mountSize > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(mountSize), mountSize, "Implausible inner mount size.");

        // fidx = uncompressed file-offset boundaries. Only values inside the mount are real block
        // boundaries; trailing sentinels (huge/typed) are ignored by the min-above-cursor lookup.
        long[] boundaries = rawOffsets
            .Where(v => v > 0 && v <= mountSize)
            .Distinct().OrderBy(v => v).ToArray();

        // The metadata region base (= superblock offset) is the last file-offset boundary below the mount
        // end; the data files occupy everything below it and the metadata PFS is stored above it.
        long metaBase = boundaries.LastOrDefault(v => v < mountSize);

        var mount = new byte[mountSize];
        byte[] innerArr = innerImage.ToArray();

        long onDisk = 0, uncompOff = 0;
        for (int i = 0; i < cb.Count; i++)
        {
            NapsCblockInfoEntry e = cb[i];
            if (TryApplyRunBase(e, cb, i, ref onDisk))
                continue;

            long fileEnd = NextBoundary(boundaries, uncompOff, mountSize);
            long uncompLen = Math.Min(Ublock256K, fileEnd - uncompOff);
            if (uncompLen <= 0)
                break;
            if (!TryBlockSizes(cb, i, e, uncompLen, onDisk, innerImage.Length, out int evenComp, out int totalComp, out bool kraken))
                break;

            DecodeBlockInto(innerArr, onDisk, totalComp, evenComp, (int)uncompLen, kraken,
                            e.KdePredictor, e.ShuffleIdx, mount, (int)uncompOff);

            uncompOff += uncompLen;
            onDisk += DiskAdvance(kraken, totalComp, uncompLen);
        }

        // Locate the metadata superblock (version 2 + magic 20130315) at a 64K boundary. The metadata PFS
        // sits at the top of the mount, so scan downward and take the highest match; fall back to the
        // fidx-derived base. This is robust to per-package fidx layout differences.
        long superblockOffset = metaBase;
        for (long p = (mountSize - 0x10000) & ~0xFFFFL; p >= 0; p -= 0x10000)
        {
            if (System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(mount.AsSpan((int)p)) == 2 &&
                System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(mount.AsSpan((int)p + 8)) == 20130315)
            {
                superblockOffset = p;
                break;
            }
        }

        return new ProsperoPs5InnerMountResult { Mount = mount, SuperblockOffset = superblockOffset };
    }

    /// <summary>
    /// Parses the reconstructed inner mount's PS5 metadata (superblock + inode table + dirents) and returns
    /// every regular file under <c>uroot</c> with its user-root-relative path, logical offset and size. The
    /// file bytes are <c>mount[LogicalOffset .. LogicalOffset+Size]</c> (already decoded by
    /// <see cref="ReconstructMount"/>). The PS5 inner inode layout (LogicalOffset@0x60, parent@0x6c)
    /// differs from the layout <see cref="ProsperoPfsReader"/> reads, so this does not use it.
    /// </summary>
    /// <summary>
    /// Lists every regular file under <c>uroot</c> without reconstructing the full uncompressed
    /// mount. Only the metadata region (superblock, inodes, directory blocks) is decoded, so a
    /// 100+ GiB inner image can be listed without loading it into memory.
    /// </summary>
    public static IReadOnlyList<ProsperoPs5InnerFileEntry> ListFileTree(
        IMemoryReader innerOnDisk, long innerSize, ReadOnlySpan<byte> naps)
    {
        ArgumentNullException.ThrowIfNull(innerOnDisk);
        if (TryParsePublisherLayout(naps, out NapsLayoutDocument? publisher)
            && TryOpenPublisherLayout(innerOnDisk, innerSize, publisher!, out _, out IReadOnlyList<ProsperoPs5InnerFileEntry>? planned))
            return planned!;
        NapsLayoutDocument doc = ProsperoNapsLayout.Parse(naps);

        IReadOnlyList<NapsCblockInfoEntry> cb = doc.CblockInfos;
        long[] rawOffsets = doc.FileOffsets.Select(f => (long)f.UncompressedOffsetStart).ToArray();
        long mountSize = rawOffsets.Where(v => v > 0 && (v & 0xFFFF) == 0).DefaultIfEmpty(0).Max();
        if (mountSize <= 0)
            throw new InvalidOperationException("Could not derive the inner mount size from naps_pkg_layout.dat.");

        long[] boundaries = rawOffsets
            .Where(v => v > 0 && v <= mountSize)
            .Distinct().OrderBy(v => v).ToArray();
        long metaBase = boundaries.LastOrDefault(v => v < mountSize);
        long metaLen = mountSize - metaBase;
        if (metaBase <= 0 || metaLen <= 0 || metaLen > 512L * 1024 * 1024)
            throw new InvalidOperationException("Inner metadata region is missing or implausibly large.");

        var meta = new byte[metaLen];
        long onDisk = 0, uncompOff = 0;
        for (int i = 0; i < cb.Count; i++)
        {
            NapsCblockInfoEntry e = cb[i];
            if (TryApplyRunBase(e, cb, i, ref onDisk))
                continue;

            long fileEnd = NextBoundary(boundaries, uncompOff, mountSize);
            long uncompLen = Math.Min(Ublock256K, fileEnd - uncompOff);
            if (uncompLen <= 0)
                break;
            if (!TryBlockSizes(cb, i, e, uncompLen, onDisk, innerSize, out int evenComp, out int totalComp, out bool kraken))
                break;
            long blockEnd = uncompOff + uncompLen;

            if (blockEnd > metaBase)
            {
                int windowLen = kraken ? Math.Max(totalComp, 1) : (int)uncompLen;
                var window = new byte[windowLen];
                if (onDisk >= 0 && onDisk < innerSize)
                {
                    int avail = (int)Math.Min(window.Length, innerSize - onDisk);
                    if (avail > 0)
                        innerOnDisk.Read(onDisk, window, 0, avail);
                }

                if (uncompOff >= metaBase)
                {
                    DecodeBlockInto(window, 0, totalComp, evenComp, (int)uncompLen, kraken,
                        e.KdePredictor, e.ShuffleIdx, meta, (int)(uncompOff - metaBase));
                }
                else
                {
                    var tmp = new byte[uncompLen];
                    DecodeBlockInto(window, 0, totalComp, evenComp, (int)uncompLen, kraken,
                        e.KdePredictor, e.ShuffleIdx, tmp, 0);
                    long skip = metaBase - uncompOff;
                    int copy = (int)(uncompLen - skip);
                    if (copy > 0)
                        Array.Copy(tmp, (int)skip, meta, 0, Math.Min(copy, meta.Length));
                }
            }

            uncompOff += uncompLen;
            onDisk += DiskAdvance(kraken, totalComp, uncompLen);
        }

        return ReadFileTreeFromMetadata(meta, metaBase);
    }

    private static bool TryParsePublisherLayout(ReadOnlySpan<byte> naps, out NapsLayoutDocument? doc)
    {
        try
        {
            doc = ProsperoNapsLayout.ParsePublisherLayout(naps);
            return true;
        }
        catch
        {
            doc = null;
            return false;
        }
    }

    private static bool TryOpenPublisherLayout(
        IMemoryReader innerOnDisk, long innerSize, NapsLayoutDocument doc,
        out ProsperoNapsPlan? plan, out IReadOnlyList<ProsperoPs5InnerFileEntry>? tree)
    {
        plan = null;
        tree = null;
        if (!ProsperoNapsLogicalImage.TryBuildPlan(doc, out ProsperoNapsPlan built))
            return false;
        ProsperoNapsLogicalFile? metaFile = ProsperoNapsLogicalImage.LastMetadataFile(built);
        if (metaFile is not { } meta || meta.Length > int.MaxValue)
            return false;
        try
        {
            byte[] region = ProsperoNapsLogicalImage.DecompressRange(
                innerOnDisk, innerSize, doc, built, meta.UncompressedOffset, (int)meta.Length);
            if (region.Length < 16
                || System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(region) != 2
                || System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(region.AsSpan(8)) != 20130315)
                return false;
            tree = ReadFileTreeFromMetadata(region, meta.UncompressedOffset);
            plan = built;
            return tree.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static List<ProsperoExtractedEntry> ExtractFilesFromPlan(
        IMemoryReader innerOnDisk, long innerSize, NapsLayoutDocument doc, ProsperoNapsPlan plan,
        IReadOnlyList<ProsperoPs5InnerFileEntry> tree, string outputDirectory,
        Action<string>? log, Action<long, long>? progress)
    {
        Directory.CreateDirectory(outputDirectory);
        string rootFull = Path.GetFullPath(outputDirectory).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        var files = tree
            .OrderBy(f => f.LogicalOffset)
            .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var destinations = new string[files.Length];
        var streams = new FileStream?[files.Length];
        var have = new long[files.Length];
        var writtenOut = new List<ProsperoExtractedEntry>(files.Length);
        long totalBytes = 0;
        int skippedFiles = 0;
        long skippedBytes = 0;
        for (int i = 0; i < files.Length; i++)
        {
            string rel = files[i].Path.Replace('\\', '/');
            string dest = Path.GetFullPath(Path.Combine(rootFull, rel));
            if (!dest.StartsWith(rootFull, StringComparison.Ordinal))
                throw new ProsperoExtractionException($"Refusing to write outside the output directory: '{rel}'.");
            destinations[i] = dest;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (files[i].Size <= 0)
            {
                if (!File.Exists(dest))
                    File.WriteAllBytes(dest, []);
                writtenOut.Add(new ProsperoExtractedEntry { RelativePath = rel, Size = 0, IsCompressed = false });
                have[i] = 0;
                continue;
            }
            totalBytes += files[i].Size;
            have[i] = ProbeExistingBytes(dest, files[i].Size);
            if (have[i] == files[i].Size)
            {
                skippedFiles++;
                skippedBytes += files[i].Size;
                writtenOut.Add(new ProsperoExtractedEntry { RelativePath = rel, Size = files[i].Size, IsCompressed = false });
            }
            else if (have[i] > 0)
                log?.Invoke($"  {rel} (resume at {have[i]:N0} of {files[i].Size:N0} bytes)");
        }

        log?.Invoke($"Extracting {files.Length} file(s), {totalBytes:N0} bytes...");
        if (skippedFiles > 0)
            log?.Invoke($"Skipping {skippedFiles} complete file(s), {skippedBytes:N0} bytes already on disk.");
        long copied = skippedBytes;
        var ticker = new ExtractProgressTicker(progress, Math.Max(1, totalBytes));
        ticker.Report(copied, force: true);
        int fileIndex = 0;
        try
        {
            foreach (ProsperoNapsSpan span in plan.Spans)
            {
                long spanEnd = span.UncompressedOffset + span.UncompressedLength;
                while (fileIndex < files.Length
                    && ((long)files[fileIndex].LogicalOffset + Math.Max(0, files[fileIndex].Size)) <= span.UncompressedOffset)
                {
                    streams[fileIndex]?.Dispose();
                    streams[fileIndex] = null;
                    fileIndex++;
                }

                bool overlaps = false;
                for (int j = fileIndex; j < files.Length; j++)
                {
                    if (files[j].Size <= 0)
                        continue;
                    long start = (long)files[j].LogicalOffset;
                    if (start >= spanEnd)
                        break;
                    if (RangeNeedsWrite(start, files[j].Size, have[j], span.UncompressedOffset, spanEnd))
                    {
                        overlaps = true;
                        break;
                    }
                }
                if (!overlaps)
                    continue;

                byte[] decoded = ProsperoNapsLogicalImage.DecodeSpan(innerOnDisk, innerSize, doc, span);
                for (int j = fileIndex; j < files.Length; j++)
                {
                    if (files[j].Size <= 0 || have[j] >= files[j].Size)
                        continue;
                    long start = (long)files[j].LogicalOffset;
                    if (start >= spanEnd)
                        break;
                    long end = start + files[j].Size;
                    if (end <= span.UncompressedOffset)
                        continue;
                    long from = Math.Max(start + have[j], Math.Max(start, span.UncompressedOffset));
                    long to = Math.Min(end, spanEnd);
                    int n = (int)(to - from);
                    if (n <= 0)
                        continue;

                    FileStream fs = streams[j] ??= OpenExtractStream(destinations[j], have[j]);
                    if (have[j] == 0 && from == start)
                        log?.Invoke($"  {files[j].Path.Replace('\\', '/')} ({files[j].Size:N0} bytes)");
                    fs.Position = from - start;
                    fs.Write(decoded, (int)(from - span.UncompressedOffset), n);
                    have[j] = from - start + n;
                    copied += n;
                    ticker.Report(copied);
                }
            }
        }
        finally
        {
            for (int i = 0; i < streams.Length; i++)
                streams[i]?.Dispose();
        }

        var done = new HashSet<string>(writtenOut.Select(e => e.RelativePath), StringComparer.Ordinal);
        for (int i = 0; i < files.Length; i++)
        {
            if (files[i].Size <= 0)
                continue;
            string rel = files[i].Path.Replace('\\', '/');
            var info = new FileInfo(destinations[i]);
            if (!info.Exists || info.Length != files[i].Size)
                throw new ProsperoExtractionException(
                    $"Inner file '{rel}' was not fully written ({(info.Exists ? info.Length : 0):N0} of {files[i].Size:N0} bytes).");
            if (done.Add(rel))
                writtenOut.Add(new ProsperoExtractedEntry { RelativePath = rel, Size = files[i].Size, IsCompressed = false });
        }

        ticker.Report(Math.Max(1, totalBytes), force: true);
        return writtenOut;
    }

    /// <summary>
    /// Writes every inner file to <paramref name="outputDirectory"/> by decoding only the CblockInfo
    /// windows that overlap each file. The uncompressed mount is never allocated.
    /// </summary>
    public static List<ProsperoExtractedEntry> ExtractFiles(
        IMemoryReader innerOnDisk, long innerSize, ReadOnlySpan<byte> naps,
        string outputDirectory, Action<string>? log, Action<long, long>? progress)
    {
        ArgumentNullException.ThrowIfNull(innerOnDisk);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        if (TryParsePublisherLayout(naps, out NapsLayoutDocument? publisher)
            && TryOpenPublisherLayout(innerOnDisk, innerSize, publisher!, out ProsperoNapsPlan? plan, out IReadOnlyList<ProsperoPs5InnerFileEntry>? planned))
            return ExtractFilesFromPlan(innerOnDisk, innerSize, publisher!, plan!, planned!, outputDirectory, log, progress);

        NapsLayoutDocument doc = ProsperoNapsLayout.Parse(naps);
        IReadOnlyList<ProsperoPs5InnerFileEntry> tree;

        tree = ListFileTree(innerOnDisk, innerSize, naps);
        IReadOnlyList<NapsCblockInfoEntry> cb = doc.CblockInfos;
        long[] rawOffsets = doc.FileOffsets.Select(f => (long)f.UncompressedOffsetStart).ToArray();
        long mountSize = rawOffsets.Where(v => v > 0 && (v & 0xFFFF) == 0).DefaultIfEmpty(0).Max();
        if (mountSize <= 0)
            throw new InvalidOperationException("Could not derive the inner mount size from naps_pkg_layout.dat.");
        long[] boundaries = rawOffsets
            .Where(v => v > 0 && v <= mountSize)
            .Distinct().OrderBy(v => v).ToArray();

        Directory.CreateDirectory(outputDirectory);
        string rootFull = Path.GetFullPath(outputDirectory).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        var files = tree
            .OrderBy(f => f.LogicalOffset)
            .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var destinations = new string[files.Length];
        var streams = new FileStream?[files.Length];
        var have = new long[files.Length];
        var writtenOut = new List<ProsperoExtractedEntry>(files.Length);
        long totalBytes = 0;
        int skippedFiles = 0;
        long skippedBytes = 0;
        for (int i = 0; i < files.Length; i++)
        {
            string rel = files[i].Path.Replace('\\', '/');
            string dest = Path.GetFullPath(Path.Combine(rootFull, rel));
            if (!dest.StartsWith(rootFull, StringComparison.Ordinal))
                throw new ProsperoExtractionException($"Refusing to write outside the output directory: '{rel}'.");
            destinations[i] = dest;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (files[i].Size <= 0)
            {
                if (!File.Exists(dest))
                    File.WriteAllBytes(dest, []);
                writtenOut.Add(new ProsperoExtractedEntry { RelativePath = rel, Size = 0, IsCompressed = false });
                continue;
            }
            totalBytes += files[i].Size;
            have[i] = ProbeExistingBytes(dest, files[i].Size);
            if (have[i] == files[i].Size)
            {
                skippedFiles++;
                skippedBytes += files[i].Size;
                writtenOut.Add(new ProsperoExtractedEntry { RelativePath = rel, Size = files[i].Size, IsCompressed = false });
            }
            else if (have[i] > 0)
                log?.Invoke($"  {rel} (resume at {have[i]:N0} of {files[i].Size:N0} bytes)");
        }

        log?.Invoke($"Extracting {files.Length} file(s), {totalBytes:N0} bytes...");
        if (skippedFiles > 0)
            log?.Invoke($"Skipping {skippedFiles} complete file(s), {skippedBytes:N0} bytes already on disk.");

        var scratch = new byte[Ublock256K];
        long onDisk = 0, uncompOff = 0, copied = skippedBytes;
        var ticker = new ExtractProgressTicker(progress, Math.Max(1, totalBytes));
        ticker.Report(copied, force: true);
        int fileIndex = 0;

        try
        {
            for (int i = 0; i < cb.Count; i++)
            {
                NapsCblockInfoEntry e = cb[i];
                if (TryApplyRunBase(e, cb, i, ref onDisk))
                    continue;

                long fileEnd = NextBoundary(boundaries, uncompOff, mountSize);
                long uncompLen = Math.Min(Ublock256K, fileEnd - uncompOff);
                if (uncompLen <= 0)
                    break;
                if (!TryBlockSizes(cb, i, e, uncompLen, onDisk, innerSize, out int evenComp, out int totalComp, out bool kraken))
                    break;
                long blockEnd = uncompOff + uncompLen;

                while (fileIndex < files.Length
                    && ((long)files[fileIndex].LogicalOffset + Math.Max(0, files[fileIndex].Size)) <= uncompOff)
                {
                    streams[fileIndex]?.Dispose();
                    streams[fileIndex] = null;
                    fileIndex++;
                }

                bool overlaps = false;
                for (int j = fileIndex; j < files.Length; j++)
                {
                    if (files[j].Size <= 0)
                        continue;
                    long start = (long)files[j].LogicalOffset;
                    if (start >= blockEnd)
                        break;
                    if (RangeNeedsWrite(start, files[j].Size, have[j], uncompOff, blockEnd))
                    {
                        overlaps = true;
                        break;
                    }
                }

                if (overlaps)
                {
                    Array.Clear(scratch, 0, (int)uncompLen);
                    DecodeBlockFromReader(innerOnDisk, innerSize, onDisk, totalComp, evenComp,
                        (int)uncompLen, kraken, e.KdePredictor, e.ShuffleIdx, scratch, 0);

                    for (int j = fileIndex; j < files.Length; j++)
                    {
                        if (files[j].Size <= 0 || have[j] >= files[j].Size)
                            continue;
                        long start = (long)files[j].LogicalOffset;
                        if (start >= blockEnd)
                            break;
                        long end = start + files[j].Size;
                        if (end <= uncompOff)
                            continue;

                        long from = Math.Max(start + have[j], Math.Max(start, uncompOff));
                        long to = Math.Min(end, blockEnd);
                        int n = (int)(to - from);
                        if (n <= 0)
                            continue;

                        FileStream fs = streams[j] ??= OpenExtractStream(destinations[j], have[j]);
                        if (have[j] == 0 && from == start)
                            log?.Invoke($"  {files[j].Path.Replace('\\', '/')} ({files[j].Size:N0} bytes)");

                        fs.Position = from - start;
                        fs.Write(scratch, (int)(from - uncompOff), n);
                        have[j] = from - start + n;
                        copied += n;
                        ticker.Report(copied);
                    }
                }

                uncompOff += uncompLen;
                onDisk += DiskAdvance(kraken, totalComp, uncompLen);
            }
        }
        finally
        {
            for (int i = 0; i < streams.Length; i++)
                streams[i]?.Dispose();
        }

        var done = new HashSet<string>(writtenOut.Select(e => e.RelativePath), StringComparer.Ordinal);
        for (int i = 0; i < files.Length; i++)
        {
            if (files[i].Size <= 0)
                continue;
            string rel = files[i].Path.Replace('\\', '/');
            var info = new FileInfo(destinations[i]);
            if (!info.Exists || info.Length != files[i].Size)
                throw new ProsperoExtractionException(
                    $"Inner file '{rel}' was not fully written ({(info.Exists ? info.Length : 0):N0} of {files[i].Size:N0} bytes).");
            if (done.Add(rel))
                writtenOut.Add(new ProsperoExtractedEntry { RelativePath = rel, Size = files[i].Size, IsCompressed = false });
        }

        ticker.Report(Math.Max(1, totalBytes), force: true);
        return writtenOut;
    }

    public static IReadOnlyList<ProsperoPs5InnerFileEntry> ReadFileTree(byte[] mount, long metaBase)
    {
        ReadOnlySpan<byte> sb = mount.AsSpan((int)metaBase);
        if (System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(sb) != 2 ||
            System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(sb[8..]) != 20130315)
            throw new InvalidOperationException("Inner mount metadata does not start with a PS5 PFS superblock.");

        int blockSize = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(sb[0x20..]);
        int inodeCount = (int)System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(sb[0x30..]);
        if (blockSize <= 0 || inodeCount <= 0 || inodeCount > 1_000_000)
            throw new InvalidOperationException("Implausible inner metadata superblock (block size / inode count).");

        // Parse the inode table (block 1). PS5 inner inode: Mode@0, Size@8, LogicalOffset@0x60, parent@0x6c.
        long inodeTable = metaBase + blockSize;
        var nodes = new InnerInode[inodeCount];
        for (int i = 0; i < inodeCount; i++)
        {
            ReadOnlySpan<byte> e = mount.AsSpan((int)(inodeTable + i * 0xA8), 0xA8);
            nodes[i] = new InnerInode
            {
                Mode = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(e),
                Size = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(e[8..]),
                LogicalOffset = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(e[0x60..]),
            };
        }

        // Walk the directory tree from the super-root (inode 0), collecting files under "uroot".
        var files = new List<ProsperoPs5InnerFileEntry>();
        WalkDir(mount, 0, blockSize, nodes, 0, "", underUroot: false, files, new HashSet<uint>());
        return files;
    }

    private static IReadOnlyList<ProsperoPs5InnerFileEntry> ReadFileTreeFromMetadata(byte[] meta, long metaBase)
    {
        ReadOnlySpan<byte> sb = meta;
        if (System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(sb) != 2 ||
            System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(sb[8..]) != 20130315)
        {
            for (int p = 0; p + 16 <= meta.Length; p += 0x10000)
            {
                if (System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(meta.AsSpan(p)) == 2 &&
                    System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(meta.AsSpan(p + 8)) == 20130315)
                {
                    byte[] slice = meta.AsSpan(p).ToArray();
                    return ReadFileTreeFromMetadata(slice, metaBase + p);
                }
            }
            throw new InvalidOperationException("Inner metadata region does not start with a PS5 PFS superblock.");
        }

        int blockSize = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(sb[0x20..]);
        int inodeCount = (int)System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(sb[0x30..]);
        if (blockSize <= 0 || inodeCount <= 0 || inodeCount > 1_000_000)
            throw new InvalidOperationException("Implausible inner metadata superblock (block size / inode count).");

        var nodes = new InnerInode[inodeCount];
        for (int i = 0; i < inodeCount; i++)
        {
            ReadOnlySpan<byte> e = meta.AsSpan(blockSize + i * 0xA8, 0xA8);
            nodes[i] = new InnerInode
            {
                Mode = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(e),
                Size = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(e[8..]),
                LogicalOffset = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(e[0x60..]),
            };
        }

        var files = new List<ProsperoPs5InnerFileEntry>();
        WalkDir(meta, metaBase, blockSize, nodes, 0, "", underUroot: false, files, new HashSet<uint>());
        return files;
    }

    private sealed class InnerInode
    {
        public ushort Mode;
        public long Size;
        public ulong LogicalOffset;
        public bool IsDirectory => (Mode & 0x4000) != 0;
    }

    private static void WalkDir(byte[] mount, long regionBase, int blockSize, InnerInode[] nodes, uint dirInode,
        string path, bool underUroot, List<ProsperoPs5InnerFileEntry> files, HashSet<uint> seen)
    {
        if (dirInode >= nodes.Length || !seen.Add(dirInode))
            return;
        InnerInode dir = nodes[dirInode];
        if (!dir.IsDirectory || dir.Size <= 0)
            return;

        long start = (long)dir.LogicalOffset - regionBase;
        if (start < 0 || start >= mount.Length)
            return;
        long end = Math.Min(start + dir.Size, mount.Length);
        if (end <= start)
            return;
        using var ms = new System.IO.MemoryStream(mount, (int)start, (int)(end - start), writable: false);
        while (ms.Position + 0x10 <= ms.Length)
        {
            long entryPos = ms.Position;
            ProsperoPfsDirent d;
            try { d = ProsperoPfsDirent.ReadFromStream(ms); }
            catch { break; }
            if (d.EntSize <= 0) break;
            ms.Position = entryPos + d.EntSize;

            string name = d.Name;
            if (string.IsNullOrEmpty(name) || name == "." || name == "..")
                continue;
            if (d.InodeNumber >= nodes.Length)
                continue;

            // The super-root wraps the content under "uroot" (its other children are internal flat-path
            // tables); only files at or below uroot are real package content.
            bool childUnderUroot = underUroot || name == "uroot";
            string childPath = path.Length == 0
                ? (name == "uroot" ? "" : name)
                : path + "/" + name;

            if (d.Type == ProsperoDirentType.Directory)
                WalkDir(mount, regionBase, blockSize, nodes, d.InodeNumber, childPath, childUnderUroot, files, seen);
            else if (d.Type == ProsperoDirentType.File && underUroot && childPath.Length > 0)
                files.Add(new ProsperoPs5InnerFileEntry
                {
                    Path = childPath,
                    LogicalOffset = nodes[d.InodeNumber].LogicalOffset,
                    Size = nodes[d.InodeNumber].Size,
                });
        }
    }

    private sealed class ExtractProgressTicker
    {
        private readonly Action<long, long>? _progress;
        private readonly long _total;
        private readonly long _step;
        private long _lastBytes = -1;
        private long _lastTick;

        public ExtractProgressTicker(Action<long, long>? progress, long total)
        {
            _progress = progress;
            _total = Math.Max(1, total);
            _step = Math.Max(256 * 1024, _total / 1000);
        }

        public void Report(long copied, bool force = false)
        {
            if (_progress is null)
                return;
            long now = Environment.TickCount64;
            if (!force && copied < _total && copied - _lastBytes < _step && now - _lastTick < 200)
                return;
            _lastBytes = copied;
            _lastTick = now;
            _progress(Math.Min(copied, _total), _total);
        }
    }

    private static long ProbeExistingBytes(string path, long expected)
    {
        if (expected <= 0 || !File.Exists(path))
            return 0;
        long len = new FileInfo(path).Length;
        if (len == expected)
            return expected;
        if (len > expected)
        {
            File.Delete(path);
            return 0;
        }
        return len;
    }

    private static bool RangeNeedsWrite(long fileStart, long fileSize, long have, long rangeStart, long rangeEnd)
    {
        if (have >= fileSize)
            return false;
        long needFrom = fileStart + have;
        long fileEnd = fileStart + fileSize;
        return needFrom < rangeEnd && fileEnd > rangeStart;
    }

    private static FileStream OpenExtractStream(string path, long have)
    {
        FileMode mode = have > 0 ? FileMode.Open : FileMode.Create;
        return new FileStream(path, mode, FileAccess.Write, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
    }

    private static long DiskAdvance(bool compressedOnDisk, int totalComp, long uncompLen)
    {
        if (compressedOnDisk || (totalComp > 0 && totalComp < uncompLen))
            return Math.Max(totalComp, 0);
        return uncompLen;
    }

    private static bool TryApplyRunBase(
        NapsCblockInfoEntry e, IReadOnlyList<NapsCblockInfoEntry> cb, int i, ref long onDisk)
    {
        if (!e.IsRunBase)
            return false;
        long frac = (i + 1 < cb.Count && !cb[i + 1].IsRunBase)
            ? (cb[i + 1].CoffsetStartMod256K & 0x7fff) : 0;
        long hinted = ((long)e.TweakIdxStart << 15) + frac;
        // Trailing sentinel run-bases (tweak 0) must not rewind a cursor that already sits
        // near the end of a 100+ GiB image — that was wiping the metadata decode.
        if (e.TweakIdxStart == 0 && onDisk > 0x8000)
            return true;
        onDisk = hinted;
        return true;
    }

    private static bool TryBlockSizes(
        IReadOnlyList<NapsCblockInfoEntry> cb, int i, NapsCblockInfoEntry e,
        long uncompLen, long onDisk, long innerSize,
        out int evenComp, out int totalComp, out bool kraken)
    {
        evenComp = (int)(e.ClenEvenMinus1 / 2 + 1);
        kraken = e.KdePredictor is 2 or 3;
        if (i + 1 < cb.Count)
        {
            NapsCblockInfoEntry next = cb[i + 1];
            long thisRel = e.CoffsetStartMod256K;
            long nextRel = next.IsRunBase ? next.CoffsetEndMod256K : next.CoffsetStartMod256K;
            totalComp = (int)(nextRel - thisRel);
            return true;
        }

        long remain = innerSize - onDisk;
        if (remain <= 0 || remain > int.MaxValue)
        {
            totalComp = 0;
            return false;
        }
        totalComp = kraken ? (int)remain : (int)uncompLen;
        return totalComp > 0;
    }

    private static long NextBoundary(long[] boundaries, long cur, long mountSize)
    {
        foreach (long b in boundaries)
            if (b > cur)
                return b;
        return mountSize;
    }

    // Decode a single block (files + metadata) into the mount at mountOff. Raw blocks copy uncompLen
    // bytes; Kraken blocks decode even (seeded) + odd (seedless, back-referencing even in the same
    // 256K buffer). Unreferenced padding blocks may copy garbage past their real footprint, which is
    // harmless because no inode points into the inter-file padding region.
    // kde 2 = Kraken, stored un-shuffled (our builder). kde 3 = Kraken after a pre-compression
    // shuffle (Windows 0.5 / publisher packs). Other kde values are raw or padding.
    private static void DecodeBlockFromReader(
        IMemoryReader inner, long innerSize, long onDisk,
        int totalComp, int evenComp, int uncompLen, bool kraken,
        byte kde, byte shuffleIdx, byte[] dest, int destOff)
    {
        int room = dest.Length - destOff;
        if (room <= 0)
            return;
        int copyLen = Math.Min(uncompLen, room);

        if (!kraken)
        {
            if (onDisk >= 0 && onDisk < innerSize)
            {
                int avail = (int)Math.Min(copyLen, innerSize - onDisk);
                if (avail > 0)
                    inner.Read(onDisk, dest, destOff, avail);
            }
            return;
        }

        if (onDisk < 0 || totalComp <= 0 || onDisk >= innerSize)
            return;
        int srcLen = (int)Math.Min(totalComp, innerSize - onDisk);
        var src = new byte[Math.Max(totalComp, 1)];
        if (srcLen > 0)
            inner.Read(onDisk, src, 0, srcLen);
        DecodeBlockInto(src, 0, totalComp, evenComp, uncompLen, kraken, kde, shuffleIdx, dest, destOff);
    }

    private static void DecodeBlockInto(byte[] inner, long onDisk, int totalComp, int evenComp,
        int uncompLen, bool kraken, byte kde, byte shuffleIdx, byte[] mount, int mountOff)
    {
        int room = mount.Length - mountOff;
        if (room <= 0)
            return;
        int copyLen = Math.Min(uncompLen, room);

        if (!kraken)
        {
            if (onDisk >= 0 && onDisk < inner.Length)
            {
                int avail = (int)Math.Min(copyLen, inner.Length - onDisk);
                Array.Copy(inner, onDisk, mount, mountOff, avail);
            }
            return;
        }

        if (onDisk < 0 || onDisk + totalComp > inner.Length || totalComp <= 0)
            return;

        var src = inner.AsSpan((int)onDisk, totalComp);
        int firstChunkComp = uncompLen > Chunk128K ? evenComp : 0;
        var decoded = new byte[uncompLen];

        foreach (int flags in FlagCandidates(uncompLen > Chunk128K))
        {
            KrakenDecodeStatus st;
            try { st = KrakenDecoder.DecodeBlock(src, flags, firstChunkComp, decoded); }
            catch { continue; }
            if (st != KrakenDecodeStatus.Success)
                continue;

            ReadOnlySpan<byte> plain = decoded;
            if (kde == 3 && shuffleIdx is >= 1 and <= 12)
            {
                try
                {
                    plain = ProsperoPfsShuffle.Deshuffle(
                        decoded.AsSpan(0, copyLen), (ProsperoPfsShufflePattern)shuffleIdx);
                }
                catch
                {
                    plain = decoded;
                }
            }

            plain[..copyLen].CopyTo(mount.AsSpan(mountOff, copyLen));
            return;
        }
    }

    private static int[] FlagCandidates(bool multiChunk) => multiChunk
        ? [0x22, 0x02, 0x12, 0x32, 0x23, 0x03, 0x13, 0x33, 0x00, 0x20]
        : [0x02, 0x00, 0x03, 0x01];
}
