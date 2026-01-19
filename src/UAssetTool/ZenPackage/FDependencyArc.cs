using System;
using System.IO;

namespace UAssetTool.ZenPackage;

/// <summary>
/// Internal dependency arc - links one export bundle to another within the same package
/// </summary>
public class FInternalDependencyArc
{
    public int FromExportBundleIndex { get; set; }
    public int ToExportBundleIndex { get; set; }

    public FInternalDependencyArc()
    {
    }

    public FInternalDependencyArc(int from, int to)
    {
        FromExportBundleIndex = from;
        ToExportBundleIndex = to;
    }

    public void Read(BinaryReader reader)
    {
        FromExportBundleIndex = reader.ReadInt32();
        ToExportBundleIndex = reader.ReadInt32();
    }

    public void Write(BinaryWriter writer)
    {
        writer.Write(FromExportBundleIndex);
        writer.Write(ToExportBundleIndex);
    }

    public override int GetHashCode() => HashCode.Combine(FromExportBundleIndex, ToExportBundleIndex);
    public override bool Equals(object? obj) => obj is FInternalDependencyArc other && 
        FromExportBundleIndex == other.FromExportBundleIndex && ToExportBundleIndex == other.ToExportBundleIndex;
}

/// <summary>
/// External dependency arc - links an import to an export bundle
/// </summary>
public class FExternalDependencyArc
{
    public int FromImportIndex { get; set; }
    public EExportCommandType FromCommandType { get; set; }
    public int ToExportBundleIndex { get; set; }

    public FExternalDependencyArc()
    {
        FromCommandType = EExportCommandType.Create;
    }

    public FExternalDependencyArc(int fromImport, EExportCommandType commandType, int toBundle)
    {
        FromImportIndex = fromImport;
        FromCommandType = commandType;
        ToExportBundleIndex = toBundle;
    }

    public void Read(BinaryReader reader)
    {
        FromImportIndex = reader.ReadInt32();
        // Command type is serialized as uint8 in external arcs (inconsistent with export bundle entries)
        FromCommandType = (EExportCommandType)reader.ReadByte();
        ToExportBundleIndex = reader.ReadInt32();
    }

    public void Write(BinaryWriter writer)
    {
        writer.Write(FromImportIndex);
        // Command type is serialized as uint8 in external arcs
        writer.Write((byte)FromCommandType);
        writer.Write(ToExportBundleIndex);
    }

    public override int GetHashCode() => HashCode.Combine(FromImportIndex, FromCommandType, ToExportBundleIndex);
    public override bool Equals(object? obj) => obj is FExternalDependencyArc other &&
        FromImportIndex == other.FromImportIndex && FromCommandType == other.FromCommandType && 
        ToExportBundleIndex == other.ToExportBundleIndex;
}

/// <summary>
/// External package dependency - groups external dependency arcs by source package
/// </summary>
public class ExternalPackageDependency
{
    public ulong FromPackageId { get; set; }
    public List<FExternalDependencyArc> ExternalDependencyArcs { get; set; }
    public List<FInternalDependencyArc> LegacyDependencyArcs { get; set; }

    public ExternalPackageDependency()
    {
        ExternalDependencyArcs = new List<FExternalDependencyArc>();
        LegacyDependencyArcs = new List<FInternalDependencyArc>();
    }
}
