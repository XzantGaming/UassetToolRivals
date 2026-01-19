using System;
using System.IO;

namespace UAssetTool.ZenPackage;

/// <summary>
/// Package object index - references imports, exports, or script objects in Zen packages
/// </summary>
public class FPackageObjectIndex
{
    private const ulong InvalidIndex = ~0ul;
    private const ulong IndexBits = 62;
    private const ulong IndexMask = (1ul << (int)IndexBits) - 1;
    private const ulong TypeMask = ~IndexMask;
    private const int TypeShift = (int)IndexBits;

    public ulong Value { get; set; }

    public FPackageObjectIndex()
    {
        Value = InvalidIndex;
    }

    public FPackageObjectIndex(ulong value)
    {
        Value = value;
    }

    public void Read(BinaryReader reader)
    {
        Value = reader.ReadUInt64();
    }

    public void Write(BinaryWriter writer)
    {
        writer.Write(Value);
    }

    public bool IsNull() => Value == InvalidIndex;
    public bool IsExport() => !IsNull() && GetIndexType() == EPackageObjectIndexType.Export;
    public bool IsImport() => !IsNull() && GetIndexType() == EPackageObjectIndexType.PackageImport;
    public bool IsScriptImport() => !IsNull() && GetIndexType() == EPackageObjectIndexType.ScriptImport;

    public EPackageObjectIndexType GetIndexType()
    {
        return (EPackageObjectIndexType)((Value & TypeMask) >> TypeShift);
    }

    public ulong GetIndex()
    {
        return Value & IndexMask;
    }

    public static FPackageObjectIndex CreateNull()
    {
        return new FPackageObjectIndex(InvalidIndex);
    }

    public static FPackageObjectIndex CreateExport(uint exportIndex)
    {
        return new FPackageObjectIndex(((ulong)EPackageObjectIndexType.Export << TypeShift) | exportIndex);
    }

    public static FPackageObjectIndex CreateImport(uint importIndex)
    {
        return new FPackageObjectIndex(((ulong)EPackageObjectIndexType.PackageImport << TypeShift) | importIndex);
    }

    public static FPackageObjectIndex CreateScriptImport(uint scriptImportIndex)
    {
        return new FPackageObjectIndex(((ulong)EPackageObjectIndexType.ScriptImport << TypeShift) | scriptImportIndex);
    }
    
    /// <summary>
    /// Create a script import from an object path (e.g., "/Script/Engine.StaticMesh")
    /// Uses CityHash64 of the UTF-16LE encoded lowercase path with : and . replaced by /
    /// </summary>
    public static FPackageObjectIndex CreateScriptImport(string objectPath)
    {
        ulong hash = GenerateImportHashFromObjectPath(objectPath);
        return new FPackageObjectIndex(((ulong)EPackageObjectIndexType.ScriptImport << TypeShift) | hash);
    }
    
    /// <summary>
    /// Generate import hash from object path using CityHash64
    /// Matches Rust: generate_import_hash_from_object_path
    /// </summary>
    private static ulong GenerateImportHashFromObjectPath(string objectPath)
    {
        // Convert : and . to /, then lowercase
        string normalizedPath = objectPath
            .Replace(':', '/')
            .Replace('.', '/')
            .ToLowerInvariant();
        
        // Hash using CityHash64 of UTF-16LE bytes
        byte[] bytes = System.Text.Encoding.Unicode.GetBytes(normalizedPath);
        ulong hash = IoStore.CityHash.CityHash64(bytes, 0, bytes.Length);
        
        // Clear the top 2 bits (type bits)
        hash &= ~(3ul << 62);
        return hash;
    }

    public static FPackageObjectIndex CreateFromRaw(ulong raw)
    {
        return new FPackageObjectIndex(raw);
    }
    
    /// <summary>
    /// Create a package import from package index and export hash index
    /// The index encodes both: (packageIndex << 32) | exportHashIndex
    /// </summary>
    public static FPackageObjectIndex CreatePackageImport(uint packageIndex, uint exportHashIndex)
    {
        ulong combinedIndex = ((ulong)packageIndex << 32) | exportHashIndex;
        return new FPackageObjectIndex(((ulong)EPackageObjectIndexType.PackageImport << TypeShift) | combinedIndex);
    }

    public uint ToExportIndex()
    {
        if (!IsExport()) throw new InvalidOperationException("Not an export index");
        return (uint)GetIndex();
    }

    public uint ToImportIndex()
    {
        if (!IsImport()) throw new InvalidOperationException("Not an import index");
        return (uint)GetIndex();
    }

    public uint GetExportIndex()
    {
        return ToExportIndex();
    }

    public bool IsPackageImport()
    {
        return IsImport();
    }

    /// <summary>
    /// Get package import details (package index and export hash index)
    /// </summary>
    public (int ImportedPackageIndex, int ImportedPublicExportHashIndex) GetPackageImport()
    {
        if (!IsImport()) throw new InvalidOperationException("Not a package import");
        ulong index = GetIndex();
        int packageIndex = (int)(index >> 32);
        int exportHashIndex = (int)(index & 0xFFFFFFFF);
        return (packageIndex, exportHashIndex);
    }
}

public enum EPackageObjectIndexType : ulong
{
    Export = 0,
    ScriptImport = 1,
    PackageImport = 2,
    Null = 3
}
