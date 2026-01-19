using System;
using System.IO;

namespace UAssetTool.ZenPackage;

/// <summary>
/// Export map entry in Zen packages - contains metadata about an exported object
/// </summary>
public class FExportMapEntry
{
    public ulong CookedSerialOffset { get; set; }
    public ulong CookedSerialSize { get; set; }
    public FMappedName ObjectName { get; set; }
    public FPackageObjectIndex OuterIndex { get; set; }
    public FPackageObjectIndex ClassIndex { get; set; }
    public FPackageObjectIndex SuperIndex { get; set; }
    public FPackageObjectIndex TemplateIndex { get; set; }
    public ulong PublicExportHash { get; set; }
    public uint ObjectFlags { get; set; }
    public EExportFilterFlags FilterFlags { get; set; }
    public byte[] Padding { get; set; }

    public FExportMapEntry()
    {
        ObjectName = new FMappedName();
        OuterIndex = new FPackageObjectIndex();
        ClassIndex = new FPackageObjectIndex();
        SuperIndex = new FPackageObjectIndex();
        TemplateIndex = new FPackageObjectIndex();
        Padding = new byte[3];
    }

    public void Read(BinaryReader reader)
    {
        CookedSerialOffset = reader.ReadUInt64();
        CookedSerialSize = reader.ReadUInt64();
        ObjectName.Read(reader);
        OuterIndex.Read(reader);
        ClassIndex.Read(reader);
        SuperIndex.Read(reader);
        TemplateIndex.Read(reader);
        PublicExportHash = reader.ReadUInt64();
        ObjectFlags = reader.ReadUInt32();
        FilterFlags = (EExportFilterFlags)reader.ReadByte();
        Padding = reader.ReadBytes(3);
    }

    public void Write(BinaryWriter writer)
    {
        writer.Write(CookedSerialOffset);
        writer.Write(CookedSerialSize);
        ObjectName.Write(writer);
        OuterIndex.Write(writer);
        ClassIndex.Write(writer);
        SuperIndex.Write(writer);
        TemplateIndex.Write(writer);
        writer.Write(PublicExportHash);
        writer.Write(ObjectFlags);
        writer.Write((byte)FilterFlags);
        writer.Write(Padding);
    }

    public bool IsPublicExport()
    {
        return PublicExportHash != 0 && LegacyGlobalImportIndex().Value != FPackageObjectIndex.CreateNull().Value;
    }

    public FPackageObjectIndex LegacyGlobalImportIndex()
    {
        return FPackageObjectIndex.CreateFromRaw(PublicExportHash);
    }
}

public enum EExportFilterFlags : byte
{
    None = 0,
    NotForClient = 1,
    NotForServer = 2
}

public enum EObjectFlags : uint
{
    Public = 0x00000001,
    Standalone = 0x00000002,
    Transactional = 0x00000008,
    ClassDefaultObject = 0x00000010,
    ArchetypeObject = 0x00000020
}
