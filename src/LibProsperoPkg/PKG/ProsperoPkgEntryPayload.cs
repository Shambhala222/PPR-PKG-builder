// LibProsperoPkg - A library for building and inspecting PS5 packages.
// Copyright (C) 2026 SvenGDK
//
// Reads a CNT entry payload (optionally decrypting it) and decides which named
// entries belong back in sce_sys after an inner-image extract. The packer lifts
// param.json, icons, trophies and similar files into the outer CNT; unpack must
// put them back or ShadowMountPlus / folder dumps look empty.

#nullable enable
using LibProsperoPkg.Util;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace LibProsperoPkg.PKG;

public static class ProsperoPkgEntryPayload
{
    public static string? ResolveName(ProsperoPkgEntry entry)
    {
        if (!string.IsNullOrEmpty(entry.Name))
            return entry.Name;
        if (entry.RawId == 0x2000)
            return "param.json";
        return ProsperoCntEntryNames.IdToName.TryGetValue((ProsperoCntEntryId)entry.RawId, out string? mapped)
            ? mapped
            : null;
    }

    public static bool ShouldRestoreToSceSys(ProsperoPkgEntry entry)
    {
        string? name = ResolveName(entry);
        if (string.IsNullOrEmpty(name) || name.StartsWith('.'))
            return false;
        if (name.Equals("license.dat", StringComparison.OrdinalIgnoreCase)
            || name.Equals("license.info", StringComparison.OrdinalIgnoreCase))
            return false;
        if (name.Equals("playgo-chunk.dat", StringComparison.OrdinalIgnoreCase)
            || name.Equals("playgo-chunk.sha", StringComparison.OrdinalIgnoreCase)
            || name.Equals("playgo-hash-table.dat", StringComparison.OrdinalIgnoreCase)
            || name.Equals("playgo-ficm.dat", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    public static byte[]? TryRead(
        Stream package, ProsperoPkg pkg, ProsperoPkgEntry entry, string? passcode)
    {
        if (entry.DataSize == 0 || entry.DataSize > 64u * 1024 * 1024)
            return null;

        long cntBase = pkg.Fih is null ? 0 : checked((long)pkg.Fih.EmbeddedCntOffset);
        long position = checked(cntBase + entry.DataOffset);
        int onDisk = entry.Encrypted
            ? (int)((entry.DataSize + 15u) & ~15u)
            : (int)entry.DataSize;
        if (position < 0 || position + onDisk > package.Length)
            onDisk = (int)entry.DataSize;
        if (position < 0 || position + onDisk > package.Length)
            return null;

        byte[] payload = new byte[onDisk];
        package.Position = position;
        int read = 0;
        while (read < payload.Length)
        {
            int n = package.Read(payload, read, payload.Length - read);
            if (n <= 0)
                return null;
            read += n;
        }

        if (!entry.Encrypted)
            return payload.Length == entry.DataSize ? payload : payload.AsSpan(0, (int)entry.DataSize).ToArray();

        string contentId = pkg.Header?.ContentId ?? "";
        if (contentId.Length != 36)
            return null;

        var passcodes = new List<string>();
        if (!string.IsNullOrEmpty(passcode) && passcode.Length == 32)
            passcodes.Add(passcode);
        string zeros = new string('0', 32);
        if (!passcodes.Contains(zeros))
            passcodes.Add(zeros);

        string? name = ResolveName(entry);
        foreach (string pc in passcodes)
        {
            foreach (bool sha3 in new[] { true, false })
            {
                try
                {
                    byte[] dec = Decrypt(payload, entry, contentId, pc, sha3);
                    if (dec.Length > entry.DataSize)
                        dec = dec.AsSpan(0, (int)entry.DataSize).ToArray();
                    if (LooksPlausible(name, dec))
                        return dec;
                }
                catch
                {
                    // try the next schedule
                }
            }
        }

        return null;
    }

    private static byte[] Decrypt(
        byte[] payload, ProsperoPkgEntry entry, string contentId, string passcode, bool sha3)
    {
        byte[] meta = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(meta.AsSpan(0), entry.RawId);
        BinaryPrimitives.WriteUInt32BigEndian(meta.AsSpan(4), entry.NameTableOffset);
        BinaryPrimitives.WriteUInt32BigEndian(meta.AsSpan(8), entry.Flags1);
        BinaryPrimitives.WriteUInt32BigEndian(meta.AsSpan(12), entry.Flags2);
        BinaryPrimitives.WriteUInt32BigEndian(meta.AsSpan(16), entry.DataOffset);
        BinaryPrimitives.WriteUInt32BigEndian(meta.AsSpan(20), entry.DataSize);

        byte[] keys = Crypto.ComputeKeys(contentId, passcode, entry.KeyIndex, sha3);
        byte[] mixed = meta.Concat(keys).ToArray();
        byte[] ivKey = sha3 ? Crypto.Sha3_256(mixed) : Crypto.Sha256(mixed);
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = ivKey.AsSpan(16, 16).ToArray();
        aes.IV = ivKey.AsSpan(0, 16).ToArray();
        using ICryptoTransform decrypt = aes.CreateDecryptor();
        int block = payload.Length & ~15;
        if (block <= 0)
            throw new InvalidDataException("CNT entry is too short to decrypt.");
        return decrypt.TransformFinalBlock(payload, 0, block);
    }

    private static bool LooksPlausible(string? name, byte[] data)
    {
        if (data.Length == 0)
            return false;
        if (name is not null && name.EndsWith("nptitle.dat", StringComparison.OrdinalIgnoreCase))
            return data.Length >= 4 && data[0] == (byte)'N' && data[1] == (byte)'P'
                && data[2] == (byte)'T' && data[3] == (byte)'D';
        if (name is not null && name.EndsWith("npbind.dat", StringComparison.OrdinalIgnoreCase))
            return data.Length >= 16
                && (BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(12)) == data.Length
                    || BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12)) == data.Length);
        return true;
    }
}
