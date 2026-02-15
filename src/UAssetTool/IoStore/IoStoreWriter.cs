using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Blake3;

namespace UAssetTool.IoStore;

/// <summary>
/// IoStore container writer (.utoc/.ucas).
/// Reference: retoc-rivals/src/iostore_writer.rs
/// </summary>
public class IoStoreWriter : IDisposable
{
    // Default Marvel Rivals AES key
    private const string DEFAULT_AES_KEY = "0C263D8C22DCB085894899C3A3796383E9BF9DE0CBFB08C9BF2DEF2E84F29D74";

    private readonly string _tocPath;
    private readonly FileStream _tocStream;
    private readonly FileStream _casStream;
    private readonly EIoStoreTocVersion _tocVersion;
    private readonly EIoContainerHeaderVersion? _containerHeaderVersion;
    private readonly string _mountPoint;
    private readonly FIoContainerId _containerId;
    private readonly uint _compressionBlockSize = 0x20000; // 128KB
    private readonly bool _enableCompression;
    private readonly bool _enableEncryption;
    private readonly byte[]? _aesKey;
    private byte[] _encryptionKeyGuid = new byte[16];

    /// <summary>
    /// Set the encryption key GUID (preserved from original container during recompression).
    /// </summary>
    public void SetEncryptionKeyGuid(byte[] guid)
    {
        if (guid != null && guid.Length == 16)
            _encryptionKeyGuid = guid;
    }

    /// <summary>
    /// Set raw directory index bytes to pass through during recompression.
    /// When set, WriteToc uses these bytes instead of rebuilding the directory index.
    /// </summary>
    public void SetRawDirectoryIndex(byte[] rawData)
    {
        _rawDirectoryIndex = rawData;
    }

    private readonly List<FIoChunkId> _chunks = new();
    private readonly List<FIoOffsetAndLength> _chunkOffsetLengths = new();
    private readonly List<FIoStoreTocCompressedBlockEntry> _compressionBlocks = new();
    private readonly List<FIoStoreTocEntryMeta> _chunkMetas = new();
    private readonly Dictionary<string, uint> _directoryIndex = new();
    private readonly List<(FPackageId, StoreEntry)> _packageStoreEntries = new();
    private readonly HashSet<string> _compressionMethods = new();
    private byte[]? _rawDirectoryIndex = null;

    private bool _disposed;

    public IoStoreWriter(
        string tocPath,
        EIoStoreTocVersion tocVersion = EIoStoreTocVersion.PerfectHashWithOverflow,
        EIoContainerHeaderVersion? containerHeaderVersion = EIoContainerHeaderVersion.NoExportInfo,
        string mountPoint = "../../../",
        bool enableCompression = true,
        bool enableEncryption = false,
        string? aesKeyHex = null,
        FIoContainerId? containerId = null,
        uint compressionBlockSize = 0x20000)
    {
        _tocPath = tocPath;
        _tocVersion = tocVersion;
        _containerHeaderVersion = containerHeaderVersion;
        _mountPoint = mountPoint;
        _enableCompression = enableCompression;
        _enableEncryption = enableEncryption;
        _compressionBlockSize = compressionBlockSize;

        // Parse AES key
        if (enableEncryption)
        {
            string keyHex = aesKeyHex ?? DEFAULT_AES_KEY;
            _aesKey = new byte[32];
            for (int i = 0; i < 32; i++)
                _aesKey[i] = Convert.ToByte(keyHex.Substring(i * 2, 2), 16);
        }

        // Use provided container ID or generate from filename
        if (containerId.HasValue)
            _containerId = containerId.Value;
        else
        {
            string name = Path.GetFileNameWithoutExtension(tocPath);
            _containerId = FIoContainerId.FromName(name);
        }

        _tocStream = new FileStream(tocPath, FileMode.Create, FileAccess.Write);
        _casStream = new FileStream(Path.ChangeExtension(tocPath, ".ucas"), FileMode.Create, FileAccess.Write);

        // Initialize Oodle if compression enabled
        if (_enableCompression)
        {
            OodleCompression.Initialize();
            if (!OodleCompression.IsAvailable)
            {
                Console.Error.WriteLine("[IoStoreWriter] Oodle not available, falling back to uncompressed");
                _enableCompression = false;
            }
        }
    }

