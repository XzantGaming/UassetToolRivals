using System;
using System.IO;

namespace UAssetTool.IoStore;

/// <summary>
/// IoStore chunk identifier (12 bytes).
/// Reference: retoc-rivals/src/chunk_id.rs
/// </summary>
public struct FIoChunkId
{
    public ulong Id { get; set; }        // 8 bytes - Package ID or chunk-specific ID
    public ushort Index { get; set; }    // 2 bytes - Chunk index within package
    public byte Padding { get; set; }    // 1 byte
    public EIoChunkType ChunkType { get; set; } // 1 byte

    public static FIoChunkId Create(ulong id, ushort index, EIoChunkType chunkType)
    {
        return new FIoChunkId
        {
            Id = id,
            Index = index,
            Padding = 0,
            ChunkType = chunkType
        };
    }

    public static FIoChunkId FromPackageId(FPackageId packageId, ushort index, EIoChunkType chunkType)
    {
        return Create(packageId.Value, index, chunkType);
    }

    public byte[] ToBytes()
    {
        using var ms = new MemoryStream(12);
        using var writer = new BinaryWriter(ms);
        writer.Write(Id);
        writer.Write(Index);
        writer.Write(Padding);
        writer.Write((byte)ChunkType);
        return ms.ToArray();
    }

    public static FIoChunkId FromBytes(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var reader = new BinaryReader(ms);
        return new FIoChunkId
        {
            Id = reader.ReadUInt64(),
            Index = reader.ReadUInt16(),
            Padding = reader.ReadByte(),
            ChunkType = (EIoChunkType)reader.ReadByte()
        };
    }

    public static FIoChunkId Read(BinaryReader reader, EIoStoreTocVersion version)
    {
        return new FIoChunkId
        {
            Id = reader.ReadUInt64(),
            Index = reader.ReadUInt16(),
            Padding = reader.ReadByte(),
            ChunkType = (EIoChunkType)reader.ReadByte()
        };
    }

    public FIoChunkIdRaw ToRaw()
    {
        return new FIoChunkIdRaw(ToBytes());
    }

    public EIoChunkType GetChunkType()
    {
        return ChunkType;
    }

    public override string ToString() => $"FIoChunkId({Id:X16}, {Index}, {ChunkType})";
}

/// <summary>
/// IoStore chunk types.
/// Reference: retoc-rivals/src/lib.rs EIoChunkType
/// </summary>
public enum EIoChunkType : byte
{
    Invalid = 0,
    ExportBundleData = 1,    // Main package data (.uasset/.uexp combined)
    BulkData = 2,            // .ubulk
    OptionalBulkData = 3,    // .uptnl
    MemoryMappedBulkData = 4,// .m.ubulk
    ScriptObjects = 5,       // Global script objects
    ContainerHeader = 6,     // Package metadata
    ExternalFile = 7,
    ShaderCodeLibrary = 8,
    ShaderCode = 9,
    PackageStoreEntry = 10,
    DerivedData = 11,
    EditorDerivedData = 12,
}

/// <summary>
/// Package ID - CityHash64 of lowercase package name.
/// Reference: retoc-rivals/src/lib.rs FPackageId
/// </summary>
public struct FPackageId
{
    public ulong Value { get; set; }

    public FPackageId(ulong value)
    {
        Value = value;
    }

    public static FPackageId FromName(string packageName)
    {
        // CityHash64 of lowercase package name
        return new FPackageId(CityHash.CityHash64(packageName.ToLowerInvariant()));
    }

    public override string ToString() => $"FPackageId({Value:X16})";
}

/// <summary>
/// CityHash64 implementation for package ID generation.
/// </summary>
public static class CityHash
{
    private const ulong k0 = 0xc3a5c85c97cb3127UL;
    private const ulong k1 = 0xb492b66fbe98f273UL;
    private const ulong k2 = 0x9ae16a3b2f90404fUL;

    /// <summary>
    /// CityHash64 with UTF-16LE encoding (matching Rust lower_utf16_cityhash).
    /// </summary>
    public static ulong CityHash64(string s)
    {
        // Rust uses: s.to_ascii_lowercase().encode_utf16().flat_map(|c| c.to_le_bytes())
        // This is UTF-16LE encoding of lowercase string
        byte[] bytes = System.Text.Encoding.Unicode.GetBytes(s.ToLowerInvariant());
        return CityHash64(bytes, 0, bytes.Length);
    }

    public static ulong CityHash64(byte[] s, int pos, int len)
    {
        if (len <= 32)
        {
            if (len <= 16)
            {
                return HashLen0to16(s, pos, len);
            }
            else
            {
                return HashLen17to32(s, pos, len);
            }
        }
        else if (len <= 64)
        {
            return HashLen33to64(s, pos, len);
        }

        ulong x = Fetch64(s, pos + len - 40);
        ulong y = Fetch64(s, pos + len - 16) + Fetch64(s, pos + len - 56);
        ulong z = HashLen16(Fetch64(s, pos + len - 48) + (ulong)len, Fetch64(s, pos + len - 24));
        var v = WeakHashLen32WithSeeds(s, pos + len - 64, (ulong)len, z);
        var w = WeakHashLen32WithSeeds(s, pos + len - 32, y + k1, x);
        x = x * k1 + Fetch64(s, pos);

        len = (len - 1) & ~63;
        do
        {
            x = Rotate(x + y + v.Item1 + Fetch64(s, pos + 8), 37) * k1;
            y = Rotate(y + v.Item2 + Fetch64(s, pos + 48), 42) * k1;
            x ^= w.Item2;
            y += v.Item1 + Fetch64(s, pos + 40);
            z = Rotate(z + w.Item1, 33) * k1;
            v = WeakHashLen32WithSeeds(s, pos, v.Item2 * k1, x + w.Item1);
            w = WeakHashLen32WithSeeds(s, pos + 32, z + w.Item2, y + Fetch64(s, pos + 16));
            (z, x) = (x, z);
            pos += 64;
            len -= 64;
        } while (len != 0);

        return HashLen16(HashLen16(v.Item1, w.Item1) + ShiftMix(y) * k1 + z,
                         HashLen16(v.Item2, w.Item2) + x);
    }

