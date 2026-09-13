// Inverse of the Windows 0.5 / publisher NAPS span graph: each normal CblockInfo is a
// (stored-offset, compressed-length, logical-offset, uncompressed-length) window. Stored
// offset is tweakIndex*64K + (compressedOffset & 0xFFFF), not a linear on-disk cursor.
// CblockInfo bitfields match that publisher layout (they differ from this library's writer).
#nullable enable
using LibProsperoPkg.PFS.Compression.Oodle;
using LibProsperoPkg.PKG;
using LibProsperoPkg.Util;
using System;
using System.Collections.Generic;
using System.IO;

namespace LibProsperoPkg.PFS.Compression;

public readonly record struct ProsperoNapsLogicalFile(
    int Index, byte Type, long UncompressedOffset, long Length);

public readonly record struct ProsperoNapsSpan(
    int Index,
    int CblockInfoIndex,
    long CompressedOffset,
    int CompressedLength,
    int FirstChunkCompressedLength,
    long StoredOffset,
    long UncompressedOffset,
    int UncompressedLength,
    uint TweakIndex,
    byte KeyTableIndex,
    byte Even,
    byte Odd,
    byte KdePredictor,
    byte ShuffleIndex);

public sealed class ProsperoNapsPlan
{
    public required IReadOnlyList<ProsperoNapsSpan> Spans { get; init; }
    public required IReadOnlyList<ProsperoNapsLogicalFile> Files { get; init; }
    public required long UncompressedSize { get; init; }
}

public static class ProsperoNapsLogicalImage
{
    public const int UBlockSize = 262144;

    public static bool TryBuildPlan(NapsLayoutDocument layout, out ProsperoNapsPlan plan)
    {
        try
        {
            plan = BuildPlan(layout);
            return true;
        }
        catch
        {
            plan = null!;
            return false;
        }
    }

    public static ProsperoNapsPlan BuildPlan(NapsLayoutDocument layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.FileOffsets.Count < 1)
            throw new InvalidDataException("NAPS layout has no terminal file boundary.");

        var win = DecodeAll(layout);
        // Publisher layouts pad a typed sentinel after NumFiles; this library's parser may
        // keep that extra fidx. The span graph only uses the NumFiles records.
        int fileCount = layout.FileOffsets.Count;
        if (layout.Counts.NumFiles > 1 && layout.Counts.NumFiles < fileCount)
            fileCount = layout.Counts.NumFiles;
        var files = new List<ProsperoNapsLogicalFile>(fileCount - 1);
        for (int i = 0; i + 1 < fileCount; i++)
        {
            NapsFileOffsetEntry a = layout.FileOffsets[i];
            NapsFileOffsetEntry b = layout.FileOffsets[i + 1];
            if (b.UncompressedOffsetStart < a.UncompressedOffsetStart)
                throw new InvalidDataException($"NAPS file boundary {i + 1} moves backwards.");
            files.Add(new ProsperoNapsLogicalFile(
                i, a.Type, (long)a.UncompressedOffsetStart,
                (long)(b.UncompressedOffsetStart - a.UncompressedOffsetStart)));
        }