    /// <summary>
    /// Write a chunk to the container.
    /// Reference: retoc-rivals/src/iostore_writer.rs write_chunk()
    /// </summary>
    public void WriteChunk(FIoChunkId chunkId, string? path, byte[] data)
    {
        // Add to directory index if path provided
        if (!string.IsNullOrEmpty(path))
        {
            string relativePath = path;
            if (relativePath.StartsWith(_mountPoint))
                relativePath = relativePath.Substring(_mountPoint.Length);
            _directoryIndex[relativePath] = (uint)_chunks.Count;
        }

        long chunkStartOffset = _casStream.Position;  // Actual byte offset in CAS file
        int startBlock = _compressionBlocks.Count;

        // Create BLAKE3 hasher for chunk hash
        using var hasher = Hasher.New();

        // Write data in compression blocks
        long blockOffset = chunkStartOffset;
        foreach (var block in ChunkData(data, (int)_compressionBlockSize))
        {
            // Hash uncompressed data (always hash uncompressed)
            hasher.Update(block);

            byte[] bytesToWrite;
            byte compressionMethod = 0;

            // Try Oodle compression if enabled
            if (_enableCompression)
            {
                var compressed = OodleCompression.Compress(block, OodleCompressor.Kraken, OodleCompressionLevel.Normal);
                
                // Use compressed if it's smaller
                if (compressed != null && compressed.Length < block.Length)
                {
                    bytesToWrite = compressed;
                    compressionMethod = 1; // Oodle
                    _compressionMethods.Add("Oodle");
                }
                else
                {
                    bytesToWrite = block;
                    compressionMethod = 0; // None
                }
            }
            else
            {
                bytesToWrite = block;
                compressionMethod = 0;
            }

            // Apply AES encryption if enabled (encrypt the block, padded to 16 bytes)
            byte[] finalBytes;
            if (_enableEncryption && _aesKey != null)
            {
                // Pad to 16-byte alignment
                int paddedLength = (bytesToWrite.Length + 15) & ~15;
                byte[] paddedData = new byte[paddedLength];
                Array.Copy(bytesToWrite, paddedData, bytesToWrite.Length);

                // Encrypt with AES-256-ECB
                finalBytes = EncryptAes(paddedData);
            }
            else
            {
                finalBytes = bytesToWrite;
            }

            // Write to CAS
            _casStream.Write(finalBytes, 0, finalBytes.Length);

            // Add compression block entry (store original compressed size, not padded)
            _compressionBlocks.Add(new FIoStoreTocCompressedBlockEntry(
                (ulong)blockOffset,
                (uint)bytesToWrite.Length,
                (uint)block.Length,
                compressionMethod
            ));

            blockOffset += finalBytes.Length;
        }

        // Create chunk meta with BLAKE3 hash
        byte[] hashBytes = hasher.Finalize().AsSpan().ToArray();
        var meta = new FIoStoreTocEntryMeta
        {
            ChunkHash = new FIoChunkHash(hashBytes),
            Flags = 0
        };

        // Add to TOC
        // IMPORTANT: Chunk offset must be VIRTUAL (block-aligned) offset, not actual CAS offset
        // The Rust code uses: block_index = chunk_offset / compression_block_size
        // So chunk_offset = startBlock * compression_block_size
        _chunks.Add(chunkId);
        _chunkOffsetLengths.Add(new FIoOffsetAndLength(
            (ulong)(startBlock * _compressionBlockSize),  // Virtual block-aligned offset
            (ulong)data.Length
        ));
        _chunkMetas.Add(meta);
    }

    /// <summary>
    /// Write a package chunk with store entry.
    /// Reference: retoc-rivals/src/iostore_writer.rs write_package_chunk()
    /// </summary>
    public void WritePackageChunk(FIoChunkId chunkId, string? path, byte[] data, StoreEntry storeEntry)
    {
        var packageId = new FPackageId(chunkId.Id);
        _packageStoreEntries.Add((packageId, storeEntry));
        WriteChunk(chunkId, path, data);
    }