    private static ulong Fetch64(byte[] s, int pos)
    {
        return BitConverter.ToUInt64(s, pos);
    }

    private static uint Fetch32(byte[] s, int pos)
    {
        return BitConverter.ToUInt32(s, pos);
    }

    private static ulong Rotate(ulong val, int shift)
    {
        return shift == 0 ? val : ((val >> shift) | (val << (64 - shift)));
    }

    private static ulong ShiftMix(ulong val)
    {
        return val ^ (val >> 47);
    }

    private static ulong HashLen16(ulong u, ulong v)
    {
        return Hash128to64(u, v);
    }

    private static ulong HashLen16(ulong u, ulong v, ulong mul)
    {
        ulong a = (u ^ v) * mul;
        a ^= (a >> 47);
        ulong b = (v ^ a) * mul;
        b ^= (b >> 47);
        b *= mul;
        return b;
    }

    private static ulong Hash128to64(ulong u, ulong v)
    {
        const ulong kMul = 0x9ddfea08eb382d69UL;
        ulong a = (u ^ v) * kMul;
        a ^= (a >> 47);
        ulong b = (v ^ a) * kMul;
        b ^= (b >> 47);
        b *= kMul;
        return b;
    }

    private static ulong HashLen0to16(byte[] s, int pos, int len)
    {
        if (len >= 8)
        {
            ulong mul = k2 + (ulong)len * 2;
            ulong a = Fetch64(s, pos) + k2;
            ulong b = Fetch64(s, pos + len - 8);
            ulong c = Rotate(b, 37) * mul + a;
            ulong d = (Rotate(a, 25) + b) * mul;
            return HashLen16(c, d, mul);
        }
        if (len >= 4)
        {
            ulong mul = k2 + (ulong)len * 2;
            ulong a = Fetch32(s, pos);
            return HashLen16((ulong)len + (a << 3), Fetch32(s, pos + len - 4), mul);
        }
        if (len > 0)
        {
            byte a = s[pos];
            byte b = s[pos + (len >> 1)];
            byte c = s[pos + len - 1];
            uint y = a + ((uint)b << 8);
            uint z = (uint)len + ((uint)c << 2);
            return ShiftMix(y * k2 ^ z * k0) * k2;
        }
        return k2;
    }

    private static ulong HashLen17to32(byte[] s, int pos, int len)
    {
        ulong mul = k2 + (ulong)len * 2;
        ulong a = Fetch64(s, pos) * k1;
        ulong b = Fetch64(s, pos + 8);
        ulong c = Fetch64(s, pos + len - 8) * mul;
        ulong d = Fetch64(s, pos + len - 16) * k2;
        return HashLen16(Rotate(a + b, 43) + Rotate(c, 30) + d,
                         a + Rotate(b + k2, 18) + c, mul);
    }

    private static ulong HashLen33to64(byte[] s, int pos, int len)
    {
        // Ported from cityhasher 0.1.0 hash64_len_33_to_64
        ulong mul = k2 + (ulong)len * 2;
        ulong a = Fetch64(s, pos) * k2;
        ulong b = Fetch64(s, pos + 8);
        ulong c = Fetch64(s, pos + len - 24);
        ulong d = Fetch64(s, pos + len - 32);
        ulong e = Fetch64(s, pos + 16) * k2;
        ulong f = Fetch64(s, pos + 24) * 9;
        ulong g = Fetch64(s, pos + len - 8);
        ulong h = Fetch64(s, pos + len - 16) * mul;
        ulong u = Rotate(a + g, 43) + (Rotate(b, 30) + c) * 9;
        ulong v = ((a + g) ^ d) + f + 1;
        ulong w = BSwap64((u + v) * mul) + h;
        ulong x = Rotate(e + f, 42) + c;
        ulong y = (BSwap64((v + w) * mul) + g) * mul;
        ulong z = e + f + c;
        a = BSwap64((x + z) * mul + y) + b;
        b = ShiftMix((z + a) * mul + d + h) * mul;
        return b + x;
    }
    
    private static ulong BSwap64(ulong value)
    {
        return ((value & 0x00000000000000FFUL) << 56) |
               ((value & 0x000000000000FF00UL) << 40) |
               ((value & 0x0000000000FF0000UL) << 24) |
               ((value & 0x00000000FF000000UL) << 8) |
               ((value & 0x000000FF00000000UL) >> 8) |
               ((value & 0x0000FF0000000000UL) >> 24) |
               ((value & 0x00FF000000000000UL) >> 40) |
               ((value & 0xFF00000000000000UL) >> 56);
    }
    
    private static (ulong, ulong) WeakHashLen32WithSeeds(byte[] s, int pos, ulong a, ulong b)
    {
        return WeakHashLen32WithSeeds(
            Fetch64(s, pos),
            Fetch64(s, pos + 8),
            Fetch64(s, pos + 16),
            Fetch64(s, pos + 24),
            a, b);
    }

    private static (ulong, ulong) WeakHashLen32WithSeeds(ulong w, ulong x, ulong y, ulong z, ulong a, ulong b)
    {
        a += w;
        b = Rotate(b + a + z, 21);
        ulong c = a;
        a += x;
        a += y;
        b += Rotate(a, 44);
        return (a + z, b + c);
    }
}
