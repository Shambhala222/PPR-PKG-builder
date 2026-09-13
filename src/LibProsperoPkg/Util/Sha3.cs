// LibProsperoPkg - A library for building and inspecting PS5 packages.
// Copyright (C) 2026 SvenGDK
//
// SHA3-256, the digest primitive used by PS5 PFS/CNT (EKPFS, block hashes, package digest).
// System.Security.Cryptography.SHA3_256 is used when the OS provides it (Windows CNG, Linux
// OpenSSL 3). Apple's CommonCrypto does not expose SHA-3, so macOS uses the FIPS 202
// Keccak-f[1600] implementation below. Both paths produce the same digest.

#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;

namespace LibProsperoPkg.Util;

/// <summary>
/// SHA3-256 for PS5 package digests. Always available: OS SHA-3 when present, otherwise a
/// managed Keccak-f[1600] implementation (FIPS 202).
/// </summary>
public static class ProsperoSha3
{
    /// <summary>Length of a SHA3-256 digest, in bytes.</summary>
    public const int DigestSize = 32;

    /// <summary>Always <see langword="true"/>; a managed implementation covers hosts without OS SHA-3.</summary>
    public static bool IsSupported => true;

    /// <summary>SHA3-256 of <paramref name="data"/>.</summary>
    public static byte[] HashData(ReadOnlySpan<byte> data)
    {
        byte[] digest = new byte[DigestSize];
        HashData(data, digest);
        return digest;
    }

    /// <summary>SHA3-256 of <paramref name="data"/> into <paramref name="destination"/> (32 bytes).</summary>
    public static int HashData(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        if (destination.Length < DigestSize)
            throw new ArgumentException("Destination must be at least 32 bytes.", nameof(destination));

        if (SHA3_256.IsSupported)
            return SHA3_256.HashData(data, destination);

        Sha3_256Core.Hash(data, destination[..DigestSize]);
        return DigestSize;
    }

    /// <summary>SHA3-256 of the remainder of <paramref name="stream"/> from its current position.</summary>
    public static byte[] HashData(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (SHA3_256.IsSupported)
            return SHA3_256.HashData(stream);

        var hasher = new Hasher();
        Span<byte> buffer = stackalloc byte[8192];
        while (true)
        {
            int n = stream.Read(buffer);
            if (n <= 0)
                break;
            hasher.Append(buffer[..n]);
        }
        return hasher.Finish();
    }

    /// <summary>Incremental SHA3-256. Used when a digest is assembled from several slices.</summary>
    public sealed class Hasher
    {
        private readonly IncrementalHash? _os;
        private readonly Sha3_256Core? _managed;

        public Hasher()
        {
            if (SHA3_256.IsSupported)
                _os = IncrementalHash.CreateHash(HashAlgorithmName.SHA3_256);
            else
                _managed = new Sha3_256Core();
        }

        public void Append(ReadOnlySpan<byte> data)
        {
            if (_os is not null)
                _os.AppendData(data);
            else
                _managed!.Absorb(data);
        }

        public byte[] Finish()
        {
            if (_os is not null)
                return _os.GetHashAndReset();
            return _managed!.Squeeze();
        }
    }
}

/// <summary>FIPS 202 SHA3-256 (Keccak[512] with domain-separation pad 0x06).</summary>
internal sealed class Sha3_256Core
{
    private const int RateBytes = 136; // 1088-bit rate
    private readonly ulong[] _state = new ulong[25];
    private readonly byte[] _queue = new byte[RateBytes];
    private int _queueLength;

    public static void Hash(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        var core = new Sha3_256Core();
        core.Absorb(data);
        core.Squeeze().CopyTo(destination);
    }

    public void Absorb(ReadOnlySpan<byte> data)
    {
        while (data.Length > 0)
        {
            int take = Math.Min(RateBytes - _queueLength, data.Length);
            data[..take].CopyTo(_queue.AsSpan(_queueLength));
            _queueLength += take;
            data = data[take..];
            if (_queueLength == RateBytes)
            {
                XorRate(_queue);
                KeccakF(_state);
                _queueLength = 0;
            }
        }
    }

