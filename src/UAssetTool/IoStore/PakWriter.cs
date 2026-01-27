using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Blake3;

namespace UAssetTool.IoStore;

/// <summary>
/// PAK V11 writer implementation based on repak source code.
/// Creates encrypted PAK files compatible with Marvel Rivals.
/// 
/// Reference: repak/src/pak.rs, repak/src/entry.rs, repak/src/data.rs, repak/src/footer.rs
/// </summary>
public class PakWriter : IDisposable
{
    // Constants from repak/src/lib.rs
    private const uint PAK_MAGIC = 0x5A6F12E1;
    private const int PAK_VERSION = 11; // Fnv64BugFix
    private const string DEFAULT_AES_KEY_HEX = "0C263D8C22DCB085894899C3A3796383E9BF9DE0CBFB08C9BF2DEF2E84F29D74";

    // 35-byte magic block before footer (from repak/src/pak.rs line 708-712)
    private static readonly byte[] MAGIC_BLOCK = new byte[]
    {
        0x06, 0x12, 0x24, 0x20, 0x06, 0x00, 0x00, 0x00, 0x01, 0x02, 0x00, 0x10, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    };

    private readonly string _mountPoint;
    private readonly ulong _pathHashSeed;
    private readonly byte[] _aesKey;
    private readonly List<PakEntry> _entries = new();
    private readonly MemoryStream _dataStream = new();
    private bool _disposed;

