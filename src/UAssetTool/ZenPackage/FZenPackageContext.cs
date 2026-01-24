using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using UAssetTool.IoStore;

namespace UAssetTool.ZenPackage;

/// <summary>
/// Context for loading and caching Zen packages from IoStore containers.
/// Provides package lookup and cross-package import resolution.
/// Ported from retoc-rivals/src/lib.rs FZenPackageContext
/// </summary>
public class FZenPackageContext : IDisposable
{
    private readonly List<IoStoreReader> _containers = new();
    private readonly ConcurrentDictionary<ulong, CachedPackage> _packageCache = new();
    private readonly Dictionary<ulong, (int ContainerIndex, FIoChunkId ChunkId)> _packageIdToChunk = new();
    private readonly Dictionary<ulong, string> _packageIdToPath = new(); // PackageId -> full package path
    private readonly Dictionary<ulong, List<ulong>> _packageExportHashes = new(); // PackageId -> list of public export hashes
    private readonly Dictionary<int, List<string>> _containerNameMaps = new(); // ContainerIndex -> global name map
    
    public ScriptObjectsDatabase? ScriptObjects { get; private set; }
    public EIoContainerHeaderVersion ContainerHeaderVersion { get; private set; } = EIoContainerHeaderVersion.NoExportInfo;
    
    public int PackageCount => _packageIdToChunk.Count;
    public int ContainerCount => _containers.Count;

    private byte[]? _aesKey;
    
    /// <summary>
    /// Set the AES key for decrypting encrypted containers
    /// </summary>
    public void SetAesKey(string hexKey)
    {
        if (string.IsNullOrEmpty(hexKey))
            return;
        _aesKey = Convert.FromHexString(hexKey);
    }
    
    /// <summary>
    /// Load an IoStore container and index all packages
    /// </summary>
    public void LoadContainer(string utocPath)
    {
        LoadContainerInternal(utocPath, overridePriority: false);
    }
    
    /// <summary>
    /// Load an IoStore container with priority - packages in this container will override
    /// any previously loaded packages with the same ID. Used for mod containers.
    /// Mod containers are loaded WITHOUT encryption (no AES key).
    /// </summary>
    public void LoadContainerWithPriority(string utocPath)
    {
        LoadContainerInternal(utocPath, overridePriority: true, useEncryption: false);
    }
    
