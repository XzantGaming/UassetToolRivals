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
    
    // Static usmap cache - loaded once per path and reused (HUGE performance win)
    private static readonly Dictionary<string, UAssetAPI.Unversioned.Usmap> _usmapCache = new();
    private static readonly object _usmapLock = new();
    
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

        // Log whether asset uses versioned or unversioned properties
        // The Zen package flags will be set to match the source asset's format
        // Verbose logging disabled for parallel performance

        // Extract package path from asset's FolderName
        // FolderName contains the directory path like "/Game/Marvel/Characters/1033/1033001/Weapons/Meshes/Stick_L"
        string folderName = asset.FolderName?.Value ?? "";
        string assetName = Path.GetFileNameWithoutExtension(uassetPath);
        
        // Build the full package path
        // FolderName may contain /../../../ segments that need to be normalized
        // Example: /Game/Marvel/../../../Marvel/Content/Marvel/Characters/... -> Marvel/Content/Marvel/Characters/...
        string normalizedFolder = NormalizePath(folderName);
        
        if (normalizedFolder.StartsWith("/Game/"))
        {
            // Convert /Game/X to Marvel/Content/X
            packagePath = "Marvel/Content" + normalizedFolder.Substring(5); // Remove "/Game" prefix
        }
        else if (normalizedFolder.StartsWith("/Marvel/Content/"))
        {
            // Already in correct format, just remove leading slash
            packagePath = normalizedFolder.TrimStart('/');
        }
        else if (!string.IsNullOrEmpty(normalizedFolder))
        {
            packagePath = normalizedFolder.TrimStart('/');
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
        
        // Verbose logging disabled for parallel performance

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

        // Check if this is a SkeletalMesh or StaticMesh that needs material slot padding
        // We need to calculate padding BEFORE building export map so sizes are correct
        bool isSkeletalMesh = asset.Exports.Any(e => 
            e.GetExportClassType()?.Value?.Value == "SkeletalMesh" ||
            e.GetExportClassType()?.Value?.Value?.Contains("SkeletalMesh") == true);
        
        bool isStaticMesh = asset.Exports.Any(e => 
            e.GetExportClassType()?.Value?.Value == "StaticMesh");
        
        bool isStringTable = asset.Exports.Any(e => 
            e.GetExportClassType()?.Value?.Value == "StringTable" ||
            e.GetExportClassType()?.Value?.Value?.EndsWith("StringTable") == true);
        
        int materialPaddingToAdd = 0;
        int stringTablePaddingToAdd = 0;
        if (isSkeletalMesh)
        {
            // For SkeletalMesh, try to use UAssetAPI's proper serialization via SkeletalMeshExport
            // which automatically includes FGameplayTagContainer padding
            var skeletalExport = asset.Exports.FirstOrDefault(e => e is UAssetAPI.ExportTypes.SkeletalMeshExport)
                as UAssetAPI.ExportTypes.SkeletalMeshExport;
            
            // Ensure extra data is parsed (lazy parsing since Extras is populated after Read)
            skeletalExport?.EnsureExtraDataParsed();
            
            // Check if materials were successfully parsed
            if (skeletalExport?.Materials == null || skeletalExport.Materials.Count == 0)
            {
                skeletalExport = null; // Reset to trigger fallback message
            }
            
            if (skeletalExport != null)
            {
                // CRITICAL: If source already had FGameplayTagContainer (extracted from game),
                // DO NOT re-serialize! Re-serialization would add 4-byte ObjectGuid booleans to
                // inner objects like MorphTargets, causing serial size mismatches.
                // Files extracted from IoStore already have correct format - pass through raw data.
                if (skeletalExport.SourceHadGameplayTags)
                {
                    // Source already has correct format - no re-serialization needed
                    // Just use the original uexpData as-is
                }
                else
                {
                    // Source needs FGameplayTagContainer padding - re-serialize
                    skeletalExport.IncludeGameplayTags = true;
                    var reserializedData = ReserializeExportData(asset);
                    if (reserializedData != null && reserializedData.Length > 0)
                    {
                        int sizeDiff = reserializedData.Length - uexpData.Length;
                        // Verbose logging disabled for parallel performance
                        uexpData = reserializedData;
                        materialPaddingToAdd = sizeDiff;
                    }
                }
            }
            else
            {
                // Materials weren't parsed by SkeletalMeshExport
                // This could mean:
                // 1. The file already has FGameplayTagContainer (extracted from game) - no padding needed
                // 2. Parsing failed for some other reason
                // For files extracted from the game, they already have the correct format
                // Verbose logging disabled for parallel performance
            }
        }
        else if (isStaticMesh)
        {
            // StaticMesh doesn't need FGameplayTagContainer padding for Marvel Rivals
            // Verbose logging disabled for parallel performance
        }
        
        if (isStringTable)
        {
            // For StringTable, use UAssetAPI's proper serialization which automatically includes
            // FGameplayTagContainer padding via StringTableExport.Write()
            // Re-serialize the asset to get properly formatted export data
            var reserializedData = ReserializeExportData(asset);
            if (reserializedData != null && reserializedData.Length > 0)
            {
                int sizeDiff = reserializedData.Length - uexpData.Length;
                if (sizeDiff != 0)
                {
                    // Verbose logging disabled for parallel performance
                    uexpData = reserializedData;
                    stringTablePaddingToAdd = sizeDiff;
                }
            }
        }
        
        // The padding is included in the re-serialized uexpData, but the export size calculation
        // uses original SerialOffset values which don't account for the padding.
        // Pass the padding amount so it can be added to the last export's size.
        int totalPaddingToAdd = materialPaddingToAdd + stringTablePaddingToAdd;

        // Build export map with RECALCULATED SerialSize from actual data
        BuildExportMapWithRecalculatedSizes(asset, zenPackage, uexpData, headerSize, totalPaddingToAdd);

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
        long headerSize,
        int materialPaddingToAdd = 0)
    {
        // Calculate export sizes from the actual data
        // For non-last exports, use the gap between SerialOffsets (these are relative positions)
        // For the last export, use the remaining data length
        var exportSizes = new Dictionary<Export, long>();
        var sortedByOffset = asset.Exports.OrderBy(e => e.SerialOffset).ToList();
        
        // For non-last exports, use the gap between SerialOffsets (these don't change during re-serialization)
        // For the last export, give it all remaining data (which includes any padding added)
        long sizeOfOtherExports = 0;
        for (int i = 0; i < sortedByOffset.Count - 1; i++)
        {
            var export = sortedByOffset[i];
            var nextExport = sortedByOffset[i + 1];
            // Use offset gap for non-last exports (more reliable than SerialSize)
            long size = nextExport.SerialOffset - export.SerialOffset;
            exportSizes[export] = size;
            sizeOfOtherExports += size;
        }
        
        // Last export: use SerialSize directly - it's updated by WriteData() after re-serialization
        // The padding is already included in SerialSize
        if (sortedByOffset.Count > 0)
        {
            var lastExport = sortedByOffset[sortedByOffset.Count - 1];
            exportSizes[lastExport] = lastExport.SerialSize;
        }
        
        // Keep original export order - reordering corrupts the data
        // The asset type icon in FModel is cosmetic, data integrity is critical
        var exportsToProcess = asset.Exports.ToList();

        // Note: .ubulk bulk data is stored in a SEPARATE IoStore chunk, NOT added to export size
        // The CookedSerialSize should only include the .uexp data, not bulk data
        string ubulkPath = Path.ChangeExtension(asset.FilePath, ".ubulk");
        if (File.Exists(ubulkPath))
        {
            long ubulkSize = new FileInfo(ubulkPath).Length;
            // Verbose logging disabled for parallel performance
        }

        // Get package name for public export hash calculation
        string packageName = Path.GetFileNameWithoutExtension(asset.FilePath);

        // Process exports in reordered list (main asset last)
        for (int i = 0; i < exportsToProcess.Count; i++)
        {
            var export = exportsToProcess[i];
            
            // Get pre-calculated size (padding already included for last export)
            long actualSize = exportSizes[export];
            // Verbose logging disabled for parallel performance

            // Remap indices from legacy FPackageIndex to Zen FPackageObjectIndex
            // Look up the import map which was already built with correct script import hashes
            var outerIndex = RemapLegacyPackageIndex(export.OuterIndex, zenPackage);
            var classIndex = RemapLegacyPackageIndex(export.ClassIndex, zenPackage);
            var superIndex = RemapLegacyPackageIndex(export.SuperIndex, zenPackage);
            var templateIndex = RemapLegacyPackageIndex(export.TemplateIndex, zenPackage);
            
            // Debug: show remapped indices for first few exports
            // Verbose logging disabled for parallel performance

            // Calculate public export hash for public exports
            // RF_Public = 0x00000001
            bool isPublic = (export.ObjectFlags & UAssetAPI.UnrealTypes.EObjectFlags.RF_Public) != 0;
            ulong publicExportHash = 0;
            if (isPublic)
            {
                string exportName = export.ObjectName?.Value?.Value ?? "None";
                int nameNumber = export.ObjectName?.Number ?? 0;
                // UE FName with Number > 0 displays as "BaseName_N" where N = Number - 1
                if (nameNumber > 0)
                    exportName = exportName + "_" + (nameNumber - 1);
                publicExportHash = CalculatePublicExportHash(exportName);
            }

            // Preserve FilterFlags from legacy export - retoc preserves these and the game expects them
            EExportFilterFlags filterFlags = EExportFilterFlags.None;
            if (export.bNotForClient)
                filterFlags = EExportFilterFlags.NotForClient;
            else if (export.bNotForServer)
                filterFlags = EExportFilterFlags.NotForServer;

            // Create Zen export entry with ACTUAL calculated size
            // CookedSerialOffset is relative to CookedHeaderSize (which equals HeaderSize in Zen packages)
            // We store the offset relative to the start of export data (after header)
            long exportOffset = export.SerialOffset - headerSize;
            var zenExport = new FExportMapEntry
            {
                CookedSerialOffset = (ulong)exportOffset,
                CookedSerialSize = (ulong)actualSize,
                ObjectName = MapNameToZen(zenPackage, export.ObjectName?.Value?.Value, export.ObjectName?.Number ?? 0),
                ObjectFlags = (uint)export.ObjectFlags,
                FilterFlags = filterFlags,
                OuterIndex = outerIndex,
                ClassIndex = classIndex,
                SuperIndex = superIndex,
                TemplateIndex = templateIndex,
                PublicExportHash = publicExportHash
            };
            

            // Log if size differs from header
            // Verbose logging disabled for parallel performance

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
            // For imports, we need to return the FPackageObjectIndex that's stored in the import map
            // The import map already contains the correct FPackageObjectIndex values (script hashes or package imports)
            int importIndex = -legacyIndex.Index - 1;
            if (importIndex >= 0 && importIndex < zenPackage.ImportMap.Count)
            {
                // Return the FPackageObjectIndex stored in the import map
                // This is either a script object hash or a package import reference
                return zenPackage.ImportMap[importIndex];
            }
            // Fallback - should not happen if import map is built correctly
            // Warning logging disabled for parallel performance
            return FPackageObjectIndex.CreateNull();
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

    /// <summary>
    /// Find the index of the main asset export (SkeletalMesh, StaticMesh, Texture2D, etc.)
    /// Returns -1 if not found or already at index 0
    /// </summary>
    private static int FindMainAssetExportIndex(UAsset asset)
    {
        // Main asset types that should be at index 0
        var mainAssetTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SkeletalMesh",
            "StaticMesh",
            "Texture2D",
            "Material",
            "MaterialInstance",
            "MaterialInstanceConstant",
            "AnimSequence",
            "AnimMontage",
            "Blueprint",
            "WidgetBlueprint",
            "SoundWave",
            "SoundCue",
            "ParticleSystem",
            "NiagaraSystem",
            "PhysicsAsset",
            "Skeleton"
        };
        
        for (int i = 0; i < asset.Exports.Count; i++)
        {
            var export = asset.Exports[i];
            
            // Get the class name from ClassIndex
            string? className = null;
            if (export.ClassIndex.IsImport())
            {
                int importIdx = -export.ClassIndex.Index - 1;
                if (importIdx >= 0 && importIdx < asset.Imports.Count)
                {
                    className = asset.Imports[importIdx].ObjectName?.Value?.Value;
                }
            }
            
            if (className != null && mainAssetTypes.Contains(className))
            {
                return i;
            }
        }
        
        return -1; // Not found
    }

    private static void BuildNameMap(UAsset asset, FZenPackage zenPackage)
    {
        // Add all names from legacy name map
        // NOTE: We cannot filter names here because the export data contains FName references
        // that use indices into this name map. Filtering would break those references.
        foreach (var fstring in asset.GetNameMapIndexList())
        {
            string nameStr = fstring?.Value ?? "None";
            zenPackage.NameMap.Add(nameStr);
        }
        
        // Get the package name from the asset
        // The package name should be the FULL path like "/Game/Marvel/Characters/..." 
        string folderName = asset.FolderName?.Value ?? "";
        string assetName = Path.GetFileNameWithoutExtension(asset.FilePath ?? "Unknown");
        
        // Normalize the folder name to resolve /../../../ segments
        string normalizedFolder = NormalizePath(folderName);
        
        // Build the package name in /Game/... format
        string packageName;
        if (normalizedFolder.StartsWith("/Game/"))
        {
            packageName = normalizedFolder;
        }
        else if (normalizedFolder.StartsWith("/Marvel/Content/"))
        {
            // Convert /Marvel/Content/X to /Game/X
            packageName = "/Game" + normalizedFolder.Substring("/Marvel/Content".Length);
        }
        else if (!string.IsNullOrEmpty(normalizedFolder))
        {
            packageName = normalizedFolder;
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
        
        // Verbose logging disabled for parallel performance
    }

    private static void SetPackageSummary(UAsset asset, FZenPackage zenPackage)
    {
        // Set package name index from the name map
        // Name.Number must be 0 - using a non-zero number causes FModel/CUE4Parse to append 
        // the number suffix to the package path when looking up store entries, breaking resolution
        zenPackage.Summary.Name = new FMappedName((uint)zenPackage.PackageNameIndex, 0);
        
        // Copy package flags from legacy asset
        // PKG_Cooked (0x00000200) + PKG_FilterEditorOnly (0x80000000) + PKG_UnversionedProperties (0x00002000)
        // The PKG_UnversionedProperties flag must match the actual serialization format of the source asset
        uint packageFlags = 0x80000200; // PKG_FilterEditorOnly | PKG_Cooked (base flags)
        
        // Merge any additional flags from the legacy asset
        packageFlags |= (uint)asset.PackageFlags;
        
        // Only set PKG_UnversionedProperties if the source asset uses unversioned serialization
        if (asset.HasUnversionedProperties)
        {
            packageFlags |= 0x00002000; // PKG_UnversionedProperties
        }
        
        zenPackage.Summary.PackageFlags = packageFlags;
        
        // Verbose logging disabled for parallel performance
        
        // Set cooked header size from legacy asset
        zenPackage.Summary.CookedHeaderSize = (uint)asset.Exports.Min(e => e.SerialOffset);
        
        // Verbose logging disabled for parallel performance
    }

    private static void BuildImportMap(UAsset asset, FZenPackage zenPackage)
    {
        // Determine this asset's own package path to prevent self-references
        // FolderName is the full package path (e.g., /Game/Marvel/VFX/.../MI_Environment_13_304)
        string assetName = Path.GetFileNameWithoutExtension(asset.FilePath);
        string folderName = asset.FolderName?.Value ?? "";
        string ownPackagePath = "";
        string normalizedFolder = NormalizePath(folderName);
        if (normalizedFolder.StartsWith("/Game/"))
            ownPackagePath = normalizedFolder.TrimEnd('/');
        else if (!string.IsNullOrEmpty(normalizedFolder))
            ownPackagePath = "/Game/" + normalizedFolder.TrimStart('/').TrimEnd('/');
        
        bool debugMode = Environment.GetEnvironmentVariable("DEBUG") == "1";
        if (debugMode)
            Console.Error.WriteLine($"[BuildImportMap] ownPackagePath={ownPackagePath}, folderName={folderName}, normalizedFolder={normalizedFolder}");
        
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
                // Use the FULL PATH for lookup to avoid ambiguity with objects that have the same name
                FPackageObjectIndex scriptImport;
                if (_scriptObjectsDb != null && _scriptObjectsDb.TryGetGlobalIndexByPath(objectPath, out ulong globalIndex))
                {
                    // Use the pre-computed hash from the game (found by full path)
                    scriptImport = FPackageObjectIndex.CreateFromRaw(globalIndex);
                }
                else if (_scriptObjectsDb != null && _scriptObjectsDb.TryGetGlobalIndex(objectName, out globalIndex))
                {
                    // Fallback to simple name lookup
                    scriptImport = FPackageObjectIndex.CreateFromRaw(globalIndex);
                    // Warning logging disabled for parallel performance
                }
                else
                {
                    // Fallback to generating hash from path
                    scriptImport = FPackageObjectIndex.CreateScriptImport(objectPath);
                    // Warning logging disabled for parallel performance
                }
                zenPackage.ImportMap.Add(scriptImport);
            }
            else
            {
                // For non-script imports:
                // OuterIndex == 0 means this IS a package reference (like /Game/Marvel/VFX/.../SomePackage)
                // OuterIndex < 0 means this is an export FROM a package (the outer points to the package import)
                // 
                // In Zen format, package references themselves are Null - the package is implicit
                // in the FPackageImportReference that points to exports from that package.
                
                if (import.OuterIndex.Index == 0)
                {
                    // Package reference - use Null (package is implicit in Zen format)
                    zenPackage.ImportMap.Add(FPackageObjectIndex.CreateNull());
                }
                else
                {
                    // Regular package import - need FPackageImportReference with package ID and export hash
                    // Get the package path from the import chain
                    string packagePath = GetImportPackagePath(asset, import);
                    string exportPath = GetImportExportPath(asset, import);
                    
                    // Skip self-references: if the import's package path matches our own package, use Null
                    if (debugMode)
                        Console.Error.WriteLine($"[BuildImportMap] Import[{i}]: packagePath={packagePath}, exportPath={exportPath}, objectName={objectName}");
                    
                    if (!string.IsNullOrEmpty(packagePath) && !string.IsNullOrEmpty(ownPackagePath) && 
                        packagePath.Equals(ownPackagePath, StringComparison.OrdinalIgnoreCase))
                    {
                        if (debugMode)
                            Console.Error.WriteLine($"[BuildImportMap] SKIPPING self-reference: {packagePath}");
                        zenPackage.ImportMap.Add(FPackageObjectIndex.CreateNull());
                    }
                    else if (!string.IsNullOrEmpty(packagePath) && !string.IsNullOrEmpty(exportPath))
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
                        // Verbose logging disabled for parallel performance
                    }
                    else
                    {
                        // Fallback to Null if we can't resolve the package
                        zenPackage.ImportMap.Add(FPackageObjectIndex.CreateNull());
                        // Warning logging disabled for parallel performance
                    }
                }
            }
        }
    }

    private static FMappedName MapNameToZen(FZenPackage zenPackage, string? name, int number = 0)
    {
        string nameStr = name ?? "None";
        int index = zenPackage.NameMap.IndexOf(nameStr);
        if (index == -1)
        {
            index = zenPackage.NameMap.Count;
            zenPackage.NameMap.Add(nameStr);
            // Verbose logging disabled for parallel performance
        }
        return new FMappedName((uint)index, (uint)number);
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
        // FName Number produces _1, _2, etc. but game files use _01, _02 format
        // Convert FName number to game's format with leading zero
        string basePath = current.ObjectName?.Value?.Value ?? "";
        int number = current.ObjectName?.Number ?? 0;
        if (number > 0)
        {
            // Number=2 means suffix _1 in FName, but game uses _01
            return basePath + "_" + (number - 1).ToString("D2");
        }
        return basePath;
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
            // Convert FName number to game's _01, _02 format
            string baseName = current.ObjectName?.Value?.Value ?? "";
            int number = current.ObjectName?.Number ?? 0;
            if (number > 0)
            {
                parts.Add(baseName + "_" + (number - 1).ToString("D2"));
            }
            else
            {
                parts.Add(baseName);
            }
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

        // Calculate total string bytes using ASCII encoding for all names
        // Non-ASCII characters are replaced with '?' (0x3F), matching MagikIO behavior
        uint totalStringBytes = 0;
        foreach (var name in names)
        {
            totalStringBytes += (uint)Encoding.ASCII.GetByteCount(name);
        }
        writer.Write(totalStringBytes);

        // Hash algorithm ID
        writer.Write(FNAME_HASH_ALGORITHM_ID);

        // Write hashes (CityHash64 of lowercase ASCII name)
        foreach (var name in names)
        {
            string lower = name.ToLowerInvariant();
            byte[] bytes = Encoding.ASCII.GetBytes(lower);
            ulong hash = IoStore.CityHash.CityHash64(bytes, 0, bytes.Length);
            writer.Write(hash);
        }

        // Write headers (i16 big-endian: positive = ASCII byte length)
        foreach (var name in names)
        {
            short len = (short)Encoding.ASCII.GetByteCount(name);
            // Write as big-endian
            writer.Write((byte)(len >> 8));
            writer.Write((byte)(len & 0xFF));
        }

        // Write string data - ASCII encoding, consecutive
        foreach (var name in names)
        {
            writer.Write(Encoding.ASCII.GetBytes(name));
        }
    }

    private static bool IsAscii(string s)
    {
        foreach (char c in s)
            if (c > 127) return false;
        return true;
    }
    
    /// <summary>
    /// Normalize a path by resolving .. segments
    /// Example: /Game/Marvel/../../../Marvel/Content/Marvel/Characters -> /Marvel/Content/Marvel/Characters
    /// </summary>
    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path) || !path.Contains(".."))
            return path;
        
        var parts = path.Split('/');
        var stack = new List<string>();
        
        foreach (var part in parts)
        {
            if (part == "..")
            {
                if (stack.Count > 0)
                    stack.RemoveAt(stack.Count - 1);
            }
            else if (!string.IsNullOrEmpty(part) && part != ".")
            {
                stack.Add(part);
            }
        }
        
        string result = string.Join("/", stack);
        if (path.StartsWith("/"))
            result = "/" + result;
        return result;
    }

    /// <summary>
    /// Build export bundles - groups of exports that are loaded together
    /// For UE5.3+ (NoExportInfo), all exports go into a single bundle
    /// The order of entries is determined by export dependencies (topological sort)
    /// </summary>
    private static void BuildExportBundles(UAsset asset, FZenPackage zenPackage)
    {
        // For UE5.3+ NoExportInfo version, we create a single export bundle containing all exports
        // Each export gets two entries: Create and Serialize
        // The order must respect dependencies - exports that depend on others must come after
        
        // Build dependency map from export dependencies
        var depsMap = new Dictionary<int, IList<int>>();
        for (int i = 0; i < asset.Exports.Count; i++)
        {
            var export = asset.Exports[i];
            var deps = new List<int>();
            
            // Add dependencies from SerializationBeforeSerializationDependencies
            if (export.SerializationBeforeSerializationDependencies != null)
            {
                foreach (var dep in export.SerializationBeforeSerializationDependencies)
                {
                    if (dep.IsExport() && dep.Index > 0 && dep.Index <= asset.Exports.Count)
                    {
                        deps.Add(dep.Index - 1); // Convert to 0-based
                    }
                }
            }
            
            // Add dependencies from CreateBeforeSerializationDependencies
            if (export.CreateBeforeSerializationDependencies != null)
            {
                foreach (var dep in export.CreateBeforeSerializationDependencies)
                {
                    if (dep.IsExport() && dep.Index > 0 && dep.Index <= asset.Exports.Count)
                    {
                        deps.Add(dep.Index - 1);
                    }
                }
            }
            
            // Add dependencies from SerializationBeforeCreateDependencies
            if (export.SerializationBeforeCreateDependencies != null)
            {
                foreach (var dep in export.SerializationBeforeCreateDependencies)
                {
                    if (dep.IsExport() && dep.Index > 0 && dep.Index <= asset.Exports.Count)
                    {
                        deps.Add(dep.Index - 1);
                    }
                }
            }
            
            // Add dependencies from CreateBeforeCreateDependencies
            if (export.CreateBeforeCreateDependencies != null)
            {
                foreach (var dep in export.CreateBeforeCreateDependencies)
                {
                    if (dep.IsExport() && dep.Index > 0 && dep.Index <= asset.Exports.Count)
                    {
                        deps.Add(dep.Index - 1);
                    }
                }
            }
            
            // Add OuterIndex as dependency (outer must be created before inner)
            if (export.OuterIndex.Index > 0)
            {
                deps.Add(export.OuterIndex.Index - 1);
            }
            
            depsMap[i] = deps.Distinct().ToList();
        }
        
        // Topological sort to get load order
        var sortedOrder = TopologicalSort(Enumerable.Range(0, asset.Exports.Count), depsMap);
        
        // Create a single bundle header
        var bundleHeader = new FExportBundleHeader
        {
            SerialOffset = 0,
            FirstEntryIndex = 0,
            EntryCount = (uint)(zenPackage.ExportMap.Count * 2) // Create + Serialize for each export
        };
        zenPackage.ExportBundleHeaders.Add(bundleHeader);
        
        // Add Create entries in dependency order, then Serialize entries in same order
        foreach (int i in sortedOrder)
        {
            zenPackage.ExportBundleEntries.Add(new FExportBundleEntry((uint)i, EExportCommandType.Create));
        }
        
        foreach (int i in sortedOrder)
        {
            zenPackage.ExportBundleEntries.Add(new FExportBundleEntry((uint)i, EExportCommandType.Serialize));
        }
        
        // Verbose logging disabled for parallel performance
    }
    
    /// <summary>
    /// Topological sort - returns items in dependency order (dependencies first)
    /// </summary>
    private static List<int> TopologicalSort(IEnumerable<int> items, Dictionary<int, IList<int>> dependencies)
    {
        var sorted = new List<int>();
        var visited = new HashSet<int>();
        
        foreach (var item in items)
        {
            TopologicalSortVisit(item, visited, sorted, dependencies);
        }
        
        return sorted;
    }
    
    private static void TopologicalSortVisit(int item, HashSet<int> visited, List<int> sorted, Dictionary<int, IList<int>> dependencies)
    {
        if (visited.Contains(item))
            return;
            
        visited.Add(item);
        
        if (dependencies.TryGetValue(item, out var deps))
        {
            foreach (var dep in deps)
            {
                TopologicalSortVisit(dep, visited, sorted, dependencies);
            }
        }
        
        sorted.Add(item);
    }

    /// <summary>
    /// Build dependency bundles - defines load order dependencies between exports
    /// For UE5.3+ (NoExportInfo), this replaces the legacy graph data
    /// </summary>
    private static void BuildDependencyBundles(UAsset asset, FZenPackage zenPackage)
    {
        bool debugMode = Environment.GetEnvironmentVariable("DEBUG") == "1";
        
        // For each export, create a dependency bundle header
        // Preserve actual preload dependencies from the legacy asset
        
        int currentEntryIndex = 0;
        
        for (int i = 0; i < zenPackage.ExportMap.Count; i++)
        {
            var export = asset.Exports[i];
            
            // Collect dependencies from all 4 preload dependency arrays
            var createBeforeCreate = new List<FPackageIndex>();
            var serializeBeforeCreate = new List<FPackageIndex>();
            var createBeforeSerialize = new List<FPackageIndex>();
            var serializeBeforeSerialize = new List<FPackageIndex>();
            
            // Preserve actual preload dependencies from legacy asset
            if (export.CreateBeforeCreateDependencies != null)
            {
                foreach (var dep in export.CreateBeforeCreateDependencies)
                    createBeforeCreate.Add(new FPackageIndex(dep.Index));
            }
            if (export.SerializationBeforeCreateDependencies != null)
            {
                foreach (var dep in export.SerializationBeforeCreateDependencies)
                    serializeBeforeCreate.Add(new FPackageIndex(dep.Index));
            }
            if (export.CreateBeforeSerializationDependencies != null)
            {
                foreach (var dep in export.CreateBeforeSerializationDependencies)
                    createBeforeSerialize.Add(new FPackageIndex(dep.Index));
            }
            if (export.SerializationBeforeSerializationDependencies != null)
            {
                foreach (var dep in export.SerializationBeforeSerializationDependencies)
                    serializeBeforeSerialize.Add(new FPackageIndex(dep.Index));
            }
            
            if (debugMode)
            {
                Console.Error.WriteLine($"[BuildDepBundles] Export[{i}]: CBC={createBeforeCreate.Count}, SBC={serializeBeforeCreate.Count}, CBS={createBeforeSerialize.Count}, SBS={serializeBeforeSerialize.Count}");
            }
            
            // Create dependency header for this export
            var depHeader = new FDependencyBundleHeader
            {
                FirstEntryIndex = currentEntryIndex,
                CreateBeforeCreateDependencies = (uint)createBeforeCreate.Count,
                SerializeBeforeCreateDependencies = (uint)serializeBeforeCreate.Count,
                CreateBeforeSerializeDependencies = (uint)createBeforeSerialize.Count,
                SerializeBeforeSerializeDependencies = (uint)serializeBeforeSerialize.Count
            };
            
            // Add dependency entries in order: CBC, SBC, CBS, SBS
            foreach (var dep in createBeforeCreate)
            {
                zenPackage.DependencyBundleEntries.Add(new FDependencyBundleEntry(dep));
                currentEntryIndex++;
            }
            foreach (var dep in serializeBeforeCreate)
            {
                zenPackage.DependencyBundleEntries.Add(new FDependencyBundleEntry(dep));
                currentEntryIndex++;
            }
            foreach (var dep in createBeforeSerialize)
            {
                zenPackage.DependencyBundleEntries.Add(new FDependencyBundleEntry(dep));
                currentEntryIndex++;
            }
            foreach (var dep in serializeBeforeSerialize)
            {
                zenPackage.DependencyBundleEntries.Add(new FDependencyBundleEntry(dep));
                currentEntryIndex++;
            }
            
            zenPackage.DependencyBundleHeaders.Add(depHeader);
        }
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
        
        // Write name map using standard UE name batch format
        // (ASCII for ASCII-only names, UTF-16LE for non-ASCII names)
        int nameMapNamesOffset = (int)writer.BaseStream.Position;
        WriteNameBatch(writer, zenPackage.NameMap);
        int nameMapNamesSize = (int)writer.BaseStream.Position - nameMapNamesOffset;
        
        // For UE5.3+ format, hashes are included in the name batch, so no separate hash section
        int nameMapHashesOffset = (int)writer.BaseStream.Position;

        // Write bulk data map entries from legacy asset's DataResources
        // UE5.3+ expects this field after the name map for packages with DataResources support
        // Each FBulkDataMapEntry is 32 bytes: serial_offset(8) + duplicate_serial_offset(8) + serial_size(8) + flags(4) + pad(4)
        // 
        // IMPORTANT: If we have a .ubulk file, we need to validate/fix the offsets to match the actual file
        long ubulkSize = 0;
        string ubulkPath = Path.ChangeExtension(asset.FilePath, ".ubulk");
        if (File.Exists(ubulkPath))
        {
            ubulkSize = new FileInfo(ubulkPath).Length;
        }
        
        if (asset.DataResources != null && asset.DataResources.Count > 0 && ubulkSize > 0)
        {
            // Validate that bulk data map entries fit within the .ubulk file
            // If they don't, create a single entry covering the entire .ubulk
            bool entriesValid = true;
            foreach (var resource in asset.DataResources)
            {
                if (resource.SerialOffset + resource.SerialSize > ubulkSize)
                {
                    entriesValid = false;
                    break;
                }
            }
            
            if (entriesValid)
            {
                // Use existing entries
                long bulkDataMapSize = asset.DataResources.Count * 32;
                writer.Write(bulkDataMapSize);
                
                // Verbose logging disabled for parallel performance
                int idx = 0;
                foreach (var resource in asset.DataResources)
                {
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
                // Entries don't match .ubulk size - create single entry for entire file
                // Verbose logging disabled for parallel performance
                writer.Write((long)32); // 1 entry * 32 bytes
                writer.Write((long)0); // SerialOffset = 0
                writer.Write((long)-1); // DuplicateSerialOffset = -1 (none)
                writer.Write(ubulkSize); // SerialSize = entire file
                writer.Write((uint)0x00010501); // Flags: PayloadAtEndOfFile | PayloadInSeperateFile | SingleUse
                writer.Write((uint)0); // padding
                // Verbose logging disabled for parallel performance
            }
        }
        else if (asset.DataResources != null && asset.DataResources.Count > 0)
        {
            // No .ubulk file but we have DataResources - write them as-is
            long bulkDataMapSize = asset.DataResources.Count * 32;
            writer.Write(bulkDataMapSize);
            
            // Verbose logging disabled for parallel performance
            int idx = 0;
            foreach (var resource in asset.DataResources)
            {
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
        // Verbose logging disabled for parallel performance

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
                header.Write(writer, containerVersion);
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

        // Write the full .uexp data as export data (do NOT strip trailing PACKAGE_FILE_TAG)
        // retoc preserves the complete .uexp contents including the 4-byte tag
        int exportDataLength = uexpData.Length;
        writer.Write(uexpData, 0, exportDataLength);
        
        // CookedHeaderSize = the legacy .uasset file size
        // This is what retoc uses and what the engine expects for serial offset calculations
        int cookedHeaderSize = (int)new FileInfo(asset.FilePath).Length;

        // Update summary with calculated offsets
        zenPackage.Summary.HeaderSize = (uint)zenHeaderSize;
        zenPackage.Summary.CookedHeaderSize = (uint)cookedHeaderSize;
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
        
        // Verbose logging disabled for parallel performance
    }

    private static UAsset LoadAsset(string filePath, string? usmapPath)
    {
        UAssetAPI.Unversioned.Usmap? mappings = null;
        
        if (!string.IsNullOrEmpty(usmapPath) && File.Exists(usmapPath))
        {
            // Use cached usmap to avoid parsing the same file 863+ times
            lock (_usmapLock)
            {
                if (!_usmapCache.TryGetValue(usmapPath, out mappings))
                {
                    mappings = new UAssetAPI.Unversioned.Usmap(usmapPath);
                    _usmapCache[usmapPath] = mappings;
                }
            }
        }

        var asset = new UAsset(filePath, EngineVersion.VER_UE5_3, mappings);
        asset.UseSeparateBulkDataFiles = true;
        return asset;
    }
    
    /// <summary>
    /// Re-serialize the asset's export data using UAssetAPI's proper serialization.
    /// This ensures types like StringTable get proper FGameplayTagContainer padding automatically.
    /// Returns just the export data portion (equivalent to .uexp content) and updates asset's SerialSize values.
    /// </summary>
    private static byte[]? ReserializeExportData(UAsset asset)
    {
        try
        {
            // Store original offsets before re-serialization
            long originalExportStart = asset.Exports.Min(e => e.SerialOffset);
            
            // Use UAssetAPI's WriteData to get properly serialized data
            // This also updates the asset's SerialOffset and SerialSize values
            using var fullStream = asset.WriteData();
            
            if (fullStream == null || fullStream.Length == 0)
                return null;
            
            // After WriteData, the asset's SerialOffset values are updated
            // Find where export data starts in the new serialization
            long newExportStart = asset.Exports.Min(e => e.SerialOffset);
            
            // Export data goes from exportStart to end of stream
            long exportDataLength = fullStream.Length - newExportStart;
            
            if (exportDataLength <= 0)
                return null;
            
            // Extract just the export data portion
            byte[] exportData = new byte[exportDataLength];
            fullStream.Seek(newExportStart, SeekOrigin.Begin);
            fullStream.Read(exportData, 0, (int)exportDataLength);
            
            Console.Error.WriteLine($"[ZenConverter] Re-serialized export data: {exportDataLength} bytes (from offset {newExportStart})");
            return exportData;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ZenConverter] Failed to re-serialize export data: {ex.Message}");
            return null;
        }
    }
    
    /// <summary>
    /// Calculate how much padding will be needed for a StringTable.
    /// Each string table entry (Key + Value) needs 4 bytes (FGameplayTagContainer count=0).
    /// Plus 4 bytes trailing padding at the end.
    /// 
    /// StringTable structure in .uexp:
    /// - FString TableNamespace
    /// - int32 NumEntries
    /// - For each entry: FString Key, FString Value, [FGameplayTagContainer - needs to be added]
    /// - [FGameplayTagContainer trailing - needs to be added]
    /// </summary>
    private static int CalculateStringTablePadding(byte[] uexpData)
    {
        const int PADDING_SIZE = 4; // Empty FGameplayTagContainer = int32 count of 0
        int dataLength = uexpData.Length;
        
        // StringTable has a simple structure:
        // 1. TableNamespace (FString: int32 length + chars + null)
        // 2. NumEntries (int32)
        // 3. For each entry: Key FString + Value FString
        
        // Search for the entry count - look for a reasonable int32 followed by FString patterns
        // FString format: int32 length (positive, reasonable size) followed by ASCII/UTF-8 chars
        
        for (int i = 0; i < dataLength - 20; i++)
        {
            // First, try to find the TableNamespace FString
            int nsLen = BitConverter.ToInt32(uexpData, i);
            if (nsLen <= 0 || nsLen > 256)
                continue;
            
            // Check if the string content looks valid (printable ASCII or null terminator)
            bool validString = true;
            for (int j = 0; j < Math.Min(nsLen, 10); j++)
            {
                byte b = uexpData[i + 4 + j];
                if (b != 0 && (b < 32 || b > 126))
                {
                    validString = false;
                    break;
                }
            }
            if (!validString)
                continue;
            
            // After the namespace string, we should have the entry count
            int entryCountOffset = i + 4 + nsLen;
            if (entryCountOffset + 4 > dataLength)
                continue;
            
            int entryCount = BitConverter.ToInt32(uexpData, entryCountOffset);
            if (entryCount <= 0 || entryCount > 10000)
                continue;
            
            // Validate by checking if next bytes look like an FString (key)
            int keyOffset = entryCountOffset + 4;
            if (keyOffset + 4 > dataLength)
                continue;
            
            int keyLen = BitConverter.ToInt32(uexpData, keyOffset);
            if (keyLen <= 0 || keyLen > 1024)
                continue;
            
            // Looks like a valid StringTable structure
            // Each entry needs 4 bytes padding + 4 bytes trailing
            int totalPadding = (entryCount * PADDING_SIZE) + PADDING_SIZE;
            Console.Error.WriteLine($"[ZenConverter] Found StringTable: namespace at 0x{i:X}, {entryCount} entries, will add {totalPadding} bytes padding");
            return totalPadding;
        }
        
        return 0;
    }
    
    /// <summary>
    /// Patch StringTable .uexp data by adding 4-byte FGameplayTagContainer padding after each entry.
    /// Marvel Rivals expects an FGameplayTagContainer (empty = 4 bytes of zeros) after each Key+Value pair.
    /// Plus a trailing FGameplayTagContainer at the end.
    /// </summary>
    private static (byte[]? patchedData, int entryCount, int paddingAdded) PatchStringTableEntries(byte[] uexpData, int dataLength)
    {
        const int PADDING_SIZE = 4; // Empty FGameplayTagContainer = int32 count of 0
        
        // Find the StringTable structure
        int namespaceOffset = -1;
        int namespaceLen = 0;
        int entryCountOffset = -1;
        int entryCount = 0;
        int firstEntryOffset = -1;
        
        for (int i = 0; i < dataLength - 20; i++)
        {
            int nsLen = BitConverter.ToInt32(uexpData, i);
            if (nsLen <= 0 || nsLen > 256)
                continue;
            
            bool validString = true;
            for (int j = 0; j < Math.Min(nsLen, 10); j++)
            {
                byte b = uexpData[i + 4 + j];
                if (b != 0 && (b < 32 || b > 126))
                {
                    validString = false;
                    break;
                }
            }
            if (!validString)
                continue;
            
            int ecOffset = i + 4 + nsLen;
            if (ecOffset + 4 > dataLength)
                continue;
            
            int ec = BitConverter.ToInt32(uexpData, ecOffset);
            if (ec <= 0 || ec > 10000)
                continue;
            
            int keyOffset = ecOffset + 4;
            if (keyOffset + 4 > dataLength)
                continue;
            
            int keyLen = BitConverter.ToInt32(uexpData, keyOffset);
            if (keyLen <= 0 || keyLen > 1024)
                continue;
            
            namespaceOffset = i;
            namespaceLen = nsLen;
            entryCountOffset = ecOffset;
            entryCount = ec;
            firstEntryOffset = keyOffset;
            Console.Error.WriteLine($"[ZenConverter] Found StringTable for patching: namespace=\"{System.Text.Encoding.UTF8.GetString(uexpData, i + 4, Math.Min(nsLen - 1, 50))}\", {entryCount} entries");
            break;
        }
        
        if (entryCount == 0 || firstEntryOffset < 0)
        {
            Console.Error.WriteLine($"[ZenConverter] No StringTable structure found to patch");
            return (null, 0, 0);
        }
        
        // Calculate new size: original + (entryCount * 4) + 4 trailing
        int paddingTotal = (entryCount * PADDING_SIZE) + PADDING_SIZE;
        int newLength = dataLength + paddingTotal;
        byte[] patchedData = new byte[newLength];
        
        // Copy data up to first entry
        Array.Copy(uexpData, 0, patchedData, 0, firstEntryOffset);
        
        int srcOffset = firstEntryOffset;
        int dstOffset = firstEntryOffset;
        
        // For each entry, copy Key + Value, then add 4 bytes padding
        for (int e = 0; e < entryCount; e++)
        {
            // Read and copy Key FString
            if (srcOffset + 4 > dataLength)
                break;
            int keyLen = BitConverter.ToInt32(uexpData, srcOffset);
            int keyTotalLen = 4 + keyLen; // length field + string bytes
            if (srcOffset + keyTotalLen > dataLength)
                break;
            Array.Copy(uexpData, srcOffset, patchedData, dstOffset, keyTotalLen);
            srcOffset += keyTotalLen;
            dstOffset += keyTotalLen;
            
            // Read and copy Value FString
            if (srcOffset + 4 > dataLength)
                break;
            int valLen = BitConverter.ToInt32(uexpData, srcOffset);
            int valTotalLen = 4 + valLen;
            if (srcOffset + valTotalLen > dataLength)
                break;
            Array.Copy(uexpData, srcOffset, patchedData, dstOffset, valTotalLen);
            srcOffset += valTotalLen;
            dstOffset += valTotalLen;
            
            // Add 4 bytes padding (empty FGameplayTagContainer: count = 0)
            patchedData[dstOffset] = 0x00;
            patchedData[dstOffset + 1] = 0x00;
            patchedData[dstOffset + 2] = 0x00;
            patchedData[dstOffset + 3] = 0x00;
            dstOffset += PADDING_SIZE;
        }
        
        // Add trailing 4 bytes padding
        patchedData[dstOffset] = 0x00;
        patchedData[dstOffset + 1] = 0x00;
        patchedData[dstOffset + 2] = 0x00;
        patchedData[dstOffset + 3] = 0x00;
        dstOffset += PADDING_SIZE;
        
        // Copy remaining data after StringTable entries
        int remainingBytes = dataLength - srcOffset;
        if (remainingBytes > 0)
        {
            Array.Copy(uexpData, srcOffset, patchedData, dstOffset, remainingBytes);
        }
        
        Console.Error.WriteLine($"[ZenConverter] Patched StringTable: {entryCount} entries with FGameplayTagContainer padding (+{paddingTotal} bytes)");
        return (patchedData, entryCount, paddingTotal);
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
    
    // Export reorder mapping: old index -> new index
    public Dictionary<int, int> ExportReorderMap { get; set; }

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
        ExportReorderMap = new Dictionary<int, int>();
    }
}
