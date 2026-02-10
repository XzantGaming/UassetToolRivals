using System;
using System.IO;

namespace UAssetTool.ZenPackage;

/// <summary>
/// Dependency bundle header - defines dependencies between export bundles
/// Always 20 bytes (4 counts) for compatibility with retoc and game
/// </summary>
public class FDependencyBundleHeader
{
    public const int Size = 20; // 4 + 4*4 bytes (FirstEntryIndex + 4 dependency counts)
    
    public int FirstEntryIndex { get; set; }
    
    // Dependency counts - all 4 types
    public uint CreateBeforeCreateDependencies { get; set; }
    public uint SerializeBeforeCreateDependencies { get; set; }
    public uint CreateBeforeSerializeDependencies { get; set; }
    public uint SerializeBeforeSerializeDependencies { get; set; }

    public FDependencyBundleHeader()
    {
    }

    public static int GetSize(EIoContainerHeaderVersion version)
    {
        return Size; // Always 20 bytes
    }

    public void Read(BinaryReader reader, EIoContainerHeaderVersion version = EIoContainerHeaderVersion.NoExportInfo)
    {
        FirstEntryIndex = reader.ReadInt32();
        CreateBeforeCreateDependencies = reader.ReadUInt32();
        SerializeBeforeCreateDependencies = reader.ReadUInt32();
        CreateBeforeSerializeDependencies = reader.ReadUInt32();
        SerializeBeforeSerializeDependencies = reader.ReadUInt32();
    }

    public void Write(BinaryWriter writer, EIoContainerHeaderVersion version = EIoContainerHeaderVersion.NoExportInfo)
    {
        writer.Write(FirstEntryIndex);
        writer.Write(CreateBeforeCreateDependencies);
        writer.Write(SerializeBeforeCreateDependencies);
        writer.Write(CreateBeforeSerializeDependencies);
        writer.Write(SerializeBeforeSerializeDependencies);
    }
    
    public uint GetTotalDependencyCount(EIoContainerHeaderVersion version = EIoContainerHeaderVersion.NoExportInfo)
    {
        return CreateBeforeCreateDependencies + SerializeBeforeCreateDependencies +
               CreateBeforeSerializeDependencies + SerializeBeforeSerializeDependencies;
    }
}

/// <summary>
/// Dependency bundle entry - references an import or export that is a dependency
/// Matches Rust's FDependencyBundleEntry which is just 4 bytes (FPackageIndex only, no padding)
/// </summary>
public class FDependencyBundleEntry
{
    public const int Size = 4; // Just the FPackageIndex, no padding (matches Rust)
    
    public FPackageIndex LocalImportOrExportIndex { get; set; }

    public FDependencyBundleEntry()
    {
        LocalImportOrExportIndex = new FPackageIndex();
    }

    public FDependencyBundleEntry(FPackageIndex index)
    {
        LocalImportOrExportIndex = index;
    }

    public void Read(BinaryReader reader)
    {
        LocalImportOrExportIndex = new FPackageIndex();
        LocalImportOrExportIndex.Read(reader);
        // No padding - Rust's FDependencyBundleEntry is exactly 4 bytes
    }

    public void Write(BinaryWriter writer)
    {
        LocalImportOrExportIndex.Write(writer);
        // No padding
    }
}

/// <summary>
/// Package index - positive for exports, negative for imports, zero for null
/// </summary>
public class FPackageIndex : IEquatable<FPackageIndex>
{
    public int Index { get; set; }

    public FPackageIndex()
    {
        Index = 0;
    }

    public FPackageIndex(int index)
    {
        Index = index;
    }

    public static FPackageIndex CreateNull() => new FPackageIndex(0);
    public static FPackageIndex CreateImport(int importIndex) => new FPackageIndex(-importIndex - 1);
    public static FPackageIndex CreateExport(int exportIndex) => new FPackageIndex(exportIndex + 1);

    public bool IsImport() => Index < 0;
    public bool IsExport() => Index > 0;
    public bool IsNull() => Index == 0;

    public int ToImportIndex()
    {
        if (Index >= 0) throw new InvalidOperationException("Not an import index");
        return -Index - 1;
    }

    public int ToExportIndex()
    {
        if (Index <= 0) throw new InvalidOperationException("Not an export index");
        return Index - 1;
    }

    public void Read(BinaryReader reader)
    {
        Index = reader.ReadInt32();
    }

    public void Write(BinaryWriter writer)
    {
        writer.Write(Index);
    }

    // Equality implementation for proper Contains() behavior
    public bool Equals(FPackageIndex? other)
    {
        if (other is null) return false;
        return Index == other.Index;
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as FPackageIndex);
    }

    public override int GetHashCode()
    {
        return Index.GetHashCode();
    }

    public static bool operator ==(FPackageIndex? left, FPackageIndex? right)
    {
        if (left is null) return right is null;
        return left.Equals(right);
    }

    public static bool operator !=(FPackageIndex? left, FPackageIndex? right)
    {
        return !(left == right);
    }
}
