using System;
using System.IO;

namespace UAssetTool.IoStore;

/// <summary>
/// IoStore offset and length (10 bytes packed format).
/// Reference: retoc-rivals/src/lib.rs FIoOffsetAndLength
/// Uses BIG-ENDIAN byte order for both offset and length!
/// </summary>
public struct FIoOffsetAndLength
{
    // Packed: 5 bytes offset (40 bits), 5 bytes length (40 bits) - BIG ENDIAN
    private readonly byte[] _data;

    public FIoOffsetAndLength(ulong offset, ulong length)
    {
        _data = new byte[10];
        SetOffset(offset);
        SetLength(length);
    }

    private void SetOffset(ulong offset)
    {
        // Big-endian: to_be_bytes()[3..8] -> data[0..5]
        byte[] bytes = BitConverter.GetBytes(offset);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);
        // bytes is now big-endian, copy bytes[3..8] to data[0..5]
        _data[0] = bytes[3];
        _data[1] = bytes[4];
        _data[2] = bytes[5];
        _data[3] = bytes[6];
        _data[4] = bytes[7];
    }

    private void SetLength(ulong length)
    {
        // Big-endian: to_be_bytes()[3..8] -> data[5..10]
        byte[] bytes = BitConverter.GetBytes(length);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);
        // bytes is now big-endian, copy bytes[3..8] to data[5..10]
        _data[5] = bytes[3];
        _data[6] = bytes[4];
        _data[7] = bytes[5];
        _data[8] = bytes[6];
        _data[9] = bytes[7];
    }

    public ulong Offset
    {
        get
        {
            // Big-endian read
            return ((ulong)_data[0] << 32)
                | ((ulong)_data[1] << 24)
                | ((ulong)_data[2] << 16)
                | ((ulong)_data[3] << 8)
                | _data[4];
        }
    }

    public ulong Length
    {
        get
        {
            // Big-endian read
            return ((ulong)_data[5] << 32)
                | ((ulong)_data[6] << 24)
                | ((ulong)_data[7] << 16)
                | ((ulong)_data[8] << 8)
                | _data[9];
        }
    }

    public byte[] ToBytes() => _data;

    public void Write(BinaryWriter writer) => writer.Write(_data);
}

/// <summary>
/// IoStore compressed block entry (12 bytes).
/// Reference: retoc-rivals/src/lib.rs FIoStoreTocCompressedBlockEntry
/// </summary>
public struct FIoStoreTocCompressedBlockEntry
{
    // Packed format:
    // - offset: 5 bytes (40 bits)
    // - compressed_size: 3 bytes (24 bits)
    // - uncompressed_size: 3 bytes (24 bits)
    // - compression_method: 1 byte

    private readonly byte[] _data;

    public FIoStoreTocCompressedBlockEntry(ulong offset, uint compressedSize, uint uncompressedSize, byte compressionMethod)
    {
        _data = new byte[12];

        // Offset (5 bytes)
        _data[0] = (byte)(offset & 0xFF);
        _data[1] = (byte)((offset >> 8) & 0xFF);
        _data[2] = (byte)((offset >> 16) & 0xFF);
        _data[3] = (byte)((offset >> 24) & 0xFF);
        _data[4] = (byte)((offset >> 32) & 0xFF);

        // Compressed size (3 bytes)
        _data[5] = (byte)(compressedSize & 0xFF);
        _data[6] = (byte)((compressedSize >> 8) & 0xFF);
        _data[7] = (byte)((compressedSize >> 16) & 0xFF);

        // Uncompressed size (3 bytes)
        _data[8] = (byte)(uncompressedSize & 0xFF);
        _data[9] = (byte)((uncompressedSize >> 8) & 0xFF);
        _data[10] = (byte)((uncompressedSize >> 16) & 0xFF);

        // Compression method (1 byte)
        _data[11] = compressionMethod;
    }

    public byte[] ToBytes() => _data;

    public void Write(BinaryWriter writer) => writer.Write(_data);
}

/// <summary>
/// IoStore chunk hash (32 bytes - BLAKE3).
/// Reference: retoc-rivals/src/lib.rs FIoChunkHash
/// </summary>
public struct FIoChunkHash
{
    public byte[] Hash { get; }