    /// <summary>
    /// Write a raw chunk without package store entry (for non-package chunks like ScriptObjects)
    /// </summary>
    public void WriteRawChunk(FIoChunkId chunkId, byte[] data)
    {
        WriteChunk(chunkId, null, data);
    }

    /// <summary>
    /// Write a chunk without compression (used for Container Header which must not be compressed).
    /// </summary>
    public void WriteChunkUncompressed(FIoChunkId chunkId, byte[] data)
    {
        long chunkStartOffset = _casStream.Position;
        int startBlock = _compressionBlocks.Count;

        // Create BLAKE3 hasher for chunk hash
        using var hasher = Hasher.New();

        // Write data in blocks without compression
        long blockOffset = chunkStartOffset;
        foreach (var block in ChunkData(data, (int)_compressionBlockSize))
        {
            hasher.Update(block);

            byte[] finalBytes = block;

            // Apply AES encryption if enabled
            if (_enableEncryption && _aesKey != null)
            {
                int paddedLength = (block.Length + 15) & ~15;
                byte[] paddedData = new byte[paddedLength];
                Array.Copy(block, paddedData, block.Length);
                finalBytes = EncryptAes(paddedData);
            }

            _casStream.Write(finalBytes, 0, finalBytes.Length);

            // Add compression block entry with NO compression (method = 0)
            _compressionBlocks.Add(new FIoStoreTocCompressedBlockEntry(
                (ulong)blockOffset,
                (uint)block.Length,  // compressed size = uncompressed size (no compression)
                (uint)block.Length,
                0  // compression method = None
            ));

            blockOffset += finalBytes.Length;
        }

        // Create chunk meta with BLAKE3 hash
        byte[] hashBytes = hasher.Finalize().AsSpan().ToArray();
        var meta = new FIoStoreTocEntryMeta
        {
            ChunkHash = new FIoChunkHash(hashBytes),
            Flags = 0
        };

        // Add to TOC
        _chunks.Add(chunkId);
        _chunkOffsetLengths.Add(new FIoOffsetAndLength(
            (ulong)(startBlock * _compressionBlockSize),
            (ulong)data.Length
        ));
        _chunkMetas.Add(meta);
    }

    /// <summary>
    /// Complete and write the container.
    /// Reference: retoc-rivals/src/iostore_writer.rs finalize()
    /// </summary>
    public void Complete()
    {
        // Write container header chunk if needed
        // Container Header should NOT be compressed - write it directly without compression
        if (_containerHeaderVersion.HasValue && _packageStoreEntries.Count > 0)
        {
            byte[] containerHeaderData = BuildContainerHeader();
            // Align to 16 bytes for AES
            int alignedLength = (containerHeaderData.Length + 15) & ~15;
            byte[] aligned = new byte[alignedLength];
            Array.Copy(containerHeaderData, aligned, containerHeaderData.Length);

            var headerChunkId = FIoChunkId.Create(_containerId.Value, 0, EIoChunkType.ContainerHeader);
            WriteChunkUncompressed(headerChunkId, aligned);
        }

        // Build and write TOC
        WriteToc();

        // Flush streams to ensure all data is written to disk
        _tocStream.Flush();
        _casStream.Flush();

        Console.Error.WriteLine($"[IoStoreWriter] Created IoStore: {_tocPath}");
        Console.Error.WriteLine($"[IoStoreWriter]   Chunks: {_chunks.Count}");
        Console.Error.WriteLine($"[IoStoreWriter]   Packages: {_packageStoreEntries.Count}");
        Console.Error.WriteLine($"[IoStoreWriter]   TOC size: {_tocStream.Length} bytes");
        Console.Error.WriteLine($"[IoStoreWriter]   CAS size: {_casStream.Length} bytes");
    }

    /// <summary>
    /// Build container header data.
    /// Reference: retoc-rivals/src/container_header.rs FIoContainerHeader::ser()
    /// </summary>
    private byte[] BuildContainerHeader()
    {
        var header = new FIoContainerHeader(_containerId, _containerHeaderVersion!.Value);

        foreach (var (packageId, storeEntry) in _packageStoreEntries)
        {
            header.AddPackage(packageId, storeEntry);
        }

        return header.Serialize();
    }

