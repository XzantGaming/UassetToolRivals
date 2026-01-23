using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UAssetTool.ZenPackage;

/// <summary>
/// Loads and provides lookup for the game's global script objects database.
/// Script objects contain pre-computed hashes that FModel/CUE4Parse uses to resolve class types.
/// </summary>
public class ScriptObjectsDatabase
{
    private readonly Dictionary<string, ulong> _scriptObjectsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong, ScriptObjectEntry> _scriptObjectsByIndex = new();
    private readonly HashSet<ulong> _classIndices = new();
    private readonly List<string> _nameMap = new();
    
    public int ScriptObjectCount { get; private set; }
    public int NameCount => _nameMap.Count;
    public int Count => ScriptObjectCount;
    
    /// <summary>
    /// Load script objects from a binary file (extracted from game's global.utoc)
    /// </summary>
    public static ScriptObjectsDatabase Load(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Script objects file not found: {filePath}");
            
        byte[] data = File.ReadAllBytes(filePath);
        return Load(data);
    }
    
    /// <summary>
    /// Load script objects from binary data
    /// </summary>
    public static ScriptObjectsDatabase Load(byte[] data)
    {
        var db = new ScriptObjectsDatabase();
        
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);
        
        // Read name map (read_name_batch format)
        uint nameCount = reader.ReadUInt32();
        uint totalStringBytes = reader.ReadUInt32();
        ulong hashAlgId = reader.ReadUInt64();
        
        // Calculate offsets
        long hashesStart = 16;
        long headersStart = hashesStart + (nameCount * 8);
        long stringsStart = headersStart + (nameCount * 2);
        long scriptObjectsStart = stringsStart + totalStringBytes;
        
        // Read headers to get name lengths (big-endian signed i16)
        reader.BaseStream.Seek(headersStart, SeekOrigin.Begin);
        var rawLengths = new short[nameCount];
        for (int i = 0; i < nameCount; i++)
        {
            byte hi = reader.ReadByte();
            byte lo = reader.ReadByte();
            rawLengths[i] = (short)((hi << 8) | lo);
        }
        
        // Calculate proper byte offsets - strings are stored consecutively WITHOUT alignment padding
        // Alignment is only for runtime memory layout, not serialization
        var nameOffsets = new int[nameCount];
        var nameByteLengths = new int[nameCount];
        var isWide = new bool[nameCount];
        int currentOffset = 0;
        for (int i = 0; i < nameCount; i++)
        {
            if (rawLengths[i] < 0)
            {
                // UTF-16 string: charCount = short.MinValue - rawLen (gives positive)
                // Example: rawLen=-32763 -> charCount=-32768-(-32763)=-5 -> |charCount|=5
                int charCount = Math.Abs(short.MinValue - rawLengths[i]);
                nameByteLengths[i] = charCount * 2;
                isWide[i] = true;
                // NO alignment padding in serialized format
            }
            else
            {
                // ASCII string
                nameByteLengths[i] = rawLengths[i];
                isWide[i] = false;
            }
            nameOffsets[i] = currentOffset;
            currentOffset += nameByteLengths[i];
        }
        
        // Read all string data at once
        reader.BaseStream.Seek(stringsStart, SeekOrigin.Begin);
        byte[] allStringData = reader.ReadBytes((int)totalStringBytes);
        
        // Build name map
        for (int i = 0; i < nameCount; i++)
        {
            if (nameOffsets[i] + nameByteLengths[i] <= allStringData.Length)
            {
                string name = isWide[i]
                    ? Encoding.Unicode.GetString(allStringData, nameOffsets[i], nameByteLengths[i])
                    : Encoding.ASCII.GetString(allStringData, nameOffsets[i], nameByteLengths[i]);
                db._nameMap.Add(name);
            }
            else
            {
                db._nameMap.Add($"__invalid_{i}__");
            }
        }
        
        // Read script object entries
        reader.BaseStream.Seek(scriptObjectsStart, SeekOrigin.Begin);
        int scriptObjectCount = reader.ReadInt32();
        db.ScriptObjectCount = scriptObjectCount;
        
        // Each entry is 32 bytes:
        // - object_name: FMappedName (8 bytes: 4 byte index + 4 byte number)
        // - global_index: FPackageObjectIndex (8 bytes)
        // - outer_index: FPackageObjectIndex (8 bytes)
        // - cdo_class_index: FPackageObjectIndex (8 bytes)
        
        for (int i = 0; i < scriptObjectCount; i++)
        {
            uint nameIndexRaw = reader.ReadUInt32();
            uint nameNumber = reader.ReadUInt32();
            ulong globalIndex = reader.ReadUInt64();
            ulong outerIndex = reader.ReadUInt64();
            ulong cdoClassIndex = reader.ReadUInt64();
            
            int nameIndex = (int)(nameIndexRaw & 0x3FFFFFFF);
            
            // Create script object entry
            var entry = new ScriptObjectEntry
            {
                ObjectName = new FMappedName((uint)nameIndex, nameNumber),
                GlobalIndex = globalIndex,
                OuterIndex = new FPackageObjectIndex(outerIndex),
                CdoClassIndex = new FPackageObjectIndex(cdoClassIndex)
            };
            
            // Store by global index
            if (!db._scriptObjectsByIndex.ContainsKey(globalIndex))
            {
                db._scriptObjectsByIndex[globalIndex] = entry;
            }
            
            // Track which indices are classes (pointed to by CDO)
            if (cdoClassIndex != ~0ul)
            {
                db._classIndices.Add(cdoClassIndex);
            }
            
            if (nameIndex >= 0 && nameIndex < db._nameMap.Count)
            {
                string objectName = db._nameMap[nameIndex];
                
                // Build full path by walking outer chain
                string fullPath = BuildFullPath(db, objectName, outerIndex);
                
                // Store the mapping from full path to global index
                if (!db._scriptObjectsByName.ContainsKey(fullPath))
                {
                    db._scriptObjectsByName[fullPath] = globalIndex;
                }
                
                // Also store by simple name for fallback lookup (if not already present)
                // This allows looking up "SkeletalMesh" without the full path
                if (!db._scriptObjectsByName.ContainsKey(objectName))
                {
                    db._scriptObjectsByName[objectName] = globalIndex;
                }
            }
        }
        