    public PakWriter(string mountPoint = "../../../", ulong pathHashSeed = 0, string? aesKeyHex = null)
    {
        _mountPoint = mountPoint;
        _pathHashSeed = pathHashSeed;
        _aesKey = ParseAesKey(aesKeyHex ?? DEFAULT_AES_KEY_HEX);
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
    /// Add an entry to the PAK file.
    /// Implements repak's build_partial_entry() and write_entry() flow.
    /// </summary>
    public void AddEntry(string path, byte[] data)
    {
        // Compute SHA1 hash of ORIGINAL data (before any modification)
        byte[] dataHash = SHA1.HashData(data);

        // Pad data to 16-byte alignment for encryption
        byte[] paddedData = PadToAlignment(data, 16);

        // Normalize path to match how it will be stored in the index
        // TrySplitPathChild adds "/" prefix for paths without directory
        string normalizedPath = path;
        if (!path.StartsWith("/"))
        {
            normalizedPath = "/" + path;
        }

        // Calculate encryption limit based on path hash (repak's get_limit function from data.rs)
        int limit = GetEncryptionLimit(normalizedPath);
        if (limit > paddedData.Length)
        {
            limit = paddedData.Length;
        }
        // Align limit to 16 bytes
        limit = (limit + 15) & ~15;
        if (limit > paddedData.Length)
            limit = paddedData.Length;

        // Partially encrypt the data (only up to limit) - from repak/src/data.rs line 271-278
        byte[] partiallyEncrypted = new byte[paddedData.Length];
        Array.Copy(paddedData, partiallyEncrypted, paddedData.Length);
        if (limit > 0)
        {
            byte[] toEncrypt = new byte[limit];
            Array.Copy(paddedData, toEncrypt, limit);
            byte[] encrypted = AesEncrypt(toEncrypt, _aesKey);
            Array.Copy(encrypted, partiallyEncrypted, limit);
        }

        // Record the entry's position in the data stream
        long entryOffset = _dataStream.Position;

        var entry = new PakEntry
        {
            Path = path,
            Offset = (ulong)entryOffset,
            UncompressedSize = (ulong)data.Length,
            CompressedSize = (ulong)partiallyEncrypted.Length,
            CompressionSlot = null, // No compression
            IsEncrypted = true,
            Hash = dataHash
        };

        // Write entry record (FPakEntry) before data - from repak/src/entry.rs Entry::write()
        // For V11 UNCOMPRESSED: offset(8) + compressed(8) + uncompressed(8) + compression(4) + hash(20) + flags(1) + blocksize(4) = 53 bytes
        // Note: blocks count is NOT written when compression is None (no blocks)
        using var entryMs = new MemoryStream();
        using var entryWriter = new BinaryWriter(entryMs);

        entryWriter.Write(0UL); // Offset - 0 for EntryLocation::Data (entry.rs line 162-165)
        entryWriter.Write(entry.CompressedSize); // Compressed size (padded for encryption)
        entryWriter.Write(entry.UncompressedSize); // Uncompressed size (original)
        entryWriter.Write(0u); // Compression method (0 = None) - u32 for V11 (entry.rs line 169-172)
        entryWriter.Write(dataHash); // SHA1 hash of ORIGINAL data (entry.rs line 177-181)
        // NO blocks count for uncompressed data - blocks only written if Some (entry.rs line 183-188)
        entryWriter.Write((byte)(entry.IsEncrypted ? 1 : 0)); // Flags (1 = encrypted) (entry.rs line 189)
        entryWriter.Write(0u); // Compression block size (0 for uncompressed) (entry.rs line 190)

        byte[] entryRecord = entryMs.ToArray();
        _dataStream.Write(entryRecord);

        // Write partially encrypted data after entry record
        _dataStream.Write(partiallyEncrypted);

        // Update entry offset to account for entry record size
        entry.Offset = (ulong)entryOffset;

        _entries.Add(entry);
    }

    /// <summary>
    /// Calculate encryption limit based on path hash.
    /// Implements repak's get_limit() from data.rs lines 10-23 using BLAKE3.
    /// </summary>
    private static int GetEncryptionLimit(string path)
    {
        // From repak/src/data.rs get_limit():
        // let mut hasher = blake3::Hasher::new();
        // hasher.update(&[0x11, 0x22, 0x33, 0x44]);
        // hasher.update(path.to_ascii_lowercase().as_bytes());
        using var hasher = Hasher.New();
        hasher.Update(new byte[] { 0x11, 0x22, 0x33, 0x44 });
        hasher.Update(Encoding.ASCII.GetBytes(path.ToLowerInvariant()));

        byte[] hashBytes = hasher.Finalize().AsSpan().ToArray();
        ulong hashValue = BitConverter.ToUInt64(hashBytes, 0);

        // ((hash % 0x3d) * 63 + 319) & 0xffffffffffffffc0
        long limit = (long)(((hashValue % 0x3d) * 63 + 319) & 0xffffffffffffffc0);
        if (limit == 0)
            limit = 0x1000;

        return (int)limit;
    }

    /// <summary>
    /// Write the PAK file to the specified path.
    /// Implements repak's Pak::write() from pak.rs lines 537-717.
    /// </summary>
    public void Write(string outputPath)
    {
        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(fs);

        // 1. Write data section
        byte[] dataBytes = _dataStream.ToArray();
        writer.Write(dataBytes);

        long indexOffset = fs.Position;

        // 2. Build index buffer (pak.rs line 544-670)
        using var indexMs = new MemoryStream();
        using var indexWriter = new BinaryWriter(indexMs);

        // Mount point (length-prefixed string with null terminator)
        WriteString(indexWriter, _mountPoint);

        // Record count
        indexWriter.Write((uint)_entries.Count);

        // Path hash seed (V10+)
        indexWriter.Write(_pathHashSeed);

        // Build encoded entries (pak.rs line 566-574)
        var encodedEntries = new MemoryStream();
        var offsets = new List<uint>();
        foreach (var entry in _entries)
        {
            offsets.Add((uint)encodedEntries.Position);
            WriteEncodedEntry(encodedEntries, entry);
        }
        byte[] encodedEntriesBytes = encodedEntries.ToArray();

        // Calculate bytes before path hash index (pak.rs line 597-615)
        long bytesBeforePhi = CalculateBytesBeforePhi(encodedEntriesBytes.Length);

        // Pad to 16-byte alignment for encryption
        bytesBeforePhi = PadLength(bytesBeforePhi, 16);

        long pathHashIndexOffset = indexOffset + bytesBeforePhi;

        // Build path hash index (pak.rs line 619-636)
        byte[] phiBytes = BuildPathHashIndex(offsets);
        phiBytes = PadToAlignment(phiBytes, 16);
        byte[] phiHash = SHA1.HashData(phiBytes); // Hash BEFORE encryption (pak.rs line 632)
        byte[] encryptedPhi = AesEncrypt(phiBytes, _aesKey);

        long fullDirectoryIndexOffset = pathHashIndexOffset + encryptedPhi.Length;

        // Build full directory index (pak.rs line 640-652)
        byte[] fdiBytes = BuildFullDirectoryIndex(offsets);
        fdiBytes = PadToAlignment(fdiBytes, 16);
        byte[] fdiHash = SHA1.HashData(fdiBytes); // Hash BEFORE encryption (pak.rs line 648)
        byte[] encryptedFdi = AesEncrypt(fdiBytes, _aesKey);

        // Write path hash index info (pak.rs line 654-657)
        indexWriter.Write(1u); // has path hash index
        indexWriter.Write(pathHashIndexOffset);
        indexWriter.Write((ulong)encryptedPhi.Length);
        indexWriter.Write(phiHash);

        // Write full directory index info (pak.rs line 659-662)
        indexWriter.Write(1u); // has full directory index
        indexWriter.Write(fullDirectoryIndexOffset);
        indexWriter.Write((ulong)encryptedFdi.Length);
        indexWriter.Write(fdiHash);

        // Write encoded entries (pak.rs line 664-665)
        indexWriter.Write((uint)encodedEntriesBytes.Length);
        indexWriter.Write(encodedEntriesBytes);

        // Write unused file count (pak.rs line 667)
        indexWriter.Write(0u);

        // Get index bytes, pad, hash BEFORE encryption, then encrypt (pak.rs line 685-699)
        byte[] indexBytes = indexMs.ToArray();
        indexBytes = PadToAlignment(indexBytes, 16);
        byte[] indexHash = SHA1.HashData(indexBytes); // Hash BEFORE encryption!
        byte[] encryptedIndex = AesEncrypt(indexBytes, _aesKey);

        // 3. Write encrypted index (pak.rs line 701)
        writer.Write(encryptedIndex);

        // 4. Write path hash index (pak.rs line 704)
        writer.Write(encryptedPhi);

        // 5. Write full directory index (pak.rs line 705)
        writer.Write(encryptedFdi);

        // 6. Write the 35-byte magic block before footer (pak.rs line 708-712)
        writer.Write(MAGIC_BLOCK);

        // 7. Write footer (pak.rs line 714)
        WriteFooter(writer, indexOffset, (ulong)encryptedIndex.Length, indexHash);

        Console.Error.WriteLine($"[PakWriter] Created PAK: {outputPath}");
        Console.Error.WriteLine($"[PakWriter]   Entries: {_entries.Count}");
        Console.Error.WriteLine($"[PakWriter]   Size: {fs.Length} bytes");
    }

    /// <summary>
    /// Calculate bytes before path hash index (from pak.rs line 597-615)
    /// </summary>
    private long CalculateBytesBeforePhi(int encodedEntriesLength)
    {
        long size = 0;
        size += 4; // mount point length
        size += _mountPoint.Length + 1; // mount point string with null
        size += 4; // record count
        size += 8; // path hash seed
        size += 4; // has path hash index
        size += 8 + 8 + 20; // path hash index offset, size, hash
        size += 4; // has full directory index
        size += 8 + 8 + 20; // full directory index offset, size, hash
        size += 4; // encoded entries size
        size += encodedEntriesLength;
        size += 4; // unused file count
        return size;
    }

    /// <summary>
    /// Build path hash index (from pak.rs generate_path_hash_index lines 727-743)
    /// </summary>
    private byte[] BuildPathHashIndex(List<uint> offsets)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((uint)_entries.Count);
        for (int i = 0; i < _entries.Count; i++)
        {
            ulong pathHash = Fnv64Path(_entries[i].Path, _pathHashSeed);
            writer.Write(pathHash);
            writer.Write(offsets[i]);
        }
        writer.Write(0u); // terminator

        return ms.ToArray();
    }

