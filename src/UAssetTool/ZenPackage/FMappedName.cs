using System;
using System.IO;

namespace UAssetTool.ZenPackage;

/// <summary>
/// Mapped name in Zen packages - can reference package-level, container-level, or global name tables
/// </summary>
public class FMappedName
{
    private const int IndexBits = 30;
    private const int TypeShift = IndexBits;
    private const uint IndexMask = (1u << IndexBits) - 1;
    private const uint TypeMask = ~IndexMask;

    public uint Index { get; set; }
    public uint Number { get; set; }
    public EMappedNameType Type { get; set; }

    public FMappedName()
    {
        Index = 0;
        Number = 0;
        Type = EMappedNameType.Package;
    }

    public FMappedName(uint index, uint number, EMappedNameType type = EMappedNameType.Package)
    {
        Index = index;
        Number = number;
        Type = type;
    }

    public void Read(BinaryReader reader)
    {
        uint nameIndex = reader.ReadUInt32();
        Number = reader.ReadUInt32();
        
        Index = nameIndex & IndexMask;
        Type = (EMappedNameType)((nameIndex & TypeMask) >> TypeShift);
    }

    public void Write(BinaryWriter writer)
    {
        uint nameIndex = Index | ((uint)Type << TypeShift);
        writer.Write(nameIndex);
        writer.Write(Number);
    }

    public bool IsGlobal => Type != EMappedNameType.Package;
}

public enum EMappedNameType : uint
{
    Package = 0,
    Container = 1,
    Global = 2
}
