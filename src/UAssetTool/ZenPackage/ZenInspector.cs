using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UAssetTool.ZenPackage;

/// <summary>
/// Inspects and dumps information from Zen-formatted .uasset files
/// </summary>
public class ZenInspector
{
    public static void InspectZenAsset(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Console.Error.WriteLine($"File not found: {filePath}");
            return;
        }

        byte[] data = File.ReadAllBytes(filePath);
        Console.WriteLine($"=== Zen Asset Inspector ===");
        Console.WriteLine($"File: {Path.GetFileName(filePath)}");
        Console.WriteLine($"Size: {data.Length} bytes");
        Console.WriteLine();

        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        // Try to detect container version from header structure
        // UE5.3+ NoExportInfo version starts with HasVersioningInfo (0 or 1)
        uint hasVersioningInfo = reader.ReadUInt32();
        uint headerSize = 0;
        
        if (hasVersioningInfo == 0 || hasVersioningInfo == 1)
        {
            // UE5.3+ format
            headerSize = reader.ReadUInt32();
            Console.WriteLine($"Container Version: NoExportInfo (UE5.3+)");
            Console.WriteLine($"Has Versioning Info: {hasVersioningInfo}");
            Console.WriteLine($"Header Size: {headerSize} bytes");
        }
        else
        {
            // Older format - rewind and try Initial version
            ms.Position = 0;
            Console.WriteLine($"Container Version: Initial (UE4/early UE5)");
        }

        Console.WriteLine();
        Console.WriteLine("=== Package Summary ===");

        // Read package name (FMappedName)
        uint nameIndex = reader.ReadUInt32();
        uint nameNumber = reader.ReadUInt32();
        Console.WriteLine($"Name Index: {nameIndex}, Number: {nameNumber}");

        // Package flags
        uint packageFlags = reader.ReadUInt32();
        Console.WriteLine($"Package Flags: 0x{packageFlags:X8}");

        // Cooked header size
        uint cookedHeaderSize = reader.ReadUInt32();
        Console.WriteLine($"Cooked Header Size: {cookedHeaderSize}");

        // Imported public export hashes offset (UE5.3+)
        int importedPublicExportHashesOffset = reader.ReadInt32();
        Console.WriteLine($"Imported Public Export Hashes Offset: {importedPublicExportHashesOffset}");

        // Import map offset
        int importMapOffset = reader.ReadInt32();
        Console.WriteLine($"Import Map Offset: {importMapOffset}");

        // Export map offset
        int exportMapOffset = reader.ReadInt32();
        Console.WriteLine($"Export Map Offset: {exportMapOffset}");

        // Export bundle entries offset
        int exportBundleEntriesOffset = reader.ReadInt32();
        Console.WriteLine($"Export Bundle Entries Offset: {exportBundleEntriesOffset}");

        // Dependency bundle headers offset (UE5.3+)
        int dependencyBundleHeadersOffset = reader.ReadInt32();
        Console.WriteLine($"Dependency Bundle Headers Offset: {dependencyBundleHeadersOffset}");

        // Dependency bundle entries offset (UE5.3+)
        int dependencyBundleEntriesOffset = reader.ReadInt32();
        Console.WriteLine($"Dependency Bundle Entries Offset: {dependencyBundleEntriesOffset}");

        // Imported package names offset (UE5.3+)
        int importedPackageNamesOffset = reader.ReadInt32();
        Console.WriteLine($"Imported Package Names Offset: {importedPackageNamesOffset}");

        Console.WriteLine();
        Console.WriteLine("=== Name Map ===");

        // Read name map - UE5.3+ format has hash entries followed by string data
        // The format is: [hash entries (2 bytes each)] [string lengths (2 bytes each)] [string data]
        long nameMapStart = ms.Position;
        long nameMapEnd = importMapOffset > 0 ? importMapOffset : exportMapOffset;
        
        // First, try to find where the actual string data starts by looking for ASCII text
        var names = new List<string>();
        byte[] nameMapData = new byte[nameMapEnd - nameMapStart];
        ms.Read(nameMapData, 0, nameMapData.Length);
        
        // Find the start of string data (look for sequences of printable ASCII)
        int stringDataStart = 0;
        for (int i = 0; i < nameMapData.Length - 4; i++)
        {
            // Look for a sequence of at least 4 printable ASCII characters
            if (IsPrintableAscii(nameMapData[i]) && IsPrintableAscii(nameMapData[i+1]) &&
                IsPrintableAscii(nameMapData[i+2]) && IsPrintableAscii(nameMapData[i+3]))
            {
                stringDataStart = i;
                break;
            }
        }
        
        Console.WriteLine($"Name Map Range: {nameMapStart} - {nameMapEnd} ({nameMapEnd - nameMapStart} bytes)");
        Console.WriteLine($"String Data Starts At: offset {stringDataStart} within name map");
        
        // Parse concatenated strings (null-separated or length-prefixed)
        int pos = stringDataStart;
        var currentName = new StringBuilder();
        