    /// <summary>
    /// Build full directory index (from pak.rs generate_full_directory_index lines 778-807)
    /// </summary>
    private byte[] BuildFullDirectoryIndex(List<uint> offsets)
    {
        // Build directory structure
        var fdi = new SortedDictionary<string, SortedDictionary<string, uint>>();

        for (int i = 0; i < _entries.Count; i++)
        {
            string path = _entries[i].Path;

            // Add all parent directories
            string p = path;
            while (TrySplitPathChild(p, out string? parent, out _))
            {
                p = parent!;
                if (!fdi.ContainsKey(p))
                    fdi[p] = new SortedDictionary<string, uint>();
            }

            // Add file to its directory
            if (TrySplitPathChild(path, out string? directory, out string? filename))
            {
                if (!fdi.ContainsKey(directory!))
                    fdi[directory!] = new SortedDictionary<string, uint>();
                fdi[directory!][filename!] = offsets[i];
            }
        }

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((uint)fdi.Count);
        foreach (var (directory, files) in fdi)
        {
            WriteString(writer, directory);
            writer.Write((uint)files.Count);
            foreach (var (filename, offset) in files)
            {
                WriteString(writer, filename);
                writer.Write(offset);
            }
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Split path into parent directory and child (from pak.rs split_path_child lines 765-776)
    /// </summary>
    private static bool TrySplitPathChild(string path, out string? parent, out string? child)
    {
        parent = null;
        child = null;

        if (path == "/" || string.IsNullOrEmpty(path))
            return false;

        path = path.TrimEnd('/');
        int i = path.LastIndexOf('/');
        if (i >= 0)
        {
            parent = path.Substring(0, i + 1);
            child = path.Substring(i + 1);
        }
        else
        {
            parent = "/";
            child = path;
        }
        return true;
    }

    /// <summary>
    /// Write encoded entry (from entry.rs write_encoded lines 273-331)
    /// </summary>
    private static void WriteEncodedEntry(MemoryStream stream, PakEntry entry)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        uint compressionBlockSize = 0;
        uint compressionBlocksCount = 0;
        bool isSizeSafe = entry.CompressedSize <= uint.MaxValue;
        bool isUncompressedSizeSafe = entry.UncompressedSize <= uint.MaxValue;
        bool isOffsetSafe = entry.Offset <= uint.MaxValue;

        // Build flags (entry.rs line 287-293)
        uint flags = compressionBlockSize
            | (compressionBlocksCount << 6)
            | ((entry.IsEncrypted ? 1u : 0u) << 22)
            | ((entry.CompressionSlot.HasValue ? (uint)(entry.CompressionSlot.Value + 1) : 0u) << 23)
            | ((isSizeSafe ? 1u : 0u) << 29)
            | ((isUncompressedSizeSafe ? 1u : 0u) << 30)
            | ((isOffsetSafe ? 1u : 0u) << 31);

        writer.Write(flags);

        // Offset (entry.rs line 301-305)
        if (isOffsetSafe)
            writer.Write((uint)entry.Offset);
        else
            writer.Write(entry.Offset);

        // Uncompressed size (entry.rs line 307-311)
        if (isUncompressedSizeSafe)
            writer.Write((uint)entry.UncompressedSize);
        else
            writer.Write(entry.UncompressedSize);

        // No compression blocks since we're not compressing (entry.rs line 313-328)
    }

    /// <summary>
    /// Write footer (from footer.rs Footer::write lines 86-117)
    /// Footer is 221 bytes for V11
    /// </summary>
    private static void WriteFooter(BinaryWriter writer, long indexOffset, ulong indexSize, byte[] indexHash)
    {
        // Encryption key GUID (16 bytes) - all zeros for default key (footer.rs line 87-89)
        writer.Write(new byte[16]);

        // Is index encrypted (1 byte) (footer.rs line 90-92)
        writer.Write((byte)1);

        // Magic (4 bytes) (footer.rs line 93)
        writer.Write(PAK_MAGIC);

        // Version (4 bytes) (footer.rs line 94)
        writer.Write(PAK_VERSION);

        // Index offset (8 bytes) (footer.rs line 95)
        writer.Write(indexOffset);

        // Index size (8 bytes) (footer.rs line 96)
        writer.Write(indexSize);

        // Index hash (20 bytes) (footer.rs line 97)
        writer.Write(indexHash);

        // Compression methods (5 * 32 = 160 bytes for V8B+) (footer.rs line 101-115)
        for (int i = 0; i < 5; i++)
        {
            writer.Write(new byte[32]);
        }
    }

    /// <summary>
    /// Write length-prefixed string with null terminator (from ext.rs write_string lines 98-112)
    /// </summary>
    private static void WriteString(BinaryWriter writer, string str)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(str + "\0");
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    /// <summary>
    /// FNV64 hash for path (from pak.rs fnv64_path lines 759-763)
    /// CRITICAL: Uses UTF-16 LE encoding!
    /// </summary>
    private static ulong Fnv64Path(string path, ulong offset)
    {
        string lower = path.ToLowerInvariant();
        byte[] data = Encoding.Unicode.GetBytes(lower); // UTF-16 LE
        return Fnv64(data, offset);
    }

    /// <summary>
    /// FNV64 hash (from pak.rs fnv64 lines 745-757)
    /// </summary>
    private static ulong Fnv64(byte[] data, ulong offset)
    {
        const ulong FNV_OFFSET = 0xcbf29ce484222325;
        const ulong FNV_PRIME = 0x00000100000001b3;

        ulong hash = FNV_OFFSET + offset; // wrapping_add
        foreach (byte b in data)
        {
            hash ^= b;
            hash *= FNV_PRIME; // wrapping_mul
        }
        return hash;
    }

    /// <summary>
    /// AES encrypt with UE4's custom byte-swapping pattern (from data.rs encrypt lines 34-41)
    /// Each 16-byte block has its 4-byte chunks reversed before AND after encryption.
    /// </summary>
    private static byte[] AesEncrypt(byte[] data, byte[] key)
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

            // Reverse each 4-byte chunk BEFORE encryption (data.rs line 37)
            ReverseChunks(block);

            // Encrypt the block
            using var encryptor = aes.CreateEncryptor();
            byte[] encrypted = encryptor.TransformFinalBlock(block, 0, 16);

            // Reverse each 4-byte chunk AFTER encryption (data.rs line 39)
            ReverseChunks(encrypted);

            Array.Copy(encrypted, 0, result, i, 16);
        }

        return result;
    }

    /// <summary>
    /// Reverse each 4-byte chunk in a 16-byte block (from data.rs line 37, 39)
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
    /// Pad byte array to alignment
    /// </summary>
    private static byte[] PadToAlignment(byte[] data, int alignment)
    {
        int paddedLength = (data.Length + alignment - 1) / alignment * alignment;
        if (paddedLength == data.Length)
            return data;

        byte[] padded = new byte[paddedLength];
        Array.Copy(data, padded, data.Length);
        return padded;
    }

    /// <summary>
    /// Pad length to alignment
    /// </summary>
    private static long PadLength(long length, int alignment)
    {
        return (length + alignment - 1) / alignment * alignment;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _dataStream.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    private class PakEntry
    {
        public string Path { get; set; } = "";
        public ulong Offset { get; set; }
        public ulong CompressedSize { get; set; }
        public ulong UncompressedSize { get; set; }
        public int? CompressionSlot { get; set; }
        public bool IsEncrypted { get; set; }
        public byte[] Hash { get; set; } = Array.Empty<byte>();
    }
}