    /// <summary>
    /// Write the TOC file.
    /// Reference: retoc-rivals/src/lib.rs Toc serialization
    /// </summary>
    private void WriteToc()
    {
        // Use leaveOpen: true to prevent BinaryWriter from disposing the stream
        using var writer = new BinaryWriter(_tocStream, System.Text.Encoding.UTF8, leaveOpen: true);

        // Use raw directory index if provided (recompression pass-through), otherwise build from scratch
        byte[] directoryIndexData = _rawDirectoryIndex ?? BuildDirectoryIndex();

        // Build compression method names list
        var compressionMethodsList = _compressionMethods.ToList();

        // Build container flags
        var containerFlags = EIoContainerFlags.Indexed;
        if (_compressionMethods.Count > 0)
            containerFlags |= EIoContainerFlags.Compressed;
        if (_enableEncryption)
            containerFlags |= EIoContainerFlags.Encrypted;

        // Build header
        var header = new FIoStoreTocHeader
        {
            Version = _tocVersion,
            TocEntryCount = (uint)_chunks.Count,
            TocCompressedBlockEntryCount = (uint)_compressionBlocks.Count,
            CompressionMethodNameCount = (uint)compressionMethodsList.Count,
            CompressionBlockSize = _compressionBlockSize,
            DirectoryIndexSize = (uint)directoryIndexData.Length,
            TocChunkPerfectHashSeedsCount = 0,
            ContainerId = _containerId,
            EncryptionKeyGuid = _encryptionKeyGuid,
            ContainerFlags = containerFlags,
            TocChunksWithoutPerfectHashCount = 0, // No overflow - chunks are indexed directly
        };

        // Write header
        header.Write(writer);

        // Write chunk IDs
        foreach (var chunk in _chunks)
        {
            writer.Write(chunk.ToBytes());
        }

        // Write chunk offset/lengths
        foreach (var offsetLength in _chunkOffsetLengths)
        {
            offsetLength.Write(writer);
        }

        // Write perfect hash seeds (none)
        // (skipped since TocChunkPerfectHashSeedsCount = 0)

        // Write chunks without perfect hash (none)
        // (skipped since TocChunksWithoutPerfectHashCount = 0)

        // Write compression blocks
        foreach (var block in _compressionBlocks)
        {
            block.Write(writer);
        }

        // Write compression method names (32 bytes each, null-padded)
        foreach (var methodName in compressionMethodsList)
        {
            byte[] nameBytes = new byte[32];
            byte[] srcBytes = Encoding.ASCII.GetBytes(methodName);
            Array.Copy(srcBytes, nameBytes, Math.Min(srcBytes.Length, 32));
            writer.Write(nameBytes);
        }

        // Write directory index (BEFORE chunk metas - per Rust implementation)
        writer.Write(directoryIndexData);

        // Write chunk metas (AFTER directory index)
        foreach (var meta in _chunkMetas)
        {
            meta.Write(writer);
        }
    }

    /// <summary>
    /// Build directory index matching Rust FIoDirectoryIndexResource format.
    /// Reference: retoc-rivals/src/lib.rs FIoDirectoryIndexResource
    /// 
    /// Format:
    /// - Mount point (length-prefixed string)
    /// - Directory entries array (length-prefixed)
    /// - File entries array (length-prefixed)
    /// - String table array (length-prefixed)
    /// 
    /// FIoDirectoryIndexEntry: name (u32), first_child_entry (u32), next_sibling_entry (u32), first_file_entry (u32)
    /// FIoFileIndexEntry: name (u32), next_file_entry (u32), user_data (u32)
    /// </summary>
    private byte[] BuildDirectoryIndex()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Build the tree structure
        var stringTable = new List<string>();
        var stringToIndex = new Dictionary<string, uint>();
        var directoryEntries = new List<(uint name, uint firstChild, uint nextSibling, uint firstFile)>();
        var fileEntries = new List<(uint name, uint nextFile, uint userData)>();

        uint GetOrAddString(string s)
        {
            if (stringToIndex.TryGetValue(s, out uint idx))
                return idx;
            idx = (uint)stringTable.Count;
            stringTable.Add(s);
            stringToIndex[s] = idx;
            return idx;
        }

