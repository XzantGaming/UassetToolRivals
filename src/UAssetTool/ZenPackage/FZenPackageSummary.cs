using System;
using System.IO;

namespace UAssetTool.ZenPackage;

/// <summary>
/// Zen package summary - the header of a Zen (.uzenasset) package
/// </summary>
public class FZenPackageSummary
{
    public uint HasVersioningInfo { get; set; }
    public uint HeaderSize { get; set; }
    public FMappedName Name { get; set; }
    public FMappedName SourceName { get; set; } // Only for Initial version
    public uint PackageFlags { get; set; }
    public uint CookedHeaderSize { get; set; }
    public int ImportedPublicExportHashesOffset { get; set; }
    public int ImportMapOffset { get; set; }
    public int ExportMapOffset { get; set; }
    public int ExportBundleEntriesOffset { get; set; }
    public int GraphDataOffset { get; set; }
    public int DependencyBundleHeadersOffset { get; set; }
    public int DependencyBundleEntriesOffset { get; set; }
    public int ImportedPackageNamesOffset { get; set; }
    
    // Only for Initial version
    public int NameMapNamesOffset { get; set; }
    public int NameMapNamesSize { get; set; }
    public int NameMapHashesOffset { get; set; }
    public int NameMapHashesSize { get; set; }
    public int GraphDataSize { get; set; }

    public FZenPackageSummary()
    {
        Name = new FMappedName();
        SourceName = new FMappedName();
    }

    public void Read(BinaryReader reader, EIoContainerHeaderVersion containerVersion)
    {
        if (containerVersion > EIoContainerHeaderVersion.Initial)
        {
            HasVersioningInfo = reader.ReadUInt32();
            HeaderSize = reader.ReadUInt32();
        }

        Name.Read(reader);
        
        if (containerVersion <= EIoContainerHeaderVersion.Initial)
        {
            SourceName.Read(reader);
        }

        PackageFlags = reader.ReadUInt32();
        CookedHeaderSize = reader.ReadUInt32();

        ImportedPublicExportHashesOffset = -1;
        NameMapNamesOffset = -1;
        NameMapNamesSize = -1;
        NameMapHashesOffset = -1;
        NameMapHashesSize = -1;

        if (containerVersion <= EIoContainerHeaderVersion.Initial)
        {
            NameMapNamesOffset = reader.ReadInt32();
            NameMapNamesSize = reader.ReadInt32();
            NameMapHashesOffset = reader.ReadInt32();
            NameMapHashesSize = reader.ReadInt32();
        }
        else
        {
            ImportedPublicExportHashesOffset = reader.ReadInt32();
        }

        ImportMapOffset = reader.ReadInt32();
        ExportMapOffset = reader.ReadInt32();
        ExportBundleEntriesOffset = reader.ReadInt32();

        GraphDataOffset = -1;
        DependencyBundleHeadersOffset = -1;
        DependencyBundleEntriesOffset = -1;
        ImportedPackageNamesOffset = -1;

        if (containerVersion >= EIoContainerHeaderVersion.NoExportInfo)
        {
            DependencyBundleHeadersOffset = reader.ReadInt32();
            DependencyBundleEntriesOffset = reader.ReadInt32();
            ImportedPackageNamesOffset = reader.ReadInt32();
        }
        else
        {
            GraphDataOffset = reader.ReadInt32();
        }

        if (containerVersion <= EIoContainerHeaderVersion.Initial)
        {
            GraphDataSize = reader.ReadInt32();
            int pad = reader.ReadInt32();
            HeaderSize = (uint)(GraphDataOffset + GraphDataSize);
        }
    }

    public void Write(BinaryWriter writer, EIoContainerHeaderVersion containerVersion)
    {
        if (containerVersion > EIoContainerHeaderVersion.Initial)
        {
            writer.Write(HasVersioningInfo);
            writer.Write(HeaderSize);
        }

        Name.Write(writer);
        
        if (containerVersion <= EIoContainerHeaderVersion.Initial)
        {
            SourceName.Write(writer);
        }

        writer.Write(PackageFlags);
        writer.Write(CookedHeaderSize);

        if (containerVersion <= EIoContainerHeaderVersion.Initial)
        {
            writer.Write(NameMapNamesOffset);
            writer.Write(NameMapNamesSize);
            writer.Write(NameMapHashesOffset);
            writer.Write(NameMapHashesSize);
        }
        else
        {
            writer.Write(ImportedPublicExportHashesOffset);
        }

        writer.Write(ImportMapOffset);
        writer.Write(ExportMapOffset);
        writer.Write(ExportBundleEntriesOffset);

        if (containerVersion >= EIoContainerHeaderVersion.NoExportInfo)
        {
            writer.Write(DependencyBundleHeadersOffset);
            writer.Write(DependencyBundleEntriesOffset);
            writer.Write(ImportedPackageNamesOffset);
        }
        else
        {
            writer.Write(GraphDataOffset);
        }

        if (containerVersion <= EIoContainerHeaderVersion.Initial)
        {
            writer.Write(GraphDataSize);
            writer.Write(0); // padding
        }
    }
}