    public FIoChunkHash(byte[] blake3Hash)
    {
        // IoStore uses first 20 bytes of BLAKE3 hash (truncated to match SHA1 size)
        Hash = new byte[32];
        Array.Copy(blake3Hash, Hash, Math.Min(blake3Hash.Length, 32));
    }

    public void Write(BinaryWriter writer) => writer.Write(Hash);
}

/// <summary>
/// IoStore entry meta.
/// Reference: retoc-rivals/src/lib.rs FIoStoreTocEntryMeta
/// </summary>
public struct FIoStoreTocEntryMeta
{
    public FIoChunkHash ChunkHash { get; set; }
    public byte Flags { get; set; }

    public void Write(BinaryWriter writer)
    {
        ChunkHash.Write(writer);
        writer.Write(Flags);
    }
}

/// <summary>
/// IoStore container ID.
/// Reference: retoc-rivals/src/lib.rs FIoContainerId
/// </summary>
public struct FIoContainerId
{
    public ulong Value { get; set; }

    public FIoContainerId(ulong value)
    {
        Value = value;
    }

    public static FIoContainerId FromName(string name)
    {
        return new FIoContainerId(CityHash.CityHash64(name.ToLowerInvariant()));
    }
}

/// <summary>
/// IoStore TOC version.
/// Reference: retoc-rivals/src/lib.rs EIoStoreTocVersion
/// </summary>
public enum EIoStoreTocVersion : byte
{
    Invalid = 0,
    Initial = 1,
    DirectoryIndex = 2,
    PartitionSize = 3,
    PerfectHash = 4,
    PerfectHashWithOverflow = 5, // UE5.3
    OnDemandMetaData = 6,
}

/// <summary>
/// Container header version.
/// Reference: retoc-rivals/src/container_header.rs EIoContainerHeaderVersion
/// </summary>
public enum EIoContainerHeaderVersion : int
{
    PreInitial = -1,
    Initial = 0,
    LocalizedPackages = 1,
    OptionalSegmentPackages = 2, // UE5.3
    NoExportInfo = 3,
    SoftPackageReferences = 4,
}

/// <summary>
/// Package store entry for container header.
/// Reference: retoc-rivals/src/container_header.rs StoreEntry
/// </summary>
public class StoreEntry
{
    public ulong ExportBundlesSize { get; set; }
    public uint LoadOrder { get; set; }
    public int ExportCount { get; set; }
    public int ExportBundleCount { get; set; }
    public List<FPackageId> ImportedPackages { get; set; } = new();
    public List<byte[]> ShaderMapHashes { get; set; } = new(); // 20 bytes each (SHA1)
}

/// <summary>
/// Container header for IoStore.
/// Reference: retoc-rivals/src/container_header.rs FIoContainerHeader
/// </summary>
public class FIoContainerHeader
{
    private const uint MAGIC = 0x496f436e; // "IoCn"

    public EIoContainerHeaderVersion Version { get; set; } = EIoContainerHeaderVersion.OptionalSegmentPackages;
    public FIoContainerId ContainerId { get; set; }
    public List<(FPackageId, StoreEntry)> Packages { get; set; } = new();

    public FIoContainerHeader(FIoContainerId containerId, EIoContainerHeaderVersion version = EIoContainerHeaderVersion.OptionalSegmentPackages)
    {
        ContainerId = containerId;
        Version = version;
    }

    public void AddPackage(FPackageId packageId, StoreEntry storeEntry)
    {
        Packages.Add((packageId, storeEntry));
    }

    /// <summary>
    /// Serialize the container header.
    /// Reference: retoc-rivals/src/container_header.rs FIoContainerHeader::ser()
    /// </summary>
    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Write magic and version for versions > Initial
        if (Version > EIoContainerHeaderVersion.Initial)
        {
            writer.Write(MAGIC);
            writer.Write((uint)Version);
        }

        // Container ID
        writer.Write(ContainerId.Value);

        // Serialize store entries
        SerializeStoreEntries(writer);

        // For versions > Initial, write additional fields
        if (Version > EIoContainerHeaderVersion.Initial)
        {
            // Optional segment package IDs (empty)
            writer.Write(0); // count

            // Optional segment store entries (empty buffer)
            writer.Write(0); // buffer length

            // Redirect name map (empty) - only write count, no hash count for empty map
            // Reference: retoc-rivals/src/name_map.rs write_name_batch()
            writer.Write(0); // name count (when 0, nothing else is written)

            // Localized packages (empty)
            writer.Write(0); // count

            // Package redirects (empty)
            writer.Write(0); // count
        }

