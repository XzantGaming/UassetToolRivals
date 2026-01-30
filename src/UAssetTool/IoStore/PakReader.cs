using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace UAssetTool.IoStore;

/// <summary>
/// PAK V11 reader implementation based on repak source code.
/// Reads encrypted PAK files compatible with Marvel Rivals.
/// 
/// Reference: repak/src/pak.rs, repak/src/entry.rs, repak/src/data.rs, repak/src/footer.rs
/// </summary>
public class PakReader : IDisposable
{
    // Constants from repak/src/lib.rs
    private const uint PAK_MAGIC = 0x5A6F12E1;
    private const string DEFAULT_AES_KEY_HEX = "0C263D8C22DCB085894899C3A3796383E9BF9DE0CBFB08C9BF2DEF2E84F29D74";

    private readonly Stream _stream;
    private readonly byte[] _aesKey;
    private readonly bool _ownsStream;
    private bool _disposed;

    // PAK metadata
    public int Version { get; private set; }
    public string MountPoint { get; private set; } = "";
    public bool EncryptedIndex { get; private set; }
    public ulong PathHashSeed { get; private set; }
    
    // Index data
    private readonly Dictionary<string, PakEntry> _entries = new();
    
    // Compression methods from footer
    private readonly string[] _compressionMethods = new string[5];

    public PakReader(string pakPath, string? aesKeyHex = null)
        : this(File.OpenRead(pakPath), aesKeyHex, ownsStream: true)
    {
    }

    public PakReader(Stream stream, string? aesKeyHex = null, bool ownsStream = false)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        _aesKey = ParseAesKey(aesKeyHex ?? DEFAULT_AES_KEY_HEX);
        
