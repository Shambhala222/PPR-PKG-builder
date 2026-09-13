using LibProsperoPkg.Metadata;
using LibProsperoPkg.PKG;
using LibProsperoPkg.Util;
using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace LibProsperoPkg.Gui.Services;

internal static class PackageEntryReader
{
    private const int MaxPreviewBytes = 8 * 1024 * 1024;

    public static byte[]? TryRead(string packagePath, string entryName, string passcode)
    {
        if (!File.Exists(packagePath))
            return null;

        ProsperoPkg pkg = ProsperoPkgReader.Read(packagePath);
        if (pkg.Entries is null || pkg.Entries.Count == 0)
            return null;

        ProsperoPkgEntry? entry = pkg.Entries.FirstOrDefault(e =>
            string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase));
        if (entry is null || entry.DataSize == 0 || entry.DataSize > MaxPreviewBytes)
            return null;

        long cntBase = pkg.Fih is null ? 0 : checked((long)pkg.Fih.EmbeddedCntOffset);
        long position = checked(cntBase + entry.DataOffset);
        byte[] payload = new byte[entry.DataSize];
        using (var stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (position < 0 || position + payload.Length > stream.Length)
                return null;
            stream.Position = position;
            stream.ReadExactly(payload);
        }

        if (entry.Encrypted)
        {
            string contentId = pkg.Header?.ContentId ?? "";
            if (contentId.Length != 36 || passcode.Length != 32)
                return LooksLikePreview(payload, entryName) ? payload : null;
            try
            {
                payload = DecryptCnt(payload, entry, contentId, passcode);
            }
            catch
            {
                return null;
            }
        }

        return LooksLikePreview(payload, entryName) ? payload : null;
    }

    public static ProsperoParam? TryReadParam(string packagePath, string passcode)
    {
        byte[]? bytes = TryRead(packagePath, "param.json", passcode);
        if (bytes is null)
            return null;
        try
        {
            return ProsperoParam.Parse(System.Text.Encoding.UTF8.GetString(bytes));
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksLikePreview(byte[] payload, string entryName)
    {
        if (payload.Length < 4)
            return false;
        if (entryName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return payload[0] == 0x89 && payload[1] == 0x50 && payload[2] == 0x4E && payload[3] == 0x47;
        if (entryName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            int i = 0;
            while (i < payload.Length && payload[i] <= 32)
                i++;
            return i < payload.Length && payload[i] == (byte)'{';
        }
        return true;
    }

    private static byte[] DecryptCnt(byte[] payload, ProsperoPkgEntry entry, string contentId, string passcode)
    {
        byte[] meta = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(meta.AsSpan(0), entry.RawId);
        BinaryPrimitives.WriteUInt32BigEndian(meta.AsSpan(4), entry.NameTableOffset);
        BinaryPrimitives.WriteUInt32BigEndian(meta.AsSpan(8), entry.Flags1);
        BinaryPrimitives.WriteUInt32BigEndian(meta.AsSpan(12), entry.Flags2);
        BinaryPrimitives.WriteUInt32BigEndian(meta.AsSpan(16), entry.DataOffset);
        BinaryPrimitives.WriteUInt32BigEndian(meta.AsSpan(20), entry.DataSize);

        byte[] ivKey = Crypto.Sha256(meta.Concat(Crypto.ComputeKeys(contentId, passcode, entry.KeyIndex)).ToArray());
        byte[] iv = ivKey.AsSpan(0, 16).ToArray();
        byte[] key = ivKey.AsSpan(16, 16).ToArray();
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        aes.IV = iv;
        using ICryptoTransform decrypt = aes.CreateDecryptor();
        return decrypt.TransformFinalBlock(payload, 0, payload.Length);
    }
}