        // For SoftPackageReferences version, write soft package references flag
        if (Version >= EIoContainerHeaderVersion.SoftPackageReferences)
        {
            writer.Write((byte)0); // has_soft_package_references = false
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Serialize store entries in the complex UE format.
    /// Reference: retoc-rivals/src/container_header.rs StoreEntries::serialize()
    /// 
    /// Uses two-pass approach to avoid buffer corruption with 4+ packages:
    /// Pass 1: Calculate offsets for all array data
    /// Pass 2: Write fixed entries, then write all array data sequentially
    /// </summary>
    private void SerializeStoreEntries(BinaryWriter writer)
    {
        // Package count
        writer.Write((uint)Packages.Count);

        // Package IDs
        foreach (var (packageId, _) in Packages)
        {
            writer.Write(packageId.Value);
        }

        // Calculate entry size based on version
        int memberOffset, entrySize;
        switch (Version)
        {
            case EIoContainerHeaderVersion.Initial:
                memberOffset = 24;
                entrySize = 32;
                break;
            case EIoContainerHeaderVersion.LocalizedPackages:
            case EIoContainerHeaderVersion.OptionalSegmentPackages:
                memberOffset = 8;
                entrySize = 24;
                break;
            case EIoContainerHeaderVersion.NoExportInfo:
            case EIoContainerHeaderVersion.SoftPackageReferences:
                memberOffset = 0;
                entrySize = 16;
                break;
            default:
                memberOffset = 8;
                entrySize = 16;
                break;
        }

        // Pass 1: Calculate where each entry's array data will be located
        int fixedDataSize = Packages.Count * entrySize;
        var arrayDataOffsets = new List<int>(); // Offset from start of buffer for each entry's array data
        int currentArrayOffset = fixedDataSize;
        
        foreach (var (_, entry) in Packages)
        {
            arrayDataOffsets.Add(currentArrayOffset);
            currentArrayOffset += entry.ImportedPackages.Count * 8; // FPackageId is 8 bytes
            if (Version > EIoContainerHeaderVersion.Initial)
            {
                currentArrayOffset += entry.ShaderMapHashes.Count * 20; // FSHAHash is 20 bytes
            }
        }

        // Pass 2: Build the buffer - write fixed entries first, then array data
        using var bufferMs = new MemoryStream();
        using var bufferWriter = new BinaryWriter(bufferMs);

        // Write all fixed entries
        for (int i = 0; i < Packages.Count; i++)
        {
            var (_, entry) = Packages[i];
            long entryOffset = bufferMs.Position;

            // Write fixed part of entry
            if (Version == EIoContainerHeaderVersion.Initial)
            {
                bufferWriter.Write(entry.ExportBundlesSize);
            }
            if (Version < EIoContainerHeaderVersion.NoExportInfo)
            {
                bufferWriter.Write(entry.ExportCount);
                bufferWriter.Write(entry.ExportBundleCount);
            }
            if (Version == EIoContainerHeaderVersion.Initial)
            {
                bufferWriter.Write(entry.LoadOrder);
                bufferWriter.Write(0u); // pad
            }

            // Imported packages array view
            if (entry.ImportedPackages.Count > 0)
            {
                int offset = arrayDataOffsets[i] - (int)entryOffset - memberOffset;
                bufferWriter.Write((uint)entry.ImportedPackages.Count);
                bufferWriter.Write((uint)offset);
            }
            else
            {
                bufferWriter.Write(0u); // array_num
                bufferWriter.Write(0u); // offset
            }

            // Shader map hashes array view (for versions > Initial)
            if (Version > EIoContainerHeaderVersion.Initial)
            {
                if (entry.ShaderMapHashes.Count > 0)
                {
                    int shaderOffset = arrayDataOffsets[i] + entry.ImportedPackages.Count * 8;
                    int offset = shaderOffset - (int)entryOffset - memberOffset - 8;
                    bufferWriter.Write((uint)entry.ShaderMapHashes.Count);
                    bufferWriter.Write((uint)offset);
                }
                else
                {
                    bufferWriter.Write(0u); // array_num
                    bufferWriter.Write(0u); // offset
                }
            }
        }

        // Write all array data sequentially (no position jumping)
        foreach (var (_, entry) in Packages)
        {
            // Write imported packages
            foreach (var pkg in entry.ImportedPackages)
            {
                bufferWriter.Write(pkg.Value);
            }

            // Write shader map hashes
            if (Version > EIoContainerHeaderVersion.Initial)
            {
                foreach (var hash in entry.ShaderMapHashes)
                {
                    bufferWriter.Write(hash);
                }
            }
        }

        // Write buffer with length prefix
        byte[] buffer = bufferMs.ToArray();
        writer.Write((uint)buffer.Length);
        writer.Write(buffer);
    }
}

/// <summary>
/// IoStore TOC header.
/// Reference: retoc-rivals/src/lib.rs FIoStoreTocHeader
/// </summary>
public class FIoStoreTocHeader
{
    public static readonly byte[] TOC_MAGIC = { 0x2D, 0x3D, 0x3D, 0x2D, 0x2D, 0x3D, 0x3D, 0x2D, 0x2D, 0x3D, 0x3D, 0x2D, 0x2D, 0x3D, 0x3D, 0x2D };

