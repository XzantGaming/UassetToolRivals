using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;

namespace UAssetTool.ZenPackage;

/// <summary>
/// Converts between Legacy (.uasset/.uexp) and Zen (.uzenasset) package formats
/// with full control over SerialSize and offset calculations
/// </summary>
public class ZenConverter
{
    private const uint PACKAGE_FILE_TAG = 0x9E2A83C1;
    
    // Static script objects database - loaded once and reused
    private static ScriptObjectsDatabase? _scriptObjectsDb;
    private static readonly object _scriptObjectsLock = new();
    
    /// <summary>
    /// Set the script objects database to use for resolving class hashes
    /// </summary>
    public static void SetScriptObjectsDatabase(ScriptObjectsDatabase db)
    {
        lock (_scriptObjectsLock)
        {
            _scriptObjectsDb = db;
        }
    }
    
    /// <summary>
    /// Try to load script objects database from default location
    /// </summary>
    public static bool TryLoadScriptObjectsDatabase(string? path = null)
    {
        string dbPath = path ?? @"E:\WindsurfCoding\repak_rivals-remastered\ScriptObjectExportTest\ScriptObjects.bin";
        if (File.Exists(dbPath))
        {
            try
            {
                var db = ScriptObjectsDatabase.Load(dbPath);
                SetScriptObjectsDatabase(db);
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ZenConverter] Failed to load script objects database: {ex.Message}");
            }
        }
        return false;
    }
    
    /// <summary>
    /// Convert Legacy package to Zen format and return the package path from the asset
    /// </summary>
    public static (byte[] ZenData, string PackagePath) ConvertLegacyToZenWithPath(
        string uassetPath,
        string? usmapPath = null,
        EIoContainerHeaderVersion containerVersion = EIoContainerHeaderVersion.NoExportInfo)
    {
        var zenData = ConvertLegacyToZenInternal(uassetPath, usmapPath, containerVersion, out string packagePath, out _);
        return (zenData, packagePath);
    }
    
    /// <summary>
    /// Convert Legacy package to Zen format and return the package path and FZenPackage object
    /// </summary>
    public static (byte[] ZenData, string PackagePath, FZenPackage ZenPackage) ConvertLegacyToZenFull(
        string uassetPath,
        string? usmapPath = null,
        EIoContainerHeaderVersion containerVersion = EIoContainerHeaderVersion.NoExportInfo)
    {
        var zenData = ConvertLegacyToZenInternal(uassetPath, usmapPath, containerVersion, out string packagePath, out FZenPackage zenPackage);
        return (zenData, packagePath, zenPackage);
    }

    /// <summary>
    /// Convert Legacy package to Zen format with recalculated SerialSize from actual export data
    /// </summary>
    public static byte[] ConvertLegacyToZen(
        string uassetPath,
        string? usmapPath = null,
        EIoContainerHeaderVersion containerVersion = EIoContainerHeaderVersion.NoExportInfo)
    {
        return ConvertLegacyToZenInternal(uassetPath, usmapPath, containerVersion, out _, out _);
    }

    private static byte[] ConvertLegacyToZenInternal(
        string uassetPath,
        string? usmapPath,
        EIoContainerHeaderVersion containerVersion,
        out string packagePath,
        out FZenPackage zenPackageOut)
    {
        // Try to load script objects database if not already loaded
        lock (_scriptObjectsLock)
        {
            if (_scriptObjectsDb == null)
            {
                TryLoadScriptObjectsDatabase();
            }
        }
        
        // Load the legacy asset
        var asset = LoadAsset(uassetPath, usmapPath);
        asset.UseSeparateBulkDataFiles = true;

        // Extract package path from asset's FolderName
        // FolderName contains the directory path like "/Game/Marvel/Characters/1033/1033001/Weapons/Meshes/Stick_L"
        string folderName = asset.FolderName?.Value ?? "";
        string assetName = Path.GetFileNameWithoutExtension(uassetPath);
        
        // Build the full package path
        // FolderName is like "/Game/Marvel/Characters/..." - we need to convert to "Marvel/Content/Marvel/Characters/..."
        if (folderName.StartsWith("/Game/"))
        {
            // Convert /Game/X to Marvel/Content/X
            packagePath = "Marvel/Content" + folderName.Substring(5); // Remove "/Game" prefix
        }
        else if (!string.IsNullOrEmpty(folderName))
        {
            packagePath = folderName.TrimStart('/');
        }
        else
        {
            // Fallback to just the asset name
            packagePath = "Marvel/Content/" + assetName;
        }
        
        // Ensure path ends with asset name (without extension)
        if (!packagePath.EndsWith(assetName))
        {
            packagePath = packagePath.TrimEnd('/') + "/" + assetName;
        }
        
        Console.Error.WriteLine($"[ZenConverter] Package path from asset: {packagePath}");

        string uexpPath = uassetPath.Replace(".uasset", ".uexp");
        if (!File.Exists(uexpPath))
        {
            throw new FileNotFoundException($"No .uexp file found: {uexpPath}");
        }

        byte[] uexpData = File.ReadAllBytes(uexpPath);
        long headerSize = asset.Exports.Min(e => e.SerialOffset);

        // Build Zen package
        var zenPackage = new FZenPackage
        {
            ContainerVersion = containerVersion,
            Summary = new FZenPackageSummary(),
            NameMap = new List<string>(),
            ImportMap = new List<FPackageObjectIndex>(),
            ExportMap = new List<FExportMapEntry>(),
            ExportBundleHeaders = new List<FExportBundleHeader>(),
            ExportBundleEntries = new List<FExportBundleEntry>(),
            DependencyBundleHeaders = new List<FDependencyBundleHeader>(),
            DependencyBundleEntries = new List<FDependencyBundleEntry>()
        };

        // Build name map from legacy asset (includes package name)
        BuildNameMap(asset, zenPackage);

        // Set package name and flags from legacy asset
        SetPackageSummary(asset, zenPackage);

        // Build import map with proper remapping
        BuildImportMap(asset, zenPackage);

        // Build export map with RECALCULATED SerialSize from actual data
        BuildExportMapWithRecalculatedSizes(asset, zenPackage, uexpData, headerSize);

        // Build export bundles (required for UE5 to load assets)
        BuildExportBundles(asset, zenPackage);

        // Build dependency bundles (for UE5.3+ NoExportInfo version)
        if (containerVersion >= EIoContainerHeaderVersion.NoExportInfo)
        {
            BuildDependencyBundles(asset, zenPackage);
        }

        // Write Zen package to memory
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        WriteZenPackage(asset, writer, zenPackage, uexpData, headerSize);

        zenPackageOut = zenPackage;
        return ms.ToArray();
    }

    /// <summary>
    /// Build export map with SerialSize recalculated from actual export data in .uexp
    /// This ensures the size matches the real data, not what the header claims
    /// </summary>
    private static void BuildExportMapWithRecalculatedSizes(
        UAsset asset,
        FZenPackage zenPackage,
        byte[] uexpData,
        long headerSize)
    {
        var sortedExports = asset.Exports.OrderBy(e => e.SerialOffset).ToList();
        
        // Build a mapping from export to its index for remapping
        var exportToIndex = new Dictionary<Export, int>();
        for (int i = 0; i < asset.Exports.Count; i++)
        {
            exportToIndex[asset.Exports[i]] = i;
        }

        // Check for .ubulk file - bulk data size must be added to the last export's CookedSerialSize
        // Even though bulk data is in a separate IoStore chunk, the game expects the export size
        // to include the bulk data reference structure
        string ubulkPath = Path.ChangeExtension(asset.FilePath, ".ubulk");
        long bulkDataAdjustment = 0;
        if (File.Exists(ubulkPath))
        {
            long ubulkSize = new FileInfo(ubulkPath).Length;
            const long BULK_DATA_OVERHEAD = 432; // Overhead for bulk data reference structure
            bulkDataAdjustment = ubulkSize + BULK_DATA_OVERHEAD;
            Console.Error.WriteLine($"[ZenConverter] Found .ubulk ({ubulkSize} bytes), will add {bulkDataAdjustment} to last export");
        }

        // Get package name for public export hash calculation
        string packageName = Path.GetFileNameWithoutExtension(asset.FilePath);

        for (int i = 0; i < sortedExports.Count; i++)
        {
            var export = sortedExports[i];
            int originalIndex = exportToIndex[export];
            
            // Calculate ACTUAL size from export data, not from header
            long startInUexp = export.SerialOffset - headerSize;
            long endInUexp = (i < sortedExports.Count - 1)
                ? sortedExports[i + 1].SerialOffset - headerSize
                : uexpData.Length - 4; // Exclude PACKAGE_FILE_TAG

            long actualSize = endInUexp - startInUexp;

            // Add bulk data adjustment for last export if .ubulk exists
            if (i == sortedExports.Count - 1 && bulkDataAdjustment > 0)
            {
                actualSize += bulkDataAdjustment;
                Console.Error.WriteLine($"[ZenConverter] Export {i} ({export.ObjectName?.Value?.Value}): Added bulk data adjustment {bulkDataAdjustment}, size={actualSize}");
            }
            else
            {
                Console.Error.WriteLine($"[ZenConverter] Export {i} ({export.ObjectName?.Value?.Value}): size={actualSize}");
            }

            // Remap indices from legacy FPackageIndex to Zen FPackageObjectIndex
            // Look up the import map which was already built with correct script import hashes
            var outerIndex = RemapLegacyPackageIndex(export.OuterIndex, zenPackage);
            var classIndex = RemapLegacyPackageIndex(export.ClassIndex, zenPackage);
            var superIndex = RemapLegacyPackageIndex(export.SuperIndex, zenPackage);
            var templateIndex = RemapLegacyPackageIndex(export.TemplateIndex, zenPackage);

            // Calculate public export hash for public exports
            // RF_Public = 0x00000001
            bool isPublic = (export.ObjectFlags & UAssetAPI.UnrealTypes.EObjectFlags.RF_Public) != 0;
            ulong publicExportHash = 0;
            if (isPublic)
            {
                string exportName = export.ObjectName?.Value?.Value ?? "None";
                publicExportHash = CalculatePublicExportHash(exportName);
            }

            // Determine filter flags (these properties may not exist on all exports)
            EExportFilterFlags filterFlags = EExportFilterFlags.None;

            // Create Zen export entry with ACTUAL calculated size
            // CookedSerialOffset is relative to CookedHeaderSize (which equals HeaderSize in Zen packages)
            // We store the offset relative to the start of export data (after header)
            var zenExport = new FExportMapEntry
            {
                CookedSerialOffset = (ulong)startInUexp,
                CookedSerialSize = (ulong)actualSize,
                ObjectName = MapNameToZen(zenPackage, export.ObjectName?.Value?.Value),
                ObjectFlags = (uint)export.ObjectFlags,
                FilterFlags = filterFlags,
                OuterIndex = outerIndex,
                ClassIndex = classIndex,
                SuperIndex = superIndex,
                TemplateIndex = templateIndex,
                PublicExportHash = publicExportHash
            };
            

            // Log if size differs from header
            if (actualSize != export.SerialSize)
            {
                Console.Error.WriteLine(
                    $"[ZenConverter] Export {i} ({export.ObjectName?.Value?.Value}): " +
                    $"Header={export.SerialSize}, Actual={actualSize}, Diff={actualSize - export.SerialSize}");
            }

            zenPackage.ExportMap.Add(zenExport);
        }
    }

    /// <summary>
    /// Remap a legacy UAssetAPI FPackageIndex to a Zen FPackageObjectIndex
    /// </summary>
    private static FPackageObjectIndex RemapLegacyPackageIndex(UAssetAPI.UnrealTypes.FPackageIndex legacyIndex, FZenPackage zenPackage)
    {
        if (legacyIndex.Index == 0)
            return FPackageObjectIndex.CreateNull();
        
        if (legacyIndex.IsExport())
            return FPackageObjectIndex.CreateExport((uint)(legacyIndex.Index - 1));
        
        if (legacyIndex.IsImport())
        {
            // Look up the import in the import map (which was already built with correct types)
            int importIndex = -legacyIndex.Index - 1;
            if (importIndex >= 0 && importIndex < zenPackage.ImportMap.Count)
            {
                return zenPackage.ImportMap[importIndex];
            }
            // Fallback to package import if import map not available
            return FPackageObjectIndex.CreateImport((uint)importIndex);
        }
        
        return FPackageObjectIndex.CreateNull();
    }
    
    /// <summary>
    /// Build the full object path for a script import (e.g., "/Script/Engine/StaticMesh")
    /// </summary>
    private static string BuildScriptObjectPath(UAsset asset, Import import)
    {
        string objectName = import.ObjectName?.Value?.Value ?? "None";
        
        // For script objects, the path is typically: /Script/Package/ClassName
        // If this is a top-level import (outer is null), it's the package itself
        if (import.OuterIndex.Index == 0)
        {
            // Top-level import - this is the package itself (e.g., "/Script/Engine")
            return objectName;
        }
        
        // Build the path by walking up the outer chain
        var pathParts = new List<string>();
        pathParts.Add(objectName);
        
        var currentIndex = import.OuterIndex;
        while (currentIndex.Index != 0 && currentIndex.IsImport())
        {
            int outerImportIndex = -currentIndex.Index - 1;
            if (outerImportIndex >= 0 && outerImportIndex < asset.Imports.Count)
            {
                var outerImport = asset.Imports[outerImportIndex];
                string outerName = outerImport.ObjectName?.Value?.Value ?? "None";
                pathParts.Insert(0, outerName);
                currentIndex = outerImport.OuterIndex;
            }
            else
            {
                break;
            }
        }
        
        // The first part should be the package path (e.g., "/Script/Engine")
        // which already starts with /, so just join with /
        return string.Join("/", pathParts);
    }

    /// <summary>
    /// Calculate public export hash using CityHash64 of the export name
    /// Uses GetPublicExportHash which calls hash_helper.exe for correct cityhasher output
    /// </summary>
    private static ulong CalculatePublicExportHash(string exportName)
    {
        // Use the same hash function as GetPublicExportHash for consistency
        // The export name is the path used for hashing (lowercase)
        return GetPublicExportHash(exportName);
    }

    private static void BuildNameMap(UAsset asset, FZenPackage zenPackage)
    {
        // Add all names from legacy name map
        foreach (var fstring in asset.GetNameMapIndexList())
        {
            string nameStr = fstring?.Value ?? "None";
            zenPackage.NameMap.Add(nameStr);
        }
        
        // Get the package name from the asset
        // The package name should be the FULL path like "/Game/Marvel/Characters/..." 
        // which gets converted to "/Marvel/Content/Marvel/Characters/..." for the mod manager
        string folderName = asset.FolderName?.Value ?? "";
        string assetName = Path.GetFileNameWithoutExtension(asset.FilePath ?? "Unknown");
        
        // Build the full package path that the mod manager expects
        // FolderName is like "/Game/Marvel/Characters/.../AssetName" - it already includes the asset name
        // So we just use FolderName directly as the package name
        string packageName;
        if (!string.IsNullOrEmpty(folderName))
        {
            // FolderName already contains the full path including asset name
            packageName = folderName;
        }
        else
        {
            // Fallback to just the asset name with a leading slash
            packageName = "/" + assetName;
        }
        
        // Check if package name is already in name map, if not add it
        int packageNameIndex = zenPackage.NameMap.IndexOf(packageName);
        if (packageNameIndex < 0)
        {
            packageNameIndex = zenPackage.NameMap.Count;
            zenPackage.NameMap.Add(packageName);
        }
        
        zenPackage.PackageName = packageName;
        zenPackage.PackageNameIndex = packageNameIndex;
        
        Console.Error.WriteLine($"[ZenConverter] Package name: {packageName} (index {packageNameIndex})");
    }

    private static void SetPackageSummary(UAsset asset, FZenPackage zenPackage)
    {
        // Set package name index from the name map
        zenPackage.Summary.Name = new FMappedName((uint)zenPackage.PackageNameIndex, 0);
        
        // Copy package flags from legacy asset
        // PKG_Cooked (0x00000200) + PKG_FilterEditorOnly (0x80000000) + PKG_ContainsMap (0x00002000) etc.
        // For cooked assets, we need at minimum PKG_Cooked
        uint packageFlags = 0;
        
        // Get flags from the legacy asset if available
        if (asset.PackageFlags != 0)
        {
            packageFlags = (uint)asset.PackageFlags;
        }
        else
        {
            // Default flags for cooked UE5 assets
            // PKG_Cooked = 0x00000200, PKG_FilterEditorOnly = 0x80000000
            packageFlags = 0x80000200;
        }
        
        zenPackage.Summary.PackageFlags = packageFlags;
        
        // Set cooked header size from legacy asset
        zenPackage.Summary.CookedHeaderSize = (uint)asset.Exports.Min(e => e.SerialOffset);
        
        Console.Error.WriteLine($"[ZenConverter] Package name: {zenPackage.PackageName} (index {zenPackage.PackageNameIndex})");
        Console.Error.WriteLine($"[ZenConverter] Package flags: 0x{packageFlags:X8}");
    }

    private static void BuildImportMap(UAsset asset, FZenPackage zenPackage)
    {
        // Build import map from legacy imports
        // For script imports (from /Script/), use pre-computed hashes from game's script objects database
        // For package imports (from other packages), create package import indices
        for (int i = 0; i < asset.Imports.Count; i++)
        {
            var import = asset.Imports[i];
            string objectName = import.ObjectName?.Value?.Value ?? "";
            
            // Build the full object path to determine the package
            string objectPath = BuildScriptObjectPath(asset, import);
            
            // Check if this import is from a /Script/ package (based on the object path, not class package)
            if (objectPath.StartsWith("/Script/"))
            {
                // Try to look up the pre-computed hash from the game's script objects database
                FPackageObjectIndex scriptImport;
                if (_scriptObjectsDb != null && _scriptObjectsDb.TryGetGlobalIndex(objectName, out ulong globalIndex))
                {
                    // Use the pre-computed hash from the game
                    scriptImport = FPackageObjectIndex.CreateFromRaw(globalIndex);
                }
                else
                {
                    // Fallback to generating hash from path
                    scriptImport = FPackageObjectIndex.CreateScriptImport(objectPath);
                    Console.Error.WriteLine($"[BuildImportMap] Warning: Script object '{objectName}' not found in database, using generated hash");
                }
                zenPackage.ImportMap.Add(scriptImport);
            }
            else
            {
                // For non-script imports, check if this is a package import (outer is null, meaning it's a package reference)
                // Package imports are represented as Null in Zen format
                if (import.OuterIndex.Index == 0)
                {
                    zenPackage.ImportMap.Add(FPackageObjectIndex.CreateNull());
                }
                else
                {
                    // Regular package import - need FPackageImportReference with package ID and export hash
                    // Get the package path from the import chain
                    string packagePath = GetImportPackagePath(asset, import);
                    string exportPath = GetImportExportPath(asset, import);
                    
                    if (!string.IsNullOrEmpty(packagePath) && !string.IsNullOrEmpty(exportPath))
                    {
                        // Calculate package ID from the package path
                        ulong packageId = IoStore.FPackageId.FromName(packagePath).Value;
                        
                        // Calculate public export hash from the export path (lowercase, without package prefix)
                        ulong exportHash = GetPublicExportHash(exportPath.ToLowerInvariant());
                        
                        // Add to imported packages if not already present
                        int packageIndex = zenPackage.ImportedPackages.IndexOf(packageId);
                        if (packageIndex == -1)
                        {
                            packageIndex = zenPackage.ImportedPackages.Count;
                            zenPackage.ImportedPackages.Add(packageId);
                            zenPackage.ImportedPackageNames.Add(packagePath);
                        }
                        
                        // Add to imported public export hashes if not already present
                        int hashIndex = zenPackage.ImportedPublicExportHashes.IndexOf(exportHash);
                        if (hashIndex == -1)
                        {
                            hashIndex = zenPackage.ImportedPublicExportHashes.Count;
                            zenPackage.ImportedPublicExportHashes.Add(exportHash);
                        }
                        
                        // Create FPackageImportReference and convert to FPackageObjectIndex
                        var importRef = FPackageObjectIndex.CreatePackageImport((uint)packageIndex, (uint)hashIndex);
                        zenPackage.ImportMap.Add(importRef);
                        
                        string lowerExportPath = exportPath.ToLowerInvariant();
                        Console.Error.WriteLine($"[BuildImportMap] Import {i} ({objectName}): Package={packagePath}, Export={lowerExportPath}, Hash=0x{exportHash:X16}, PkgIdx={packageIndex}, HashIdx={hashIndex}");
                    }
                    else
                    {
                        // Fallback to Null if we can't resolve the package
                        zenPackage.ImportMap.Add(FPackageObjectIndex.CreateNull());
                        Console.Error.WriteLine($"[BuildImportMap] Warning: Could not resolve package for import {i} ({objectName})");
                    }
                }
            }
        }
    }

    private static FMappedName MapNameToZen(FZenPackage zenPackage, string? name)
    {
        string nameStr = name ?? "None";
        int index = zenPackage.NameMap.IndexOf(nameStr);
        if (index == -1)
        {
            index = zenPackage.NameMap.Count;
            zenPackage.NameMap.Add(nameStr);
        }
        return new FMappedName((uint)index, 0);
    }
    
    /// <summary>
    /// Get the package path for an import by traversing the outer chain to find the root package
    /// </summary>
    private static string GetImportPackagePath(UAsset asset, Import import)
    {
        // Traverse up the outer chain to find the package (import with OuterIndex == 0)
        var current = import;
        while (current.OuterIndex.Index != 0)
        {
            int outerIdx = -current.OuterIndex.Index - 1; // Convert to 0-based import index
            if (outerIdx < 0 || outerIdx >= asset.Imports.Count)
                return "";
            current = asset.Imports[outerIdx];
        }
        // The root import's ObjectName is the package path
        return current.ObjectName?.Value?.Value ?? "";
    }
    
    /// <summary>
    /// Get the export path (object name within package) for an import
    /// </summary>
    private static string GetImportExportPath(UAsset asset, Import import)
    {
        // Build the path from the import up to (but not including) the package
        var parts = new List<string>();
        var current = import;
        
        while (current.OuterIndex.Index != 0)
        {
            parts.Add(current.ObjectName?.Value?.Value ?? "");
            int outerIdx = -current.OuterIndex.Index - 1;
            if (outerIdx < 0 || outerIdx >= asset.Imports.Count)
                break;
            current = asset.Imports[outerIdx];
        }
        
        // Reverse to get the path from package to object
        // Note: Rust uses path WITHOUT leading slash for hash calculation
        parts.Reverse();
        return string.Join("/", parts);
    }
    
    /// <summary>
    /// Calculate public export hash from export path using CityHash64
    /// Matches Rust: get_public_export_hash (uses cityhasher crate)
    /// Now uses native C# implementation that matches cityhasher exactly.
    /// </summary>
    private static ulong GetPublicExportHash(string exportPath)
    {
        // Use native C# CityHash64 implementation (matches cityhasher 0.1.0)
        return IoStore.CityHash.CityHash64(exportPath.ToLowerInvariant());
    }

    private const ulong FNAME_HASH_ALGORITHM_ID = 0xC1640000;

    /// <summary>
    /// Write a name batch in UE format.
    /// Format: count (u32), if count > 0: string_bytes (u32), hash_algo_id (u64), hashes (u64[]), headers (i16 BE[]), strings
    /// </summary>
    private static void WriteNameBatch(BinaryWriter writer, List<string> names)
    {
        writer.Write((uint)names.Count);
        if (names.Count == 0)
            return;

        // Calculate total string bytes
        uint totalStringBytes = 0;
        foreach (var name in names)
        {
            if (IsAscii(name))
                totalStringBytes += (uint)name.Length;
            else
                totalStringBytes += (uint)(Encoding.Unicode.GetByteCount(name));
        }
        writer.Write(totalStringBytes);

        // Hash algorithm ID
        writer.Write(FNAME_HASH_ALGORITHM_ID);

        // Write hashes (CityHash64 of lowercase name - ASCII bytes for ASCII strings, UTF-16LE for non-ASCII)
        // This is a NAME hash, not a package ID hash. Name hashes use ASCII bytes.
        foreach (var name in names)
        {
            string lower = name.ToLowerInvariant();
            byte[] bytes;
            if (IsAscii(lower))
                bytes = Encoding.ASCII.GetBytes(lower);
            else
                bytes = Encoding.Unicode.GetBytes(lower);
            
            // Use C# CityHash64 for name hashes (ASCII-based)
            ulong hash = IoStore.CityHash.CityHash64(bytes, 0, bytes.Length);
            writer.Write(hash);
        }

        // Write headers (i16 big-endian: positive = ASCII length, negative = UTF16 length)
        foreach (var name in names)
        {
            short len;
            if (IsAscii(name))
                len = (short)name.Length;
            else
                len = (short)(name.Length + short.MinValue); // Negative indicates UTF-16
            
            // Write as big-endian
            byte[] beBytes = BitConverter.GetBytes(len);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(beBytes);
            writer.Write(beBytes);
        }

        // Write string data
        foreach (var name in names)
        {
            if (IsAscii(name))
                writer.Write(Encoding.ASCII.GetBytes(name));
            else
                writer.Write(Encoding.Unicode.GetBytes(name));
        }
    }

    private static bool IsAscii(string s)
    {
        foreach (char c in s)
            if (c > 127) return false;
        return true;
    }

    /// <summary>
    /// Build export bundles - groups of exports that are loaded together
    /// For UE5.3+ (NoExportInfo), all exports go into a single bundle
    /// </summary>
    private static void BuildExportBundles(UAsset asset, FZenPackage zenPackage)
    {
        // For UE5.3+ NoExportInfo version, we create a single export bundle containing all exports
        // Each export gets two entries: Create and Serialize
        
        // Create a single bundle header
        var bundleHeader = new FExportBundleHeader
        {
            SerialOffset = 0,
            FirstEntryIndex = 0,
            EntryCount = (uint)(zenPackage.ExportMap.Count * 2) // Create + Serialize for each export
        };
        zenPackage.ExportBundleHeaders.Add(bundleHeader);
        
        // Add Create entries for all exports first, then Serialize entries
        for (int i = 0; i < zenPackage.ExportMap.Count; i++)
        {
            zenPackage.ExportBundleEntries.Add(new FExportBundleEntry((uint)i, EExportCommandType.Create));
        }
        
        for (int i = 0; i < zenPackage.ExportMap.Count; i++)
        {
            zenPackage.ExportBundleEntries.Add(new FExportBundleEntry((uint)i, EExportCommandType.Serialize));
        }
        
        Console.Error.WriteLine($"[ZenConverter] Created {zenPackage.ExportBundleHeaders.Count} export bundle(s) with {zenPackage.ExportBundleEntries.Count} entries");
    }

    /// <summary>
    /// Build dependency bundles - defines load order dependencies between exports
    /// For UE5.3+ (NoExportInfo), this replaces the legacy graph data
    /// </summary>
    private static void BuildDependencyBundles(UAsset asset, FZenPackage zenPackage)
    {
        // For each export, create a dependency bundle header
        // In the simple case (single bundle), dependencies are straightforward
        
        int currentEntryIndex = 0;
        
        for (int i = 0; i < zenPackage.ExportMap.Count; i++)
        {
            var export = asset.Exports[i];
            
            // Create dependency header for this export
            var depHeader = new FDependencyBundleHeader
            {
                FirstEntryIndex = currentEntryIndex,
                CreateBeforeCreateDependencies = 0,
                SerializeBeforeCreateDependencies = 0,
                CreateBeforeSerializeDependencies = 0,
                SerializeBeforeSerializeDependencies = 0
            };
            
            // Add dependencies based on export's outer/class/super/template indices
            var dependencies = new List<FPackageIndex>();
            
            // Check OuterIndex - if it references another export, add dependency
            if (export.OuterIndex.Index != 0)
            {
                var outerIdx = new FPackageIndex(export.OuterIndex.Index);
                if (outerIdx.IsExport())
                {
                    dependencies.Add(outerIdx);
                    depHeader.CreateBeforeCreateDependencies++;
                }
            }
            
            // Add dependency entries
            foreach (var dep in dependencies)
            {
                zenPackage.DependencyBundleEntries.Add(new FDependencyBundleEntry(dep));
                currentEntryIndex++;
            }
            
            zenPackage.DependencyBundleHeaders.Add(depHeader);
        }
        
        Console.Error.WriteLine($"[ZenConverter] Created {zenPackage.DependencyBundleHeaders.Count} dependency bundle header(s) with {zenPackage.DependencyBundleEntries.Count} entries");
    }

    private static void WriteZenPackage(
        UAsset asset,
        BinaryWriter writer,
        FZenPackage zenPackage,
        byte[] uexpData,
        long headerSize)
    {
        var containerVersion = zenPackage.ContainerVersion;
        
        // Write summary (placeholder, will update later)
        long summaryOffset = writer.BaseStream.Position;
        zenPackage.Summary.Write(writer, containerVersion);
        
        // Write name map in Rust read_name_batch format:
        // 1. num (u32) - count of names
        // 2. If num > 0:
        //    - num_string_bytes (u32) - total bytes of all strings
        //    - hash_version (u64) - must be 0xC1640000
        //    - hashes (8 bytes each, CityHash64 of lowercase name)
        //    - headers (2 bytes each, big-endian i16 length)
        //    - string data (concatenated ASCII bytes)
        int nameMapNamesOffset = (int)writer.BaseStream.Position;
        
        // Write count
        writer.Write((uint)zenPackage.NameMap.Count);
        
        if (zenPackage.NameMap.Count > 0)
        {
            // Write total byte size of all string data
            uint totalStringBytes = 0;
            foreach (var name in zenPackage.NameMap)
            {
                totalStringBytes += (uint)Encoding.ASCII.GetBytes(name).Length;
            }
            writer.Write(totalStringBytes);
            
            // Write hash algorithm ID (0xC1640000)
            writer.Write((ulong)0xC1640000);
            
            // Write hashes (CityHash64 of lowercase ASCII name)
            foreach (var name in zenPackage.NameMap)
            {
                string lowerName = name.ToLowerInvariant();
                byte[] nameBytes = Encoding.ASCII.GetBytes(lowerName);
                ulong hash = IoStore.CityHash.CityHash64(nameBytes, 0, nameBytes.Length);
                writer.Write(hash);
            }
            
            // Write headers (2 bytes each, big-endian i16 length)
            foreach (var name in zenPackage.NameMap)
            {
                short len = (short)Encoding.ASCII.GetBytes(name).Length;
                // Big-endian
                writer.Write((byte)(len >> 8));
                writer.Write((byte)(len & 0xFF));
            }
            
            // Write string data (concatenated ASCII bytes)
            foreach (var name in zenPackage.NameMap)
            {
                byte[] nameBytes = Encoding.ASCII.GetBytes(name);
                writer.Write(nameBytes);
            }
        }
        int nameMapNamesSize = (int)writer.BaseStream.Position - nameMapNamesOffset;
        
        // For UE5.3+ format, hashes are included in the name batch, so no separate hash section
        int nameMapHashesOffset = (int)writer.BaseStream.Position;

        // Write bulk data map entries from legacy asset's DataResources
        // UE5.3+ expects this field after the name map for packages with DataResources support
        // Each FBulkDataMapEntry is 32 bytes: serial_offset(8) + duplicate_serial_offset(8) + serial_size(8) + flags(4) + pad(4)
        if (asset.DataResources != null && asset.DataResources.Count > 0)
        {
            long bulkDataMapSize = asset.DataResources.Count * 32;
            writer.Write(bulkDataMapSize);
            
            Console.Error.WriteLine($"[ZenConverter] Writing {asset.DataResources.Count} bulk data map entries ({bulkDataMapSize} bytes):");
            int idx = 0;
            foreach (var resource in asset.DataResources)
            {
                Console.Error.WriteLine($"  [{idx}] SerialOffset={resource.SerialOffset}, DupOffset={resource.DuplicateSerialOffset}, Size={resource.SerialSize}, RawSize={resource.RawSize}, Flags=0x{resource.LegacyBulkDataFlags:X8}, OuterIndex={resource.OuterIndex}");
                writer.Write(resource.SerialOffset);
                writer.Write(resource.DuplicateSerialOffset);
                writer.Write(resource.SerialSize);
                writer.Write((uint)resource.LegacyBulkDataFlags);
                writer.Write((uint)0); // padding
                idx++;
            }
        }
        else
        {
            writer.Write((long)0);  // bulk_data_map_size = 0
        }

        // For UE5.0+, write imported public export hashes
        // This section contains hashes of public exports from imported packages
        int importedPublicExportHashesOffset = (int)writer.BaseStream.Position;
        foreach (var hash in zenPackage.ImportedPublicExportHashes)
        {
            writer.Write(hash);
        }
        if (zenPackage.ImportedPublicExportHashes.Count > 0)
        {
            Console.Error.WriteLine($"[ZenConverter] Wrote {zenPackage.ImportedPublicExportHashes.Count} imported public export hashes");
        }

        // Write import map
        int importMapOffset = (int)writer.BaseStream.Position;
        foreach (var import in zenPackage.ImportMap)
        {
            import.Write(writer);
        }

        // Write export map
        int exportMapOffset = (int)writer.BaseStream.Position;
        foreach (var export in zenPackage.ExportMap)
        {
            export.Write(writer);
        }

        // Write export bundle entries
        int exportBundleEntriesOffset = (int)writer.BaseStream.Position;
        foreach (var entry in zenPackage.ExportBundleEntries)
        {
            entry.Write(writer);
        }

        // For UE5.3+ NoExportInfo, write dependency bundle headers and entries
        int dependencyBundleHeadersOffset = -1;
        int dependencyBundleEntriesOffset = -1;
        int importedPackageNamesOffset = -1;
        
        if (containerVersion >= EIoContainerHeaderVersion.NoExportInfo)
        {
            dependencyBundleHeadersOffset = (int)writer.BaseStream.Position;
            foreach (var header in zenPackage.DependencyBundleHeaders)
            {
                header.Write(writer);
            }
            
            dependencyBundleEntriesOffset = (int)writer.BaseStream.Position;
            foreach (var entry in zenPackage.DependencyBundleEntries)
            {
                entry.Write(writer);
            }
            
            // Write imported package names in FZenPackageImportedPackageNamesContainer format
            // Format: name_batch + imported_package_name_numbers (i32[])
            importedPackageNamesOffset = (int)writer.BaseStream.Position;
            WriteNameBatch(writer, zenPackage.ImportedPackageNames);
            
            // Write imported_package_name_numbers (i32 array, one per package name)
            // These are the numeric suffixes from names like "Package_0" -> 0
            // For most package names without numeric suffix, this is 0
            foreach (var _ in zenPackage.ImportedPackageNames)
            {
                writer.Write((int)0); // No numeric suffix for standard package names
            }
        }

        // Record header size before writing export data
        int zenHeaderSize = (int)writer.BaseStream.Position;

        // Write export data (excluding PACKAGE_FILE_TAG)
        int exportDataLength = uexpData.Length;
        if (exportDataLength >= 4)
        {
            uint lastDword = BitConverter.ToUInt32(uexpData, exportDataLength - 4);
            if (lastDword == PACKAGE_FILE_TAG)
            {
                exportDataLength -= 4;
            }
        }
        writer.Write(uexpData, 0, exportDataLength);

        // Update summary with calculated offsets
        zenPackage.Summary.HeaderSize = (uint)zenHeaderSize;
        // CookedHeaderSize should equal HeaderSize for Zen packages (no separate cooked header)
        zenPackage.Summary.CookedHeaderSize = (uint)zenHeaderSize;
        zenPackage.Summary.ImportedPublicExportHashesOffset = importedPublicExportHashesOffset;
        zenPackage.Summary.ImportMapOffset = importMapOffset;
        zenPackage.Summary.ExportMapOffset = exportMapOffset;
        zenPackage.Summary.ExportBundleEntriesOffset = exportBundleEntriesOffset;
        
        if (containerVersion >= EIoContainerHeaderVersion.NoExportInfo)
        {
            zenPackage.Summary.DependencyBundleHeadersOffset = dependencyBundleHeadersOffset;
            zenPackage.Summary.DependencyBundleEntriesOffset = dependencyBundleEntriesOffset;
            zenPackage.Summary.ImportedPackageNamesOffset = importedPackageNamesOffset;
        }

        // Go back and write updated summary
        long endPosition = writer.BaseStream.Position;
        writer.BaseStream.Seek(summaryOffset, SeekOrigin.Begin);
        zenPackage.Summary.Write(writer, containerVersion);
        writer.BaseStream.Seek(endPosition, SeekOrigin.Begin);
        
        Console.Error.WriteLine($"[ZenConverter] Wrote Zen package: HeaderSize={zenHeaderSize}, ExportData={exportDataLength} bytes");
    }

    private static UAsset LoadAsset(string filePath, string? usmapPath)
    {
        UAssetAPI.Unversioned.Usmap? mappings = null;
        
        if (!string.IsNullOrEmpty(usmapPath) && File.Exists(usmapPath))
        {
            mappings = new UAssetAPI.Unversioned.Usmap(usmapPath);
        }

        var asset = new UAsset(filePath, EngineVersion.VER_UE5_3, mappings);
        asset.UseSeparateBulkDataFiles = true;
        return asset;
    }
}