        // Group files by directory path
        var dirToFiles = new Dictionary<string, List<(string fileName, uint chunkIndex)>>();
        foreach (var (path, chunkIndex) in _directoryIndex)
        {
            string dir = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";
            string file = Path.GetFileName(path);
            
            if (!dirToFiles.ContainsKey(dir))
                dirToFiles[dir] = new List<(string, uint)>();
            dirToFiles[dir].Add((file, chunkIndex));
        }

        // Create root directory entry (name = u32::MAX means no name)
        directoryEntries.Add((uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue));

        // Build directory tree - for simplicity, create flat structure with all dirs as children of root
        uint? firstChildDir = null;
        uint? lastDirIndex = null;

        foreach (var (dirPath, files) in dirToFiles)
        {
            // Create directory entry
            uint dirIndex = (uint)directoryEntries.Count;
            uint dirNameIndex = GetOrAddString(dirPath);
            
            // Create file entries for this directory
            uint? firstFileIndex = null;
            uint? lastFileIndex = null;
            
            foreach (var (fileName, chunkIndex) in files)
            {
                uint fileIndex = (uint)fileEntries.Count;
                uint fileNameIndex = GetOrAddString(fileName);
                
                fileEntries.Add((fileNameIndex, uint.MaxValue, chunkIndex));
                
                if (firstFileIndex == null)
                    firstFileIndex = fileIndex;
                
                // Link previous file to this one
                if (lastFileIndex != null)
                {
                    var prev = fileEntries[(int)lastFileIndex.Value];
                    fileEntries[(int)lastFileIndex.Value] = (prev.name, fileIndex, prev.userData);
                }
                lastFileIndex = fileIndex;
            }

            directoryEntries.Add((dirNameIndex, uint.MaxValue, uint.MaxValue, firstFileIndex ?? uint.MaxValue));

            // Link to previous sibling
            if (lastDirIndex != null)
            {
                var prev = directoryEntries[(int)lastDirIndex.Value];
                directoryEntries[(int)lastDirIndex.Value] = (prev.name, prev.firstChild, dirIndex, prev.firstFile);
            }

            if (firstChildDir == null)
                firstChildDir = dirIndex;

            lastDirIndex = dirIndex;
        }

        // Update root to point to first child directory
        if (firstChildDir != null)
        {
            var root = directoryEntries[0];
            directoryEntries[0] = (root.name, firstChildDir.Value, root.nextSibling, root.firstFile);
        }

        // Write mount point
        WriteString(writer, _mountPoint);

        // Write directory entries array
        writer.Write((uint)directoryEntries.Count);
        foreach (var (name, firstChild, nextSibling, firstFile) in directoryEntries)
        {
            writer.Write(name);
            writer.Write(firstChild);
            writer.Write(nextSibling);
            writer.Write(firstFile);
        }

        // Write file entries array
        writer.Write((uint)fileEntries.Count);
        foreach (var (name, nextFile, userData) in fileEntries)
        {
            writer.Write(name);
            writer.Write(nextFile);
            writer.Write(userData);
        }

        // Write string table
        writer.Write((uint)stringTable.Count);
        foreach (var s in stringTable)
        {
            WriteString(writer, s);
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Write length-prefixed string with null terminator.
    /// </summary>
    private static void WriteString(BinaryWriter writer, string str)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(str + "\0");
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    /// <summary>
    /// Split data into chunks.
    /// </summary>
    private static IEnumerable<byte[]> ChunkData(byte[] data, int chunkSize)
    {
        for (int i = 0; i < data.Length; i += chunkSize)
        {
            int size = Math.Min(chunkSize, data.Length - i);
            byte[] chunk = new byte[size];
            Array.Copy(data, i, chunk, 0, size);
            yield return chunk;
        }
    }

    /// <summary>
    /// Encrypt data using AES-256-ECB.
    /// IoStore uses standard AES-256-ECB without the 4-byte chunk reversal that PAK uses.
    /// </summary>
    private byte[] EncryptAes(byte[] data)
    {
        if (_aesKey == null)
            return data;

        using var aes = Aes.Create();
        aes.Key = _aesKey;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(data, 0, data.Length);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _tocStream?.Dispose();
            _casStream?.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
