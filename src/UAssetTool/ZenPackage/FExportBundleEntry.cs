using System;
using System.IO;

namespace UAssetTool.ZenPackage;

/// <summary>
/// Export bundle entry - links an export to a command type (Create or Serialize)
/// </summary>
public class FExportBundleEntry
{
    public uint LocalExportIndex { get; set; }
    public EExportCommandType CommandType { get; set; }

    public FExportBundleEntry()
    {
        CommandType = EExportCommandType.Create;
    }

    public FExportBundleEntry(uint exportIndex, EExportCommandType commandType)
    {
        LocalExportIndex = exportIndex;
        CommandType = commandType;
    }

    public void Read(BinaryReader reader)
    {
        LocalExportIndex = reader.ReadUInt32();
        CommandType = (EExportCommandType)reader.ReadUInt32();
    }

    public void Write(BinaryWriter writer)
    {
        writer.Write(LocalExportIndex);
        writer.Write((uint)CommandType);
    }
}

/// <summary>
/// Export command type - Create or Serialize
/// </summary>
public enum EExportCommandType : uint
{
    Create = 0,
    Serialize = 1,
    Count = 2
}