    public byte[] Squeeze()
    {
        // SHA3 padding: 0x06 then 10*1 (0x80 in the last rate byte).
        _queue[_queueLength] = 0x06;
        for (int i = _queueLength + 1; i < RateBytes; i++)
            _queue[i] = 0;
        _queue[RateBytes - 1] |= 0x80;
        XorRate(_queue);
        KeccakF(_state);

        byte[] digest = new byte[32];
        for (int i = 0; i < 4; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(digest.AsSpan(i * 8), _state[i]);

        Array.Clear(_state);
        Array.Clear(_queue);
        _queueLength = 0;
        return digest;
    }

    private void XorRate(ReadOnlySpan<byte> rate)
    {
        for (int i = 0; i < RateBytes / 8; i++)
            _state[i] ^= System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(rate.Slice(i * 8, 8));
    }

    private static readonly ulong[] RoundConstants =
    [
        0x0000000000000001UL, 0x0000000000008082UL, 0x800000000000808aUL, 0x8000000080008000UL,
        0x000000000000808bUL, 0x0000000080000001UL, 0x8000000080008081UL, 0x8000000000008009UL,
        0x000000000000008aUL, 0x0000000000000088UL, 0x0000000080008009UL, 0x000000008000000aUL,
        0x000000008000808bUL, 0x800000000000008bUL, 0x8000000000008089UL, 0x8000000000008003UL,
        0x8000000000008002UL, 0x8000000000000080UL, 0x000000000000800aUL, 0x800000008000000aUL,
        0x8000000080008081UL, 0x8000000000008080UL, 0x0000000080000001UL, 0x8000000080008008UL,
    ];

    private static readonly int[] RhoPiLane =
    [
        10, 7, 11, 17, 18, 3, 5, 16, 8, 21, 24, 4, 15, 23, 19, 13, 12, 2, 20, 14, 22, 9, 6, 1,
    ];

    private static readonly int[] RhoPiRot =
    [
        1, 3, 6, 10, 15, 21, 28, 36, 45, 55, 2, 14, 27, 41, 56, 8, 25, 43, 62, 18, 39, 61, 20, 44,
    ];

    private static ulong Rotl64(ulong x, int n) => (x << n) | (x >> (64 - n));

    private static void KeccakF(ulong[] a)
    {
        ulong t, t0, t1, t2, t3, t4;
        for (int round = 0; round < 24; round++)
        {
            // Theta
            t0 = a[0] ^ a[5] ^ a[10] ^ a[15] ^ a[20];
            t1 = a[1] ^ a[6] ^ a[11] ^ a[16] ^ a[21];
            t2 = a[2] ^ a[7] ^ a[12] ^ a[17] ^ a[22];
            t3 = a[3] ^ a[8] ^ a[13] ^ a[18] ^ a[23];
            t4 = a[4] ^ a[9] ^ a[14] ^ a[19] ^ a[24];
            ulong d0 = t4 ^ Rotl64(t1, 1);
            ulong d1 = t0 ^ Rotl64(t2, 1);
            ulong d2 = t1 ^ Rotl64(t3, 1);
            ulong d3 = t2 ^ Rotl64(t4, 1);
            ulong d4 = t3 ^ Rotl64(t0, 1);
            a[0] ^= d0; a[5] ^= d0; a[10] ^= d0; a[15] ^= d0; a[20] ^= d0;
            a[1] ^= d1; a[6] ^= d1; a[11] ^= d1; a[16] ^= d1; a[21] ^= d1;
            a[2] ^= d2; a[7] ^= d2; a[12] ^= d2; a[17] ^= d2; a[22] ^= d2;
            a[3] ^= d3; a[8] ^= d3; a[13] ^= d3; a[18] ^= d3; a[23] ^= d3;
            a[4] ^= d4; a[9] ^= d4; a[14] ^= d4; a[19] ^= d4; a[24] ^= d4;

            // Rho and Pi
            t = a[1];
            for (int i = 0; i < 24; i++)
            {
                int j = RhoPiLane[i];
                ulong tmp = a[j];
                a[j] = Rotl64(t, RhoPiRot[i]);
                t = tmp;
            }

            // Chi
            for (int y = 0; y < 25; y += 5)
            {
                t0 = a[y]; t1 = a[y + 1]; t2 = a[y + 2]; t3 = a[y + 3]; t4 = a[y + 4];
                a[y] = t0 ^ (~t1 & t2);
                a[y + 1] = t1 ^ (~t2 & t3);
                a[y + 2] = t2 ^ (~t3 & t4);
                a[y + 3] = t3 ^ (~t4 & t0);
                a[y + 4] = t4 ^ (~t0 & t1);
            }

            a[0] ^= RoundConstants[round];
        }
    }
}