/// <summary>
/// Represents a complete Zen package
/// </summary>
public class FZenPackage
{
    public EIoContainerHeaderVersion ContainerVersion { get; set; }
    public FZenPackageSummary Summary { get; set; }
    public List<string> NameMap { get; set; }
    public List<FPackageObjectIndex> ImportMap { get; set; }
    public List<FExportMapEntry> ExportMap { get; set; }
    public List<FExportBundleHeader> ExportBundleHeaders { get; set; }
    public List<FExportBundleEntry> ExportBundleEntries { get; set; }
    public List<FDependencyBundleHeader> DependencyBundleHeaders { get; set; }
    public List<FDependencyBundleEntry> DependencyBundleEntries { get; set; }
    public string PackageName { get; set; } = "";
    public int PackageNameIndex { get; set; }
    
    // For UE5.0+ package imports
    public List<ulong> ImportedPackages { get; set; }
    public List<string> ImportedPackageNames { get; set; }
    public List<ulong> ImportedPublicExportHashes { get; set; }

    public FZenPackage()
    {
        Summary = new FZenPackageSummary();
        NameMap = new List<string>();
        ImportMap = new List<FPackageObjectIndex>();
        ExportMap = new List<FExportMapEntry>();
        ExportBundleHeaders = new List<FExportBundleHeader>();
        ExportBundleEntries = new List<FExportBundleEntry>();
        DependencyBundleHeaders = new List<FDependencyBundleHeader>();
        DependencyBundleEntries = new List<FDependencyBundleEntry>();
        ImportedPackages = new List<ulong>();
        ImportedPackageNames = new List<string>();
        ImportedPublicExportHashes = new List<ulong>();
    }
}