        var spans = new List<ProsperoNapsSpan>();
        ProsperoNapsSpan? prev = null;
        foreach (ProsperoNapsLogicalFile file in files)
        {
            if ((file.Type & 0x40) != 0)
            {
                if (!prev.HasValue)
                    throw new InvalidDataException($"NAPS continuation file {file.Index} has no preceding span.");
                continue;
            }

            long logical = file.UncompressedOffset;
            long remain = file.Length;
            int cbi = ResolveCblockInfoIndex(layout, win, logical);
            while (remain != 0)
            {
                if ((uint)cbi >= (uint)win.Length)
                    throw new InvalidDataException($"NAPS file {file.Index} runs past CblockInfo.");
                Win02Cblock cur = win[cbi];
                if (cur.IsRunBase)
                {
                    cbi++;
                    continue;
                }
                if (cur.IsTerminal)
                    throw new InvalidDataException($"NAPS file {file.Index} reaches the terminal boundary early.");

                int nextIdx = cbi + 1;
                if ((uint)nextIdx >= (uint)win.Length)
                    throw new InvalidDataException("NAPS normal CblockInfo has no following boundary.");
                Win02Cblock next = win[nextIdx];
                int afterRun = next.IsRunBase ? nextIdx + 1 : nextIdx;
                if ((uint)afterRun >= (uint)win.Length)
                    throw new InvalidDataException("NAPS run-base has no following logical boundary.");
                if (win[afterRun].IsRunBase)
                    throw new InvalidDataException($"NAPS contains adjacent RUN records at CblockInfo {nextIdx}/{afterRun}.");

                Win02Cblock after = win[afterRun];
                int compLen = Delta18(next.IsRunBase ? next.CoffsetEndMod256K : next.CoffsetStartMod256K, cur.CoffsetStartMod256K);
                int uncompLen = Delta18(after.UoffsetStart, cur.UoffsetStart);
                if (compLen <= 0 || uncompLen <= 0 || uncompLen > UBlockSize)
                    throw new InvalidDataException($"NAPS span at CblockInfo {cbi} has invalid lengths.");
                if (uncompLen > remain)
                    throw new InvalidDataException($"NAPS span at CblockInfo {cbi} crosses file {file.Index}.");

                long coff;
                uint tweak;
                byte key;
                if (cbi > 0 && win[cbi - 1].IsRunBase)
                {
                    Win02Cblock run = win[cbi - 1];
                    coff = ((long)run.CoffsetStart256K << 18) | cur.CoffsetStartMod256K;
                    tweak = run.TweakIdxStart;
                    key = run.KeyTableIdx;
                }
                else
                {
                    if (!prev.HasValue)
                        throw new InvalidDataException("The first NAPS span is not preceded by a run-base.");
                    ProsperoNapsSpan last = prev.GetValueOrDefault();
                    coff = last.CompressedOffset + last.CompressedLength;
                    long tweakDelta = (coff >> 16) - (last.CompressedOffset >> 16);
                    tweak = (uint)(last.TweakIndex + tweakDelta);
                    key = last.KeyTableIndex;
                }

                long stored = (long)tweak * 65536L + (coff & 0xFFFF);
                var span = new ProsperoNapsSpan(
                    spans.Count, cbi, coff, compLen, (int)cur.ClenEvenMinus1 + 1, stored,
                    logical, uncompLen, tweak, key, cur.Even, cur.Odd, cur.KdePredictor, cur.ShuffleIdx);
                spans.Add(span);
                prev = span;
                logical += uncompLen;
                remain -= uncompLen;
                cbi++;
            }
        }

        int expected = 0;
        foreach (Win02Cblock e in win)
        {
            if (!e.IsRunBase && !e.IsTerminal)
                expected++;
        }
        if (spans.Count != expected)
            throw new InvalidDataException($"NAPS span graph used {spans.Count} of {expected} normal entries.");

