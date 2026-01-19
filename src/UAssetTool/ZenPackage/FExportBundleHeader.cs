using System;
using System.IO;

namespace UAssetTool.ZenPackage;

/// <summary>
/// Export bundle header - defines a bundle of exports that are loaded together
/// </summary>
public class FExportBundleHeader
{
    /// <summary>
    /// Serial offset to the first serialized export in this bundle (relative to zen header size)
    /// </summary>
    public ulong SerialOffset { get; set; }
    
    /// <summary>
    /// Index into ExportBundleEntries to the first entry belonging to this export bundle
    /// </summary>
    public uint FirstEntryIndex { get; set; }
    
    /// <summary>
    /// Number of entries in this export bundle
    /// </summary>
    public uint EntryCount { get; set; }

    public FExportBundleHeader()
    {
    }

    public FExportBundleHeader(ulong serialOffset, uint firstEntryIndex, uint entryCount)
    {
        SerialOffset = serialOffset;
        FirstEntryIndex = firstEntryIndex;
        EntryCount = entryCount;
    }

    public void Read(BinaryReader reader, EIoContainerHeaderVersion containerVersion)
    {
        // For legacy UE4 packages, serial offset is not written
        if (containerVersion > EIoContainerHeaderVersion.Initial)
        {
            SerialOffset = reader.ReadUInt64();
        }
        else
        {
            SerialOffset = ulong.MaxValue;
        }

        FirstEntryIndex = reader.ReadUInt32();
        EntryCount = reader.ReadUInt32();
    }

    public void Write(BinaryWriter writer, EIoContainerHeaderVersion containerVersion)
    {
        if (containerVersion > EIoContainerHeaderVersion.Initial)
        {
            writer.Write(SerialOffset);
        }
        writer.Write(FirstEntryIndex);
        writer.Write(EntryCount);
    }
}