    private void LoadContainerInternal(string utocPath, bool overridePriority, bool useEncryption = true)
    {
        var reader = new IoStoreReader(utocPath, useEncryption ? _aesKey : null);
        int containerIndex = _containers.Count;
        _containers.Add(reader);
        
        // Determine container version from TOC version
        if (reader.Toc.Version >= EIoStoreTocVersion.PerfectHashWithOverflow)
            ContainerHeaderVersion = EIoContainerHeaderVersion.NoExportInfo;
        else if (reader.Toc.Version >= EIoStoreTocVersion.PartitionSize)
            ContainerHeaderVersion = EIoContainerHeaderVersion.LocalizedPackages;
        else
            ContainerHeaderVersion = EIoContainerHeaderVersion.Initial;
        
        if (Environment.GetEnvironmentVariable("DEBUG") == "1")
            Console.WriteLine($"[Context] TOC version: {reader.Toc.Version}, Container header version: {ContainerHeaderVersion}");
        
        // Read ContainerHeader chunk to get global name map (for versions > Initial)
        if (ContainerHeaderVersion > EIoContainerHeaderVersion.Initial)
        {
            foreach (var chunk in reader.GetChunks())
            {
                if (chunk.GetChunkType() == EIoChunkType.ContainerHeader)
                {
                    try
                    {
                        byte[] headerData = reader.ReadChunk(chunk);
                        var nameMap = ParseContainerHeaderNameMap(headerData);
                        if (nameMap.Count > 0)
                        {
                            _containerNameMaps[containerIndex] = nameMap;
                            if (Environment.GetEnvironmentVariable("DEBUG") == "1")
                                Console.WriteLine($"[Context] Loaded {nameMap.Count} names from ContainerHeader");
                        }
                    }
                    catch (Exception ex)
                    {
                        if (Environment.GetEnvironmentVariable("DEBUG") == "1")
                            Console.WriteLine($"[Context] Failed to parse ContainerHeader: {ex.Message}");
                    }
                    break;
                }
            }
        }
        
        int newPackages = 0;
        int overriddenPackages = 0;
        
        // Index all ExportBundleData chunks (actual packages)
        foreach (var chunk in reader.GetChunks())
        {
            if (chunk.GetChunkType() != EIoChunkType.ExportBundleData)
                continue;
            
            ulong packageId = chunk.Id;
            
            bool exists = _packageIdToChunk.ContainsKey(packageId);
            
            // Add package if it doesn't exist, or override if priority mode
            if (!exists || overridePriority)
            {
                if (exists && overridePriority)
                {
                    // Clear cached data for overridden package
                    _packageCache.TryRemove(packageId, out _);
                    overriddenPackages++;
                }
                else
                {
                    newPackages++;
                }
                
                _packageIdToChunk[packageId] = (containerIndex, chunk);
                
                // Get full package path from TOC directory index
                string? chunkPath = reader.GetChunkPath(chunk);
                if (!string.IsNullOrEmpty(chunkPath))
                {
                    // Convert file path to package path (remove extension, convert to /Game/... format)
                    string packagePath = ConvertFilePathToPackagePath(chunkPath);
                    _packageIdToPath[packageId] = packagePath;
                }
            }
        }
        
        string priorityStr = overridePriority ? " [PRIORITY]" : "";
        string overrideStr = overriddenPackages > 0 ? $", {overriddenPackages} overridden" : "";
        Console.WriteLine($"[Context] Loaded container{priorityStr}: {reader.ContainerName} ({newPackages} new packages{overrideStr}, {_packageIdToChunk.Count} total)");
    }
    
    /// <summary>
    /// Parse the name map from a ContainerHeader chunk
    /// Based on retoc-rivals FIoContainerHeader deserialization
    /// </summary>
    private static List<string> ParseContainerHeaderNameMap(byte[] data)
    {
        var names = new List<string>();
        
        // The name map in ContainerHeader uses hash_version = 0xC1640000
        // Search for this signature: count(4) + num_string_bytes(4) + hash_version(8)
        const ulong NAME_HASH_ALGORITHM_ID = 0x00000000C1640000UL; // Little endian
        byte[] signature = BitConverter.GetBytes(NAME_HASH_ALGORITHM_ID);
        
        // Search for the signature in the data
        int signaturePos = -1;
        for (int i = 8; i < data.Length - 8; i++)
        {
            if (data[i] == signature[0] && data[i + 1] == signature[1] &&
                data[i + 2] == signature[2] && data[i + 3] == signature[3] &&
                data[i + 4] == signature[4] && data[i + 5] == signature[5] &&
                data[i + 6] == signature[6] && data[i + 7] == signature[7])
            {
                signaturePos = i;
                break;
            }
        }
        
        if (signaturePos < 8)
            return names;
        
        // Read numNames and numStringBytes from before the signature
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);
        
        reader.BaseStream.Seek(signaturePos - 8, SeekOrigin.Begin);
        int numNames = reader.ReadInt32();
        int numStringBytes = reader.ReadInt32();
        ulong hashVersion = reader.ReadUInt64();
        
        if (numNames <= 0 || numNames > 100000 || hashVersion != NAME_HASH_ALGORITHM_ID)
            return names;
        
        // Skip hashes
        reader.BaseStream.Seek(numNames * 8, SeekOrigin.Current);
        
        // Read length headers (big endian i16)
        var lengths = new List<(int len, bool isWide)>();
        for (int i = 0; i < numNames; i++)
        {
            byte hi = reader.ReadByte();
            byte lo = reader.ReadByte();
            short len = (short)((hi << 8) | lo);
            bool isWide = len < 0;
            // For wide strings: charCount = |short.MinValue - len|
            // Encoding: len = charCount + short.MinValue (e.g., 5 chars -> 5 + (-32768) = -32763)
            int actualLen = isWide ? Math.Abs(short.MinValue - len) : len;
            lengths.Add((actualLen, isWide));
        }
        