        while (pos < nameMapData.Length)
        {
            byte b = nameMapData[pos];
            if (b == 0 || !IsPrintableAscii(b))
            {
                if (currentName.Length > 0)
                {
                    names.Add(currentName.ToString());
                    currentName.Clear();
                }
                pos++;
                // Skip any non-printable bytes
                while (pos < nameMapData.Length && !IsPrintableAscii(nameMapData[pos]))
                    pos++;
            }
            else
            {
                currentName.Append((char)b);
                pos++;
            }
        }
        
        if (currentName.Length > 0)
            names.Add(currentName.ToString());
        
        // Display names
        for (int i = 0; i < Math.Min(names.Count, 25); i++)
        {
            Console.WriteLine($"  [{i}] {names[i]}");
        }
        
        if (names.Count > 25)
            Console.WriteLine($"  ... and {names.Count - 25} more names");
        
        Console.WriteLine($"Total Names Found: {names.Count}");
        
        // Reset position for further reading
        ms.Position = nameMapEnd;

        // Read export map
        Console.WriteLine();
        Console.WriteLine("=== Export Map ===");

        if (exportMapOffset > 0 && exportMapOffset < data.Length)
        {
            ms.Position = exportMapOffset;
            
            // Calculate number of exports from offset difference
            int exportMapSize = exportBundleEntriesOffset - exportMapOffset;
            int exportEntrySize = 72; // Size of FExportMapEntry
            int exportCount = exportMapSize / exportEntrySize;
            
            Console.WriteLine($"Export Map Size: {exportMapSize} bytes");
            Console.WriteLine($"Estimated Export Count: {exportCount}");
            Console.WriteLine();

            for (int i = 0; i < Math.Min(exportCount, 10); i++)
            {
                long entryStart = ms.Position;
                
                ulong cookedSerialOffset = reader.ReadUInt64();
                ulong cookedSerialSize = reader.ReadUInt64();
                uint objNameIndex = reader.ReadUInt32();
                uint objNameNumber = reader.ReadUInt32();
                ulong outerIndex = reader.ReadUInt64();
                ulong classIndex = reader.ReadUInt64();
                ulong superIndex = reader.ReadUInt64();
                ulong templateIndex = reader.ReadUInt64();
                ulong publicExportHash = reader.ReadUInt64();
                uint objectFlags = reader.ReadUInt32();
                byte filterFlags = reader.ReadByte();
                byte[] padding = reader.ReadBytes(3);

                string objName = objNameIndex < names.Count ? names[(int)objNameIndex] : $"[{objNameIndex}]";
                
                Console.WriteLine($"Export [{i}]: {objName}");
                Console.WriteLine($"  Serial Offset: {cookedSerialOffset}");
                Console.WriteLine($"  Serial Size: {cookedSerialSize}");
                Console.WriteLine($"  Object Flags: 0x{objectFlags:X8}");
                Console.WriteLine($"  Public Export Hash: 0x{publicExportHash:X16}");
                Console.WriteLine($"  Outer/Class/Super/Template: 0x{outerIndex:X}, 0x{classIndex:X}, 0x{superIndex:X}, 0x{templateIndex:X}");
                Console.WriteLine();
            }

            if (exportCount > 10)
                Console.WriteLine($"... and {exportCount - 10} more exports");
        }

        // Read export bundle entries
        Console.WriteLine();
        Console.WriteLine("=== Export Bundle Entries ===");

        if (exportBundleEntriesOffset > 0 && exportBundleEntriesOffset < data.Length)
        {
            ms.Position = exportBundleEntriesOffset;
            
            int bundleEntriesSize = dependencyBundleHeadersOffset > 0 
                ? dependencyBundleHeadersOffset - exportBundleEntriesOffset 
                : 0;
            int bundleEntrySize = 8; // Size of FExportBundleEntry
            int bundleEntryCount = bundleEntriesSize / bundleEntrySize;
            
            Console.WriteLine($"Bundle Entries Size: {bundleEntriesSize} bytes");
            Console.WriteLine($"Estimated Entry Count: {bundleEntryCount}");

            for (int i = 0; i < Math.Min(bundleEntryCount, 10); i++)
            {
                uint localExportIndex = reader.ReadUInt32();
                uint commandType = reader.ReadUInt32();
                string cmdName = commandType == 0 ? "Create" : commandType == 1 ? "Serialize" : $"Unknown({commandType})";
                Console.WriteLine($"  [{i}] Export {localExportIndex} - {cmdName}");
            }

            if (bundleEntryCount > 10)
                Console.WriteLine($"  ... and {bundleEntryCount - 10} more entries");
        }

        // Summary
        Console.WriteLine();
        Console.WriteLine("=== Summary ===");
        Console.WriteLine($"Header Size: {headerSize} bytes");
        Console.WriteLine($"Export Data Starts At: {headerSize}");
        Console.WriteLine($"Export Data Size: {data.Length - headerSize} bytes");
    }

    private static bool IsPrintableAscii(byte b)
    {
        return b >= 32 && b <= 126;
    }
}