        ReadPak();
    }

    /// <summary>
    /// Parse AES key from hex string with 4-byte chunk reversal (from repak/src/utils.rs)
    /// </summary>
    private static byte[] ParseAesKey(string hex)
    {
        hex = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex;
        byte[] bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }

        // CRITICAL: Reverse each 4-byte chunk (from repak/src/utils.rs line 9)
        for (int i = 0; i < bytes.Length; i += 4)
        {
            Array.Reverse(bytes, i, 4);
        }

        return bytes;
    }

    /// <summary>
    /// Get list of all file paths in the PAK
    /// </summary>
    public IReadOnlyList<string> Files => _entries.Keys.ToList();

    /// <summary>
    /// Get entry info for a specific file
    /// </summary>
    public PakEntry? GetEntry(string path)
    {
        return _entries.TryGetValue(path, out var entry) ? entry : null;
    }

    /// <summary>
    /// Read file data from the PAK
    /// </summary>
    public byte[] Get(string path)
    {
        if (!_entries.TryGetValue(path, out var entry))
            throw new KeyNotFoundException($"Entry not found: {path}");

        return ReadEntryData(entry, path);
    }

    /// <summary>
    /// Check if PAK contains a specific file
    /// </summary>
    public bool Contains(string path) => _entries.ContainsKey(path);

    /// <summary>
    /// Read and parse the PAK file
    /// </summary>
    private void ReadPak()
    {
        using var reader = new BinaryReader(_stream, Encoding.UTF8, leaveOpen: true);

        // 1. Read footer (at end of file)
        ReadFooter(reader, out long indexOffset, out ulong indexSize, out byte[] expectedIndexHash);

        // 2. Read index (decrypt if encrypted)
        _stream.Seek(indexOffset, SeekOrigin.Begin);
        byte[] rawIndex = reader.ReadBytes((int)indexSize);
        byte[] indexData;
        
        if (EncryptedIndex)
        {
            indexData = AesDecrypt(rawIndex, _aesKey);
        }
        else
        {
            indexData = rawIndex;
        }

        // Verify index hash
        byte[] actualIndexHash = SHA1.HashData(indexData);
        if (!actualIndexHash.SequenceEqual(expectedIndexHash))
        {
            Console.Error.WriteLine("[PakReader] Warning: Index hash mismatch (may still work)");
        }

        // 3. Parse index
        using var indexStream = new MemoryStream(indexData);
        using var indexReader = new BinaryReader(indexStream);
        ParseIndex(indexReader, reader);
    }

    /// <summary>
    /// Read footer from end of PAK file (from footer.rs)
    /// Footer is 221 bytes for V11
    /// </summary>
    private void ReadFooter(BinaryReader reader, out long indexOffset, out ulong indexSize, out byte[] indexHash)
    {
        // Footer size for V11 = 221 bytes
        // encryption_guid(16) + encrypted(1) + magic(4) + version(4) + offset(8) + size(8) + hash(20) + compression(5*32=160)
        const int footerSize = 221;
        
        _stream.Seek(-footerSize, SeekOrigin.End);

        // Skip encryption GUID (16 bytes)
        reader.ReadBytes(16);

        // Is index encrypted (1 byte)
        EncryptedIndex = reader.ReadByte() != 0;

        // Magic (4 bytes)
        uint magic = reader.ReadUInt32();
        if (magic != PAK_MAGIC)
            throw new InvalidDataException($"Invalid PAK magic: 0x{magic:X8}, expected 0x{PAK_MAGIC:X8}");

        // Version (4 bytes)
        Version = reader.ReadInt32();
        if (Version != 11)
            Console.Error.WriteLine($"[PakReader] Warning: PAK version {Version}, expected 11");

        // Index offset (8 bytes)
        indexOffset = reader.ReadInt64();

        // Index size (8 bytes)
        indexSize = reader.ReadUInt64();

        // Index hash (20 bytes)
        indexHash = reader.ReadBytes(20);

        // Read compression methods (5 * 32 = 160 bytes)
        for (int i = 0; i < 5; i++)
        {
            byte[] methodBytes = reader.ReadBytes(32);
            int nullIndex = Array.IndexOf(methodBytes, (byte)0);
            if (nullIndex < 0) nullIndex = 32;
            _compressionMethods[i] = Encoding.ASCII.GetString(methodBytes, 0, nullIndex);
        }
    }

    /// <summary>
    /// Parse the decrypted index (from pak.rs)
    /// </summary>
    private void ParseIndex(BinaryReader indexReader, BinaryReader dataReader)
    {
        // Mount point (length-prefixed string with null terminator)
        MountPoint = ReadString(indexReader);

        // Record count
        uint recordCount = indexReader.ReadUInt32();

        // Path hash seed (V10+)
        PathHashSeed = indexReader.ReadUInt64();

        // Has path hash index
        uint hasPathHashIndex = indexReader.ReadUInt32();
        long pathHashIndexOffset = 0;
        ulong pathHashIndexSize = 0;
        byte[] pathHashIndexHash = Array.Empty<byte>();
        
        if (hasPathHashIndex != 0)
        {
            pathHashIndexOffset = indexReader.ReadInt64();
            pathHashIndexSize = indexReader.ReadUInt64();
            pathHashIndexHash = indexReader.ReadBytes(20);
        }

        // Has full directory index
        uint hasFullDirectoryIndex = indexReader.ReadUInt32();
        long fullDirectoryIndexOffset = 0;
        ulong fullDirectoryIndexSize = 0;
        byte[] fullDirectoryIndexHash = Array.Empty<byte>();
        
        if (hasFullDirectoryIndex != 0)
        {
            fullDirectoryIndexOffset = indexReader.ReadInt64();
            fullDirectoryIndexSize = indexReader.ReadUInt64();
            fullDirectoryIndexHash = indexReader.ReadBytes(20);
        }

        // Encoded entries size and data
        uint encodedEntriesSize = indexReader.ReadUInt32();
        byte[] encodedEntriesData = indexReader.ReadBytes((int)encodedEntriesSize);

        // Unused file count
        uint unusedFileCount = indexReader.ReadUInt32();

        // Read path hash index to get file paths (decrypt only if index is encrypted)
        if (hasPathHashIndex != 0 && pathHashIndexSize > 0)
        {
            _stream.Seek(pathHashIndexOffset, SeekOrigin.Begin);
            byte[] rawPhi = dataReader.ReadBytes((int)pathHashIndexSize);
            byte[] phiData = EncryptedIndex ? AesDecrypt(rawPhi, _aesKey) : rawPhi;

            using var phiStream = new MemoryStream(phiData);
            using var phiReader = new BinaryReader(phiStream);
            
            uint phiCount = phiReader.ReadUInt32();
            var pathHashToOffset = new Dictionary<ulong, uint>();
            
            for (uint i = 0; i < phiCount; i++)
            {
                ulong pathHash = phiReader.ReadUInt64();
                uint offset = phiReader.ReadUInt32();
                pathHashToOffset[pathHash] = offset;
            }
        }

        // Read full directory index to get file paths and entry offsets (decrypt only if index is encrypted)
        if (hasFullDirectoryIndex != 0 && fullDirectoryIndexSize > 0)
        {
            _stream.Seek(fullDirectoryIndexOffset, SeekOrigin.Begin);
            byte[] rawFdi = dataReader.ReadBytes((int)fullDirectoryIndexSize);
            byte[] fdiData = EncryptedIndex ? AesDecrypt(rawFdi, _aesKey) : rawFdi;

            using var fdiStream = new MemoryStream(fdiData);
            using var fdiReader = new BinaryReader(fdiStream);
            
            uint directoryCount = fdiReader.ReadUInt32();
            
            for (uint d = 0; d < directoryCount; d++)
            {
                string directory = ReadString(fdiReader);
                uint fileCount = fdiReader.ReadUInt32();
                
                for (uint f = 0; f < fileCount; f++)
                {
                    string filename = ReadString(fdiReader);
                    uint encodedOffset = fdiReader.ReadUInt32();
                    
                    // Combine directory and filename
                    string fullPath = directory + filename;
                    
                    // Parse encoded entry at this offset
                    var entry = ParseEncodedEntry(encodedEntriesData, (int)encodedOffset);
                    _entries[fullPath] = entry;
                }
            }
        }

        Console.Error.WriteLine($"[PakReader] Loaded {_entries.Count} entries from PAK");
        Console.Error.WriteLine($"[PakReader] Compression methods: [{string.Join(", ", _compressionMethods.Where(m => !string.IsNullOrEmpty(m)))}]");
        
        // Debug: print first few entries
        int debugCount = 0;
        foreach (var kvp in _entries)
        {
            if (debugCount++ >= 3) break;
            Console.Error.WriteLine($"[PakReader] Entry '{kvp.Key}': Offset={kvp.Value.Offset}, Compressed={kvp.Value.CompressedSize}, Uncompressed={kvp.Value.UncompressedSize}, CompressionSlot={kvp.Value.CompressionSlot}, BlockSize={kvp.Value.CompressionBlockSize}, BlockCount={kvp.Value.CompressionBlocksCount}");
        }
    }

    /// <summary>
    /// Parse encoded entry (from entry.rs read_encoded)
    /// </summary>
    private static PakEntry ParseEncodedEntry(byte[] data, int offset)
    {
        using var stream = new MemoryStream(data, offset, data.Length - offset);
        using var reader = new BinaryReader(stream);

        uint flags = reader.ReadUInt32();

        // Extract flags (based on CUE4Parse FPakEntry.cs)
        // compressionBlockSize is stored as value << 11 (not << 10)
        // So we extract the raw value and will shift by 11 when using it
        uint compressionBlockSizeRaw = flags & 0x3F;
        uint compressionBlocksCount = (flags >> 6) & 0xFFFF;
        bool isEncrypted = ((flags >> 22) & 1) != 0;
        uint compressionSlot = (flags >> 23) & 0x3F;
        bool isSizeSafe = ((flags >> 29) & 1) != 0;
        bool isUncompressedSizeSafe = ((flags >> 30) & 1) != 0;
        bool isOffsetSafe = ((flags >> 31) & 1) != 0;

        // Read offset
        ulong entryOffset = isOffsetSafe ? reader.ReadUInt32() : reader.ReadUInt64();

        // Read uncompressed size
        ulong uncompressedSize = isUncompressedSizeSafe ? reader.ReadUInt32() : reader.ReadUInt64();

        // Compressed size (if compression is used)
        ulong compressedSize = uncompressedSize;
        if (compressionSlot > 0)
        {
            compressedSize = isSizeSafe ? reader.ReadUInt32() : reader.ReadUInt64();
        }

        return new PakEntry
        {
            Offset = entryOffset,
            CompressedSize = compressedSize,
            UncompressedSize = uncompressedSize,
            CompressionSlot = compressionSlot > 0 ? (int?)(compressionSlot - 1) : null,
            IsEncrypted = isEncrypted,
            CompressionBlockSize = compressionBlockSizeRaw,
            CompressionBlocksCount = compressionBlocksCount
        };
    }

    /// <summary>
    /// Read entry data from PAK file using entry info from index
    /// </summary>
    private byte[] ReadEntryData(PakEntry entry, string path)
    {
        // Use the entry info from the index (already parsed from encoded entries)
        ulong uncompressedSize = entry.UncompressedSize;
        ulong compressedSize = entry.CompressedSize;
        bool isCompressed = entry.CompressionSlot.HasValue;
        bool isEncrypted = entry.IsEncrypted;
        uint compressionMethod = isCompressed ? (uint)(entry.CompressionSlot!.Value + 1) : 0;

        // Handle empty files
        if (uncompressedSize == 0)
        {
            return Array.Empty<byte>();
        }

        // Seek to entry offset
        _stream.Seek((long)entry.Offset, SeekOrigin.Begin);
        using var reader = new BinaryReader(_stream, Encoding.UTF8, leaveOpen: true);
        
        // Debug: show entry offset
        if (Environment.GetEnvironmentVariable("DEBUG_PAK") == "1")
        {
            Console.Error.WriteLine($"[PakReader] Reading entry at offset={entry.Offset}, compressed={compressedSize}, uncompressed={uncompressedSize}");
        }
        
        // Read FPakEntry header (V11 format):
        // offset(8) + compressed(8) + uncompressed(8) + compression(4) + hash(20) = 48 bytes
        // Then: blockCount(4) + blocks(16 each for compressed) OR data directly for uncompressed
        reader.ReadBytes(48); // Skip base entry header

        if (!isCompressed)
        {
            // Uncompressed: data follows directly after header
            // For encrypted files, data is padded to 16-byte alignment
            int dataSize = (int)uncompressedSize;
            if (isEncrypted)
            {
                // Calculate padded size (16-byte alignment)
                dataSize = ((int)uncompressedSize + 15) & ~15;
            }
            
            byte[] data = reader.ReadBytes(dataSize);
            if (isEncrypted)
            {
                data = PartialDecrypt(data, path);
            }
            // Truncate to actual uncompressed size (remove padding)
            if (data.Length > (int)uncompressedSize)
            {
                Array.Resize(ref data, (int)uncompressedSize);
            }
            return data;
        }

        // Compressed: read block count from file header (at position 48), then block offsets
        uint blockCount = reader.ReadUInt32();
        
        // Block size from encoded entry (shifted left by 11), or default 64KB
        uint blockSize;
        if (entry.CompressionBlockSize > 0)
        {
            blockSize = entry.CompressionBlockSize << 11;
        }
        else
        {
            blockSize = 65536;
        }

        var compressionBlocks = new List<(long start, long size)>();
        
        if (blockCount == 0)
        {
            // No block info in file - single block, data follows the header
            // Header is 48 bytes + 4 bytes blockCount = 52 bytes
            long dataOffset = (long)entry.Offset + 52;
            compressionBlocks.Add((dataOffset, (long)compressedSize));
        }
        else
        {
            // Read block offsets - each block has (start:8, end:8) = 16 bytes
            // Block offsets are RELATIVE to entry.Offset, not absolute
            for (uint i = 0; i < blockCount; i++)
            {
                long blockStart = reader.ReadInt64();
                long blockEnd = reader.ReadInt64();
                // Convert relative offsets to absolute by adding entry.Offset
                long absoluteStart = (long)entry.Offset + blockStart;
                compressionBlocks.Add((absoluteStart, blockEnd - blockStart));
            }
        }

        // Decompress each block
        using var outputStream = new MemoryStream((int)uncompressedSize);

        for (int blockIdx = 0; blockIdx < compressionBlocks.Count; blockIdx++)
        {
            var (blockStart, blockCompressedSize) = compressionBlocks[blockIdx];
            
            if (blockCompressedSize <= 0)
                continue;
            
            _stream.Seek(blockStart, SeekOrigin.Begin);
            byte[] blockData = new byte[blockCompressedSize];
            int bytesRead = _stream.Read(blockData, 0, (int)blockCompressedSize);
            if (bytesRead != blockCompressedSize)
            {
                throw new InvalidDataException($"Failed to read block {blockIdx}: expected {blockCompressedSize} bytes, got {bytesRead}");
            }
            
            // Debug: show first bytes of block data
            if (Environment.GetEnvironmentVariable("DEBUG_PAK") == "1" && blockIdx == 0)
            {
                string firstBytes = string.Join(" ", blockData.Take(16).Select(b => b.ToString("X2")));
                Console.Error.WriteLine($"[PakReader] Block {blockIdx}: offset={blockStart}, size={blockCompressedSize}, first16bytes={firstBytes}");
            }

            if (isEncrypted)
            {
                int alignedSize = ((int)blockCompressedSize + 15) & ~15;
                if (blockData.Length < alignedSize)
                {
                    Array.Resize(ref blockData, alignedSize);
                }
                blockData = AesDecrypt(blockData, _aesKey);
                if (blockData.Length > blockCompressedSize)
                {
                    Array.Resize(ref blockData, (int)blockCompressedSize);
                }
            }

            // Calculate expected uncompressed size for this block
            long remainingUncompressed = (long)uncompressedSize - outputStream.Position;
            int blockUncompressedSize = (int)Math.Min(blockSize, remainingUncompressed);
            
            if (blockUncompressedSize <= 0)
                continue;

            try
            {
                byte[] decompressedBlock = Decompress(blockData, blockUncompressedSize, compressionMethod);
                outputStream.Write(decompressedBlock, 0, decompressedBlock.Length);
            }
            catch (Exception)
            {
                Console.Error.WriteLine($"[PakReader] Block {blockIdx} decompression failed: compressed={blockCompressedSize}, uncompressed={blockUncompressedSize}, method={compressionMethod}");
                throw;
            }
        }

        return outputStream.ToArray();
    }

    /// <summary>
    /// Partially decrypt data based on path hash (from data.rs)
    /// </summary>
    private byte[] PartialDecrypt(byte[] data, string path)
    {
        int limit = GetEncryptionLimit(path);
        if (limit > data.Length)
            limit = data.Length;
        limit = (limit + 15) & ~15; // Align to 16 bytes
        if (limit > data.Length)
            limit = data.Length;

        byte[] result = new byte[data.Length];
        Array.Copy(data, result, data.Length);

        if (limit > 0)
        {
            byte[] toDecrypt = new byte[limit];
            Array.Copy(data, toDecrypt, limit);
            byte[] decrypted = AesDecrypt(toDecrypt, _aesKey);
            Array.Copy(decrypted, result, limit);
        }

        return result;
    }

    /// <summary>
    /// Calculate encryption limit based on path hash (from data.rs)
    /// </summary>
    private static int GetEncryptionLimit(string path)
    {
        using var hasher = Blake3.Hasher.New();
        hasher.Update(new byte[] { 0x11, 0x22, 0x33, 0x44 });
        hasher.Update(Encoding.ASCII.GetBytes(path.ToLowerInvariant()));

        byte[] hashBytes = hasher.Finalize().AsSpan().ToArray();
        ulong hashValue = BitConverter.ToUInt64(hashBytes, 0);

        long limit = (long)(((hashValue % 0x3d) * 63 + 319) & 0xffffffffffffffc0);
        if (limit == 0)
            limit = 0x1000;

        return (int)limit;
    }

    /// <summary>
    /// AES decrypt with UE4's custom byte-swapping pattern (from data.rs)
    /// </summary>
    private static byte[] AesDecrypt(byte[] data, byte[] key)
    {
        int paddedLength = (data.Length + 15) & ~15;
        byte[] paddedData = new byte[paddedLength];
        Array.Copy(data, paddedData, data.Length);

        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        byte[] result = new byte[paddedData.Length];

        for (int i = 0; i < paddedData.Length; i += 16)
        {
            byte[] block = new byte[16];
            Array.Copy(paddedData, i, block, 0, 16);

            // Reverse each 4-byte chunk BEFORE decryption
            ReverseChunks(block);

            // Decrypt the block
            using var decryptor = aes.CreateDecryptor();
            byte[] decrypted = decryptor.TransformFinalBlock(block, 0, 16);

            // Reverse each 4-byte chunk AFTER decryption
            ReverseChunks(decrypted);

            Array.Copy(decrypted, 0, result, i, 16);
        }

        return result;
    }

    /// <summary>
    /// Reverse each 4-byte chunk in a 16-byte block
    /// </summary>
    private static void ReverseChunks(byte[] block)
    {
        for (int i = 0; i < 16; i += 4)
        {
            (block[i], block[i + 3]) = (block[i + 3], block[i]);
            (block[i + 1], block[i + 2]) = (block[i + 2], block[i + 1]);
        }
    }

    /// <summary>
    /// Read length-prefixed string with null terminator
    /// </summary>
    private static string ReadString(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length <= 0)
            return "";
        
        byte[] bytes = reader.ReadBytes(length);
        // Remove null terminator
        return Encoding.UTF8.GetString(bytes, 0, length - 1);
    }

    /// <summary>
    /// Decompress data using the specified compression method index
    /// </summary>
    private byte[] Decompress(byte[] data, int uncompressedSize, uint compressionMethodIndex)
    {
        // compressionMethodIndex is 1-based index into _compressionMethods array
        // 0 = no compression
        if (compressionMethodIndex == 0)
            return data;
            
        int methodIdx = (int)compressionMethodIndex - 1;
        if (methodIdx < 0 || methodIdx >= _compressionMethods.Length)
            throw new NotSupportedException($"Compression method index {compressionMethodIndex} out of range");
            
        string methodName = _compressionMethods[methodIdx];
        
        if (string.IsNullOrEmpty(methodName))
            throw new NotSupportedException($"Compression method at index {methodIdx} is empty");
        
        // Match by name (case-insensitive)
        if (methodName.Equals("Zlib", StringComparison.OrdinalIgnoreCase))
            return DecompressZlib(data, uncompressedSize);
        else if (methodName.Equals("Oodle", StringComparison.OrdinalIgnoreCase))
            return DecompressOodle(data, uncompressedSize);
        else if (methodName.Equals("Gzip", StringComparison.OrdinalIgnoreCase))
            return DecompressZlib(data, uncompressedSize); // Gzip uses same decompressor
        else if (methodName.Equals("LZ4", StringComparison.OrdinalIgnoreCase))
            return DecompressLz4(data, uncompressedSize);
        else if (methodName.Equals("Zstd", StringComparison.OrdinalIgnoreCase))
            return DecompressZstd(data, uncompressedSize);
        else
            throw new NotSupportedException($"Compression method '{methodName}' not supported");
    }

    private static byte[] DecompressZlib(byte[] data, int uncompressedSize)
    {
        using var inputStream = new MemoryStream(data);
        using var zlibStream = new System.IO.Compression.ZLibStream(inputStream, System.IO.Compression.CompressionMode.Decompress);
        using var outputStream = new MemoryStream();
        zlibStream.CopyTo(outputStream);
        return outputStream.ToArray();
    }

    private static byte[] DecompressOodle(byte[] data, int uncompressedSize)
    {
        // Use the OodleCompression class which handles DLL loading/downloading
        byte[]? result = OodleCompression.Decompress(data, uncompressedSize);
        if (result == null)
            throw new InvalidDataException($"Oodle decompression failed. Make sure oo2core_9_win64.dll is available.");
        return result;
    }

    private static byte[] DecompressLz4(byte[] data, int uncompressedSize)
    {
        // LZ4 decompression
        // For now, throw - can add K4os.Compression.LZ4 NuGet package if needed
        throw new NotSupportedException("LZ4 decompression not yet implemented. Add K4os.Compression.LZ4 package.");
    }

    private static byte[] DecompressZstd(byte[] data, int uncompressedSize)
    {
        using var decompressor = new ZstdSharp.Decompressor();
        return decompressor.Unwrap(data).ToArray();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_ownsStream)
                _stream.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Represents a PAK file entry
/// </summary>
public class PakEntry
{
    public ulong Offset { get; set; }
    public ulong CompressedSize { get; set; }
    public ulong UncompressedSize { get; set; }
    public int? CompressionSlot { get; set; }
    public bool IsEncrypted { get; set; }
    public uint CompressionBlockSize { get; set; }
    public uint CompressionBlocksCount { get; set; }
}