        // Read string data - strings are stored consecutively WITHOUT alignment padding
        byte[] stringData = reader.ReadBytes(numStringBytes);
        
        // Parse strings - NO alignment padding in serialized format
        int currentOffset = 0;
        for (int i = 0; i < numNames; i++)
        {
            var (len, isWide) = lengths[i];
            if (isWide)
            {
                int byteLen = len * 2;
                if (currentOffset + byteLen <= stringData.Length)
                {
                    names.Add(System.Text.Encoding.Unicode.GetString(stringData, currentOffset, byteLen));
                    currentOffset += byteLen;
                }
                else
                {
                    names.Add($"__invalid_wide_{i}__");
                }
            }
            else
            {
                if (currentOffset + len <= stringData.Length)
                {
                    names.Add(System.Text.Encoding.UTF8.GetString(stringData, currentOffset, len));
                    currentOffset += len;
                }
                else
                {
                    names.Add($"__invalid_{i}__");
                }
            }
        }
        
        return names;
    }
    
    /// <summary>
    /// Get the global name map for a container
    /// </summary>
    public List<string>? GetContainerNameMap(int containerIndex)
    {
        return _containerNameMaps.TryGetValue(containerIndex, out var nameMap) ? nameMap : null;
    }
    
    /// <summary>
    /// Convert a file path like "../../../Marvel/Content/..." to package path like "/Game/Marvel/..."
    /// </summary>
    private static string ConvertFilePathToPackagePath(string filePath)
    {
        // Remove extension (.uasset, .uexp, etc.)
        string path = filePath;
        if (path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
            path = path[..^7];
        else if (path.EndsWith(".uexp", StringComparison.OrdinalIgnoreCase))
            path = path[..^5];
        else if (path.EndsWith(".ubulk", StringComparison.OrdinalIgnoreCase))
            path = path[..^6];
        
        // Normalize path separators
        path = path.Replace('\\', '/');
        
        // Remove mount point prefix (e.g., "../../../")
        while (path.StartsWith("../"))
            path = path[3..];
        
        // Handle common UE content paths
        // Marvel/Content/... -> /Game/...
        // The content after "/Content/" is the actual package path
        if (path.Contains("/Content/"))
        {
            int contentIdx = path.IndexOf("/Content/");
            string afterContent = path[(contentIdx + 9)..]; // Skip "/Content/"
            path = $"/Game/{afterContent}";
        }
        else if (!path.StartsWith("/"))
        {
            path = "/Game/" + path;
        }
        
        return path;
    }

    /// <summary>
    /// Load script objects database for import resolution
    /// </summary>
    public void LoadScriptObjects(string scriptObjectsPath)
    {
        if (File.Exists(scriptObjectsPath))
        {
            ScriptObjects = ScriptObjectsDatabase.Load(scriptObjectsPath);
            Console.WriteLine($"[Context] Loaded {ScriptObjects.Count} script objects");
        }
    }

    /// <summary>
    /// Load script objects from a container's global chunk
    /// </summary>
    public void LoadScriptObjectsFromContainer(int containerIndex = 0)
    {
        if (containerIndex >= _containers.Count)
            return;
        
        var reader = _containers[containerIndex];
        
        // Look for ScriptObjects chunk
        foreach (var chunk in reader.GetChunks())
        {
            if (chunk.GetChunkType() == EIoChunkType.ScriptObjects)
            {
                try
                {
                    byte[] data = reader.ReadChunk(chunk);
                    ScriptObjects = ScriptObjectsDatabase.Load(data);
                    Console.WriteLine($"[Context] Loaded {ScriptObjects.Count} script objects from container");
                    return;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Context] Failed to load script objects: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Get a package by its ID (lazy loading with caching)
    /// </summary>
    public FZenPackageHeader? GetPackage(ulong packageId)
    {
        // Check cache first
        if (_packageCache.TryGetValue(packageId, out var cached))
        {
            return cached.Header;
        }
        
        // Find and load the package
        if (!_packageIdToChunk.TryGetValue(packageId, out var location))
        {
            return null;
        }
        
        try
        {
            var reader = _containers[location.ContainerIndex];
            byte[] rawData = reader.ReadChunk(location.ChunkId);
            
            var header = FZenPackageHeader.Deserialize(rawData, ContainerHeaderVersion);
            
            // Cache the package
            var cachedPackage = new CachedPackage
            {
                Header = header,
                RawData = rawData,
                PackageId = packageId
            };
            _packageCache[packageId] = cachedPackage;
            
            // Index public export hashes for this package
            IndexPackageExports(packageId, header);
            
            return header;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Context] Failed to load package {packageId:X16}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Get cached package with raw data
    /// </summary>
    public CachedPackage? GetCachedPackage(ulong packageId)
    {
        // Ensure it's loaded
        GetPackage(packageId);
        
        _packageCache.TryGetValue(packageId, out var cached);
        return cached;
    }

    /// <summary>
    /// Get package by path (computes package ID from path)
    /// </summary>
    public FZenPackageHeader? GetPackageByPath(string packagePath)
    {
        ulong packageId = FPackageId.FromName(packagePath);
        return GetPackage(packageId);
    }

    /// <summary>
    /// Check if a package exists
    /// </summary>
    public bool HasPackage(ulong packageId)
    {
        return _packageIdToChunk.ContainsKey(packageId);
    }
    
    /// <summary>
    /// Get the full package path for a package ID
    /// </summary>
    public string? GetPackagePath(ulong packageId)
    {
        return _packageIdToPath.TryGetValue(packageId, out string? path) ? path : null;
    }

    /// <summary>
    /// Find package ID by searching indexed paths (case-insensitive partial match)
    /// </summary>
    public ulong? FindPackageIdByPath(string searchPath)
    {
        string searchLower = searchPath.ToLowerInvariant();
        foreach (var kvp in _packageIdToPath)
        {
            if (kvp.Value.ToLowerInvariant().Contains(searchLower))
            {
                return kvp.Key;
            }
        }
        return null;
    }

    /// <summary>
    /// Get all package IDs
    /// </summary>
    public IEnumerable<ulong> GetAllPackageIds()
    {
        return _packageIdToChunk.Keys;
    }
    
    /// <summary>
    /// Get package IDs from a specific container (by index)
    /// </summary>
    public IEnumerable<ulong> GetPackageIdsFromContainer(int containerIndex)
    {
        return _packageIdToChunk
            .Where(kvp => kvp.Value.ContainerIndex == containerIndex)
            .Select(kvp => kvp.Key);
    }
    
    /// <summary>
    /// Get the index of the last loaded container (typically the mod container)
    /// </summary>
    public int LastContainerIndex => _containers.Count - 1;
    
    /// <summary>
    /// Read bulk data chunk for a package (combines all BulkData chunks with different indices)
    /// </summary>
    public byte[]? ReadBulkData(ulong packageId)
    {
        // Find which container has this package
        if (!_packageIdToChunk.TryGetValue(packageId, out var location))
            return null;
            
        var reader = _containers[location.ContainerIndex];
        
        // Collect all BulkData chunks for this package (may have multiple with different indices)
        var bulkChunks = new List<(ushort Index, byte[] Data)>();
        
        foreach (var chunk in reader.GetChunks())
        {
            // Check if this is a BulkData chunk for our package
            if (chunk.GetChunkType() == IoStore.EIoChunkType.BulkData && chunk.Id == packageId)
            {
                try
                {
                    byte[] data = reader.ReadChunk(chunk);
                    bulkChunks.Add((chunk.Index, data));
                }
                catch
                {
                    // Skip failed chunks
                }
            }
        }
        
        if (bulkChunks.Count == 0)
            return null;
            
        // Sort by index and concatenate
        bulkChunks.Sort((a, b) => a.Index.CompareTo(b.Index));
        
        if (bulkChunks.Count == 1)
            return bulkChunks[0].Data;
            
        // Concatenate all chunks
        int totalSize = bulkChunks.Sum(c => c.Data.Length);
        byte[] result = new byte[totalSize];
        int offset = 0;
        foreach (var (_, data) in bulkChunks)
        {
            Array.Copy(data, 0, result, offset, data.Length);
            offset += data.Length;
        }
        
        Console.WriteLine($"[Context] Combined {bulkChunks.Count} BulkData chunks into {totalSize} bytes");
        return result;
    }

    /// <summary>
    /// Resolve a package import to the actual export in the referenced package
    /// </summary>
    public (FZenPackageHeader? Package, FExportMapEntry? Export) ResolvePackageImport(
        FZenPackageHeader sourcePackage, 
        FPackageObjectIndex importIndex)
    {
        if (!importIndex.IsPackageImport())
            return (null, null);
        
        var (packageIndex, exportHashIndex) = importIndex.GetPackageImport();
        
        // Get the imported package ID
        if (packageIndex >= sourcePackage.ImportedPackages.Count)
            return (null, null);
        
        ulong importedPackageId = sourcePackage.ImportedPackages[packageIndex];
        
        // Load the target package
        var targetPackage = GetPackage(importedPackageId);
        if (targetPackage == null)
            return (null, null);
        
        // Find the export by its public hash
        if (exportHashIndex >= sourcePackage.ImportedPublicExportHashes.Count)
            return (targetPackage, null);
        
        ulong exportHash = sourcePackage.ImportedPublicExportHashes[exportHashIndex];
        
        // Search for export with matching hash
        foreach (var export in targetPackage.ExportMap)
        {
            if (export.PublicExportHash == exportHash)
            {
                return (targetPackage, export);
            }
        }
        
        return (targetPackage, null);
    }

    /// <summary>
    /// Resolve a script import using the script objects database
    /// </summary>
    public ScriptObjectEntry? ResolveScriptImport(FPackageObjectIndex importIndex)
    {
        if (ScriptObjects == null || !importIndex.IsScriptImport())
            return null;
        
        return ScriptObjects.GetScriptObject(importIndex);
    }

    /// <summary>
    /// Index public export hashes for a package
    /// </summary>
    private void IndexPackageExports(ulong packageId, FZenPackageHeader header)
    {
        if (_packageExportHashes.ContainsKey(packageId))
            return;
        
        var hashes = new List<ulong>();
        foreach (var export in header.ExportMap)
        {
            if (export.PublicExportHash != 0)
            {
                hashes.Add(export.PublicExportHash);
            }
        }
        _packageExportHashes[packageId] = hashes;
    }

    /// <summary>
    /// Preload all packages (useful for batch operations)
    /// </summary>
    public void PreloadAllPackages(IProgress<int>? progress = null)
    {
        int count = 0;
        int total = _packageIdToChunk.Count;
        
        foreach (var packageId in _packageIdToChunk.Keys)
        {
            GetPackage(packageId);
            count++;
            progress?.Report((count * 100) / total);
        }
        
        Console.WriteLine($"[Context] Preloaded {count} packages");
    }

    /// <summary>
    /// Clear the package cache to free memory
    /// </summary>
    public void ClearCache()
    {
        _packageCache.Clear();
        _packageExportHashes.Clear();
    }

    public void Dispose()
    {
        foreach (var container in _containers)
        {
            container.Dispose();
        }
        _containers.Clear();
        _packageCache.Clear();
        _packageExportHashes.Clear();
    }
}

/// <summary>
/// Cached package data
/// </summary>
public class CachedPackage
{
    public FZenPackageHeader Header { get; set; } = null!;
    public byte[] RawData { get; set; } = Array.Empty<byte>();
    public ulong PackageId { get; set; }
}