        return new ProsperoNapsPlan
        {
            Spans = spans,
            Files = files,
            UncompressedSize = (long)layout.FileOffsets[fileCount - 1].UncompressedOffsetStart,
        };
    }

    public static byte[] DecompressRange(IMemoryReader inner, long innerSize, NapsLayoutDocument layout, ProsperoNapsPlan plan, long offset, int length)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length));
        long end = offset + length;
        if (end > plan.UncompressedSize)
            throw new ArgumentOutOfRangeException(nameof(length), "Requested NAPS range exceeds the logical image.");
        if (length == 0)
            return [];

        var dest = new byte[length];
        int filled = 0;
        int first = LowerBound(plan.Spans, offset);
        for (int i = first; i < plan.Spans.Count; i++)
        {
            ProsperoNapsSpan span = plan.Spans[i];
            if (span.UncompressedOffset >= end)
                break;
            long spanEnd = span.UncompressedOffset + span.UncompressedLength;
            long from = Math.Max(offset, span.UncompressedOffset);
            long to = Math.Min(end, spanEnd);
            if (from >= to)
                continue;
            byte[] decoded = DecodeSpan(inner, innerSize, layout, span);
            int src = (int)(from - span.UncompressedOffset);
            int dst = (int)(from - offset);
            int n = (int)(to - from);
            decoded.AsSpan(src, n).CopyTo(dest.AsSpan(dst, n));
            filled += n;
        }
        if (filled != length)
            throw new InvalidDataException($"NAPS range [0x{offset:x},+0x{length:x}) has only 0x{filled:x} decoded bytes.");
        return dest;
    }

    public static byte[] DecodeSpan(IMemoryReader inner, long innerSize, NapsLayoutDocument layout, ProsperoNapsSpan span)
    {
        if (span.KdePredictor != 0)
            throw new NotSupportedException($"NAPS span {span.Index} requires KDE predictor {span.KdePredictor}.");

        ProsperoPfsShufflePattern shuffle = ResolveShufflePattern(layout, span.ShuffleIndex);
        if (span.StoredOffset < 0 || span.StoredOffset > innerSize || span.CompressedLength > innerSize - span.StoredOffset)
            throw new InvalidDataException($"NAPS span {span.Index} lies outside pfs_image.dat.");

        var output = new byte[span.UncompressedLength];
        if (IsDeduplicatedZeroSpan(span))
            return output;

        var payload = new byte[span.CompressedLength];
        if (span.CompressedLength > 0)
            inner.Read(span.StoredOffset, payload, 0, span.CompressedLength);

        if (span.CompressedLength == span.UncompressedLength)
            payload.CopyTo(output, 0);
        else
        {
            int firstChunk = span.UncompressedLength > 131072 ? span.FirstChunkCompressedLength : 0;
            int flags = ((span.Even & 4) != 0 ? 2 : 0)
                | ((span.Even & 2) != 0 ? 1 : 0)
                | ((span.Odd & 4) != 0 ? 32 : 0)
                | ((span.Odd & 2) != 0 ? 16 : 0);
            KrakenDecodeStatus status = KrakenDecoder.DecodeBlock(payload, flags, firstChunk, output);
            if (status != KrakenDecodeStatus.Success)
                throw new InvalidDataException(
                    $"NAPS span {span.Index} Kraken decode failed ({status}).");
        }

        if (shuffle != ProsperoPfsShufflePattern.None)
        {
            byte[] plain = ProsperoPfsShuffle.Deshuffle(output, shuffle);
            plain.CopyTo(output, 0);
        }
        return output;
    }

    public static ProsperoNapsLogicalFile? LastMetadataFile(ProsperoNapsPlan plan)
    {
        for (int i = plan.Files.Count - 1; i >= 0; i--)
        {
            ProsperoNapsLogicalFile f = plan.Files[i];
            if ((f.Type & 0x40) != 0 || f.Length <= 0 || f.Length > 512L * 1024 * 1024)
                continue;
            return f;
        }
        return null;
    }

    private static int LowerBound(IReadOnlyList<ProsperoNapsSpan> spans, long offset)
    {
        int lo = 0, hi = spans.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) / 2);
            ProsperoNapsSpan s = spans[mid];
            if (s.UncompressedOffset + s.UncompressedLength <= offset)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    private static bool IsDeduplicatedZeroSpan(ProsperoNapsSpan span)
    {
        bool longZero = span.Odd == 1 && span.CompressedLength == 16 && span.UncompressedLength > 16;
        bool shortZero = span.Odd == 0 && span.CompressedLength == 8 && span.UncompressedLength > 8 && span.UncompressedLength <= 131072;
        return (longZero || shortZero)
            && span.FirstChunkCompressedLength == 8
            && span.Even == 1
            && span.KdePredictor == 0
            && span.ShuffleIndex == 0;
    }

    private static ProsperoPfsShufflePattern ResolveShufflePattern(NapsLayoutDocument layout, byte shuffleIndex)
    {
        int count = layout.ShufflePatterns.Count;
        if (shuffleIndex == count)
            return ProsperoPfsShufflePattern.None;
        if (shuffleIndex > count)
            throw new InvalidDataException($"NAPS shuffle index {shuffleIndex} exceeds the pattern table.");
        ReadOnlySpan<byte> bytes = layout.ShufflePatterns[shuffleIndex];
        if (bytes.Length != 8)
            throw new InvalidDataException($"NAPS shuffle pattern {shuffleIndex} has invalid size {bytes.Length}.");
        int len = bytes.Length;
        while (len > 0 && bytes[len - 1] == 0)
            len--;
        for (var pattern = ProsperoPfsShufflePattern.Shuffle44; pattern <= ProsperoPfsShufflePattern.Shuffle2626; pattern++)
        {
            int[] fields = ProsperoPfsShuffle.Describe(pattern).Fields;
            if (fields.Length != len)
                continue;
            bool match = true;
            for (int i = 0; i < fields.Length; i++)
                match &= bytes[i] == fields[i];
            if (match)
                return pattern;
        }
        throw new NotSupportedException($"NAPS shuffle pattern {shuffleIndex} is unknown.");
    }

    private static int ResolveCblockInfoIndex(NapsLayoutDocument layout, Win02Cblock[] win, long uncompressedOffset)
    {
        if (uncompressedOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(uncompressedOffset));
        int ublock = (int)(uncompressedOffset >> 18);
        uint start = ResolveU2c(layout, ublock);
        int ublockCount = layout.Counts.NumUBlocks + 1;
        uint end = ublock + 1 < ublockCount ? ResolveU2c(layout, ublock + 1) : (uint)win.Length;
        long baseOff = (long)ublock << 18;
        for (uint i = start; i < end && i < win.Length; i++)
        {
            Win02Cblock e = win[i];
            if (!e.IsRunBase && !e.IsTerminal && uncompressedOffset == baseOff + e.UoffsetStart)
                return (int)i;
        }
        throw new InvalidDataException($"No NAPS CblockInfo starts at logical offset 0x{uncompressedOffset:x}.");
    }

    private static uint ResolveU2c(NapsLayoutDocument layout, int ublock)
    {
        int ublockCount = layout.Counts.NumUBlocks + 1;
        if (ublock < 0 || ublock >= ublockCount)
            throw new InvalidDataException($"NAPS ublock {ublock} is outside the u2c table.");
        NapsU2cEntry group = layout.CblockInfoOffsetByUblock[ublock >> 3];
        if ((ublock & 7) != 0)
            return group.InfoOffset9BBase + group.DeltaFromBase[(ublock & 7) - 1];
        return group.InfoOffset9BBase;
    }

    private static int Delta18(uint next, uint previous)
    {
        int d = (int)next - (int)previous;
        if (d <= 0)
            d += UBlockSize;
        return d;
    }

    private static Win02Cblock[] DecodeAll(NapsLayoutDocument layout)
    {
        var win = new Win02Cblock[layout.CblockInfos.Count];
        for (int i = 0; i < layout.CblockInfos.Count; i++)
        {
            byte[]? raw = layout.CblockInfos[i].Raw;
            if (raw is null || raw.Length < 9)
                throw new InvalidDataException($"CblockInfo {i} has no raw bytes.");
            win[i] = DecodeCblock(raw);
        }
        return win;
    }

    private readonly record struct Win02Cblock(
        bool IsRunBase,
        bool IsTerminal,
        uint CoffsetStartMod256K,
        uint UoffsetStart,
        uint ClenEvenMinus1,
        byte Even,
        byte Odd,
        byte KdePredictor,
        byte ShuffleIdx,
        uint CoffsetEndMod256K,
        uint TweakIdxStart,
        byte KeyTableIdx,
        uint CoffsetStart256K);

    private static Win02Cblock DecodeCblock(ReadOnlySpan<byte> raw)
    {
        ulong lo = 0;
        for (int i = 0; i < 8; i++)
            lo |= (ulong)raw[i] << (8 * i);
        UInt128 bits = lo | ((UInt128)raw[8] << 64);
        bool isRun = ((lo >> 18) & 1) != 0;
        uint mod = (uint)(lo & 0x3FFFF);
        if (!isRun)
        {
            bool reserved19 = ((bits >> 19) & 1) != 0;
            return new Win02Cblock(
                false, reserved19, mod,
                (uint)((bits >> 20) & 262143u),
                (uint)((bits >> 38) & 131071u),
                (byte)((bits >> 55) & 7),
                (byte)((bits >> 58) & 7),
                (byte)((bits >> 61) & 63),
                (byte)((bits >> 68) & 15),
                0, 0, 0, 0);
        }
        return new Win02Cblock(
            true, false, 0, 0, 0, 0, 0, 0, 0,
            mod,
            (uint)((bits >> 20) & 268435455u),
            (byte)((bits >> 48) & 3),
            (uint)((bits >> 50) & 4194303u));
    }
}
