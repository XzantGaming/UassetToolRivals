using System;

namespace UAssetTool.ZenPackage;

/// <summary>
/// IoStore container header version
/// </summary>
public enum EIoContainerHeaderVersion : int
{
    Initial = 0,
    LocalizedPackages = 1,
    OptionalSegmentPackages = 2,
    NoExportInfo = 3
}