        Console.Error.WriteLine($"[ScriptObjectsDatabase] Loaded {db.ScriptObjectCount} script objects, {db._scriptObjectsByName.Count} unique paths");
        
        return db;
    }
    
    private static string BuildFullPath(ScriptObjectsDatabase db, string objectName, ulong outerIndex)
    {
        // Build full path by walking the outer chain
        var pathParts = new List<string> { objectName };
        
        ulong currentOuter = outerIndex;
        int maxDepth = 20; // Prevent infinite loops
        
        while (currentOuter != 0 && currentOuter != ~0ul && maxDepth-- > 0)
        {
            if (db._scriptObjectsByIndex.TryGetValue(currentOuter, out var outerEntry))
            {
                int nameIdx = (int)outerEntry.ObjectName.Index;
                if (nameIdx >= 0 && nameIdx < db._nameMap.Count)
                {
                    pathParts.Add(db._nameMap[nameIdx]);
                }
                currentOuter = outerEntry.OuterIndex.Value;
            }
            else
            {
                break;
            }
        }
        
        // Reverse to get path from root to object
        pathParts.Reverse();
        return string.Join("/", pathParts);
    }
    
    /// <summary>
    /// Try to get the global index (hash) for a script object by its name or full path
    /// </summary>
    public bool TryGetGlobalIndex(string objectName, out ulong globalIndex)
    {
        // First try exact match (could be full path or simple name)
        if (_scriptObjectsByName.TryGetValue(objectName, out globalIndex))
            return true;
        
        // If it looks like a path, try just the last component
        int lastSlash = objectName.LastIndexOf('/');
        if (lastSlash >= 0)
        {
            string simpleName = objectName.Substring(lastSlash + 1);
            return _scriptObjectsByName.TryGetValue(simpleName, out globalIndex);
        }
        
        globalIndex = 0;
        return false;
    }
    
    /// <summary>
    /// Try to get the global index for a script object by its full path (e.g., "/Script/Engine/SkeletalMesh")
    /// </summary>
    public bool TryGetGlobalIndexByPath(string fullPath, out ulong globalIndex)
    {
        return _scriptObjectsByName.TryGetValue(fullPath, out globalIndex);
    }
    
    /// <summary>
    /// Get the global index (hash) for a script object by its name, or generate one if not found
    /// </summary>
    public ulong GetOrGenerateGlobalIndex(string objectPath)
    {
        // First try exact match
        if (_scriptObjectsByName.TryGetValue(objectPath, out ulong globalIndex))
        {
            return globalIndex;
        }
        
        // Try just the object name (last part of path)
        string objectName = objectPath;
        int lastSlash = objectPath.LastIndexOf('/');
        if (lastSlash >= 0)
        {
            objectName = objectPath.Substring(lastSlash + 1);
        }
        
        if (_scriptObjectsByName.TryGetValue(objectName, out globalIndex))
        {
            return globalIndex;
        }
        
        // Not found - generate hash from path (fallback)
        Console.Error.WriteLine($"[ScriptObjectsDatabase] Warning: Script object not found: {objectPath}, generating hash");
        return FPackageObjectIndex.CreateScriptImport(objectPath).Value;
    }
    
    /// <summary>
    /// List all loaded script objects (for debugging)
    /// </summary>
    public IEnumerable<KeyValuePair<string, ulong>> GetAllScriptObjects()
    {
        return _scriptObjectsByName;
    }
    
    /// <summary>
    /// Get script object entry by its FPackageObjectIndex
    /// </summary>
    public ScriptObjectEntry? GetScriptObject(FPackageObjectIndex index)
    {
        if (index.IsNull() || !index.IsScriptImport())
            return null;
        
        if (_scriptObjectsByIndex.TryGetValue(index.Value, out var entry))
            return entry;
            
        return null;
    }
    
    /// <summary>
    /// Get name string from FMappedName, including number suffix if present
    /// </summary>
    public string GetName(FMappedName mappedName)
    {
        int index = (int)mappedName.Index;
        string baseName;
        if (index >= 0 && index < _nameMap.Count)
            baseName = _nameMap[index];
        else
            baseName = $"__unknown_{index}__";
        
        // Append number suffix if present (Number > 0 means _N where N = Number - 1)
        if (mappedName.Number > 0)
            return $"{baseName}_{mappedName.Number - 1}";
        
        return baseName;
    }
    
    /// <summary>
    /// Check if a script object index represents a class (pointed to by a CDO)
    /// </summary>
    public bool IsClass(FPackageObjectIndex index)
    {
        return _classIndices.Contains(index.Value);
    }
}

/// <summary>
/// Script object entry
/// </summary>
public class ScriptObjectEntry
{
    public FMappedName ObjectName { get; set; } = new();
    public ulong GlobalIndex { get; set; }
    public FPackageObjectIndex OuterIndex { get; set; } = new();
    public FPackageObjectIndex CdoClassIndex { get; set; } = new();
}