    // Header fields in exact Rust order (144 bytes total)
    public EIoStoreTocVersion Version { get; set; } = EIoStoreTocVersion.PerfectHashWithOverflow;
    public byte Reserved0 { get; set; }
    public ushort Reserved1 { get; set; }
    public uint TocHeaderSize { get; set; } = 144;
    public uint TocEntryCount { get; set; }
    public uint TocCompressedBlockEntryCount { get; set; }
    public uint TocCompressedBlockEntrySize { get; set; } = 12;
    public uint CompressionMethodNameCount { get; set; }
    public uint CompressionMethodNameLength { get; set; } = 32;
    public uint CompressionBlockSize { get; set; } = 0x20000; // 128KB
    public uint DirectoryIndexSize { get; set; }
    public uint PartitionCount { get; set; } = 1;  // u32, not u64!
    public FIoContainerId ContainerId { get; set; }
    public byte[] EncryptionKeyGuid { get; set; } = new byte[16];
    public EIoContainerFlags ContainerFlags { get; set; } = EIoContainerFlags.None;
    public byte Reserved3 { get; set; }
    public ushort Reserved4 { get; set; }
    public uint TocChunkPerfectHashSeedsCount { get; set; }
    public ulong PartitionSize { get; set; } = ulong.MaxValue;
    public uint TocChunksWithoutPerfectHashCount { get; set; }
    public uint Reserved7 { get; set; }
    public ulong[] Reserved8 { get; set; } = new ulong[5];

    public void Write(BinaryWriter writer)
    {
        // Write in exact Rust struct order
        writer.Write(TOC_MAGIC);                    // 16 bytes
        writer.Write((byte)Version);                // 1 byte
        writer.Write(Reserved0);                    // 1 byte
        writer.Write(Reserved1);                    // 2 bytes
        writer.Write(TocHeaderSize);                // 4 bytes (offset 20)
        writer.Write(TocEntryCount);                // 4 bytes
        writer.Write(TocCompressedBlockEntryCount); // 4 bytes
        writer.Write(TocCompressedBlockEntrySize);  // 4 bytes
        writer.Write(CompressionMethodNameCount);   // 4 bytes
        writer.Write(CompressionMethodNameLength);  // 4 bytes
        writer.Write(CompressionBlockSize);         // 4 bytes
        writer.Write(DirectoryIndexSize);           // 4 bytes
        writer.Write(PartitionCount);               // 4 bytes (u32!)
        writer.Write(ContainerId.Value);            // 8 bytes
        writer.Write(EncryptionKeyGuid);            // 16 bytes
        writer.Write((byte)ContainerFlags);         // 1 byte
        writer.Write(Reserved3);                    // 1 byte
        writer.Write(Reserved4);                    // 2 bytes
        writer.Write(TocChunkPerfectHashSeedsCount);// 4 bytes
        writer.Write(PartitionSize);                // 8 bytes
        writer.Write(TocChunksWithoutPerfectHashCount); // 4 bytes
        writer.Write(Reserved7);                    // 4 bytes
        foreach (var r in Reserved8)
            writer.Write(r);                        // 40 bytes (5 * 8)
        // Total: 144 bytes
    }
}

[Flags]
public enum EIoContainerFlags : byte
{
    None = 0,
    Compressed = 1 << 0,
    Encrypted = 1 << 1,
    Signed = 1 << 2,
    Indexed = 1 << 3,
}
