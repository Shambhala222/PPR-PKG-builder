// LibProsperoPkg - A library for building and inspecting PS5 packages.
// Copyright (C) 2026 SvenGDK
//
// On-demand AES-XTS reader for a PS5 nwonly "data-first" outer PFS. Decrypts one 64K block
// at a time so a 100+ GiB finalized image never has to sit in RAM.
#nullable enable
using LibProsperoPkg.Util;
using System;
using System.IO;

namespace LibProsperoPkg.PFS;

/// <summary>
/// Reads a data-first outer PFS from the package stream, decrypting each 64K block as it is
/// requested. Block kinds may be updated after the probe pass (pfs_image.dat data vs signed
/// metadata); call <see cref="InvalidateCache"/> after changing <see cref="Kinds"/>.
/// </summary>
internal sealed class ProsperoDataFirstOuterReader : IMemoryReader
{
    public const int BlockSize = ProsperoOuterPfsImage.DefaultBlockSize;

    private readonly Stream _stream;
    private readonly long _imageOffset;
    private readonly long _imageSize;
    private readonly XtsBlockTransform _xts;
    private readonly byte[] _block = new byte[BlockSize];
    private readonly object _gate = new();
    private int _cachedBlock = -1;

    public ProsperoDataFirstOuterReader(
        Stream stream, long imageOffset, long imageSize,
        byte[] tweakKey, byte[] dataKey, ProsperoOuterBlockKind[] kinds)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(tweakKey);
        ArgumentNullException.ThrowIfNull(dataKey);
        ArgumentNullException.ThrowIfNull(kinds);
        if (imageOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(imageOffset));
        if (imageSize <= 0 || (imageSize % BlockSize) != 0)
            throw new ArgumentOutOfRangeException(nameof(imageSize));
        if (kinds.Length != imageSize / BlockSize)
            throw new ArgumentException("Block-kind count must equal the outer image block count.", nameof(kinds));

        _stream = stream;
        _imageOffset = imageOffset;
        _imageSize = imageSize;
        Kinds = kinds;
        _xts = new XtsBlockTransform(dataKey, tweakKey);
    }

    public ProsperoOuterBlockKind[] Kinds { get; }

    public void InvalidateCache()
    {
        lock (_gate)
            _cachedBlock = -1;
    }

    public void Read(long pos, byte[] buf, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buf);
        if (count <= 0)
            return;
        if (pos < 0 || pos + count > _imageSize)
            throw new ArgumentOutOfRangeException(nameof(pos), "Read is outside the outer PFS image.");

        lock (_gate)
        {
            while (count > 0)
            {
                int blockIndex = (int)(pos / BlockSize);
                int inner = (int)(pos % BlockSize);
                int n = Math.Min(count, BlockSize - inner);
                EnsureBlock(blockIndex);
                Buffer.BlockCopy(_block, inner, buf, offset, n);
                pos += n;
                offset += n;
                count -= n;
            }
        }
    }

    public void Dispose() => _xts.Dispose();

    private void EnsureBlock(int blockIndex)
    {
        if (_cachedBlock == blockIndex)
            return;

        _stream.Position = _imageOffset + (long)blockIndex * BlockSize;
        _stream.ReadExactly(_block, 0, BlockSize);

        ProsperoOuterBlockKind kind = Kinds[blockIndex];
        if (kind != ProsperoOuterBlockKind.Plaintext)
        {
            ulong sector = ProsperoOuterPfsSignature.BlockSector(
                blockIndex, kind == ProsperoOuterBlockKind.Signed);
            _xts.CryptSector(_block, sector, encrypt: false);
        }

        _cachedBlock = blockIndex;
    }
}
