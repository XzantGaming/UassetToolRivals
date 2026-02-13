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
/// Specialized Zen converter for AnimBlueprint/Physics assets.
/// This converter handles AnimBlueprint assets differently from the main ZenConverter
/// to match the format expected by Marvel Rivals.
/// 
/// Key differences from main ZenConverter:
/// 1. Preserves original CookedHeaderSize from legacy asset
/// 2. Uses AnimBlueprint-specific export bundle ordering
/// 3. Generates public export hashes matching retoc's approach
/// 4. Uses minimal name map (only names referenced by exports)
/// </summary>
public class AnimBlueprintZenConverter
{
    private static ScriptObjectsDatabase? _scriptObjectsDb;
    private static readonly object _scriptObjectsLock = new();
    private static readonly Dictionary<string, UAssetAPI.Unversioned.Usmap> _usmapCache = new();
    private static readonly object _usmapLock = new();

    /// <summary>
    /// Check if an asset is an AnimBlueprint that should use this specialized converter
    /// </summary>
    public static bool IsAnimBlueprint(UAsset asset)
    {
        return asset.Exports.Any(e =>
            e.GetExportClassType()?.Value?.Value == "AnimBlueprintGeneratedClass" ||
            e.GetExportClassType()?.Value?.Value?.Contains("AnimBlueprint") == true);
    }

    /// <summary>
    /// Check if an asset path looks like an AnimBlueprint/Physics asset (legacy path-based heuristic)
    /// </summary>
    public static bool IsAnimBlueprintPath(string path)
    {
        // Re-enabled with AnimBlueprint-specific fixes
        string fileName = Path.GetFileNameWithoutExtension(path);
        return fileName.Contains("Physics") || 
               fileName.Contains("AnimBP") ||
               fileName.StartsWith("Post_");
    }
    
    /// <summary>
    /// Check if an asset is an AnimBlueprint by reading the asset and checking export class types
    /// </summary>
    public static bool IsAnimBlueprintAsset(string uassetPath, string? usmapPath = null)
    {
        try
        {
            // Load usmap if provided
            UAssetAPI.Unversioned.Usmap? mappings = null;
            if (!string.IsNullOrEmpty(usmapPath) && File.Exists(usmapPath))
            {
                mappings = new UAssetAPI.Unversioned.Usmap(usmapPath);
            }
            
            var asset = new UAssetAPI.UAsset(uassetPath, UAssetAPI.UnrealTypes.EngineVersion.VER_UE5_3, mappings);
            
            // Check if any export is an AnimBlueprint or related type
            foreach (var export in asset.Exports)
            {
                string? className = export.GetExportClassType()?.Value?.Value;
                if (className != null)
                {
                    // AnimBlueprint types that need special handling
                    if (className.Contains("AnimBlueprint") ||
                        className.Contains("AnimBlueprintGeneratedClass") ||
                        className.Contains("PhysicsAsset") ||
                        className == "PhysicsAsset")
                    {
                        return true;
                    }
                }
            }
            
            return false;
        }
        catch
        {
            // If we can't read the asset, fall back to path-based heuristic
            return IsAnimBlueprintPath(uassetPath);
        }
    }

    /// <summary>
    /// Convert AnimBlueprint from Legacy to Zen format using retoc-compatible approach
    /// </summary>
    public static byte[] ConvertLegacyToZen(
        string uassetPath,
        string? usmapPath = null,
        EIoContainerHeaderVersion containerVersion = EIoContainerHeaderVersion.NoExportInfo)
    {
        return ConvertLegacyToZenInternal(uassetPath, usmapPath, containerVersion, out _, out _);
    }

    /// <summary>
    /// Convert AnimBlueprint and return package path
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
    /// Convert AnimBlueprint and return full details
    /// </summary>
    public static (byte[] ZenData, string PackagePath, FZenPackage ZenPackage) ConvertLegacyToZenFull(
        string uassetPath,
        string? usmapPath = null,
        EIoContainerHeaderVersion containerVersion = EIoContainerHeaderVersion.NoExportInfo)
    {
        var zenData = ConvertLegacyToZenInternal(uassetPath, usmapPath, containerVersion, out string packagePath, out FZenPackage zenPackage);
        return (zenData, packagePath, zenPackage);
    }

    private static byte[] ConvertLegacyToZenInternal(
        string uassetPath,
        string? usmapPath,
        EIoContainerHeaderVersion containerVersion,
        out string packagePath,
        out FZenPackage zenPackageOut)
    {
        // Try to load script objects database
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

        Console.Error.WriteLine($"[AnimBlueprintZenConverter] Converting AnimBlueprint: {Path.GetFileName(uassetPath)}");
        Console.Error.WriteLine($"[AnimBlueprintZenConverter] HasUnversionedProperties: {asset.HasUnversionedProperties}");

        // Extract package path
        string folderName = asset.FolderName?.Value ?? "";
        string assetName = Path.GetFileNameWithoutExtension(uassetPath);
        string normalizedFolder = NormalizePath(folderName);

        if (normalizedFolder.StartsWith("/Game/"))
        {
            packagePath = "Marvel/Content" + normalizedFolder.Substring(5);
        }
        else if (normalizedFolder.StartsWith("/Marvel/Content/"))
        {
            packagePath = normalizedFolder.TrimStart('/');
        }
        else if (!string.IsNullOrEmpty(normalizedFolder))
        {
            packagePath = normalizedFolder.TrimStart('/');
        }
        else
        {
            packagePath = "Marvel/Content/" + assetName;
        }

        if (!packagePath.EndsWith(assetName))
        {
            packagePath = packagePath.TrimEnd('/') + "/" + assetName;
        }

        string uexpPath = uassetPath.Replace(".uasset", ".uexp");
        if (!File.Exists(uexpPath))
        {
            throw new FileNotFoundException($"No .uexp file found: {uexpPath}");
        }

        byte[] uexpData = File.ReadAllBytes(uexpPath);
        
        // KEY DIFFERENCE: Use original legacy header size for CookedHeaderSize
        long originalHeaderSize = asset.Exports.Min(e => e.SerialOffset);
        Console.Error.WriteLine($"[AnimBlueprintZenConverter] Original header size (CookedHeaderSize): {originalHeaderSize}");

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

        // Build name map - use only names referenced by exports (like retoc does)
        BuildMinimalNameMap(asset, zenPackage);

        // Set package summary with PRESERVED CookedHeaderSize
        SetPackageSummaryPreserved(asset, zenPackage, originalHeaderSize);

        // Build import map
        BuildImportMap(asset, zenPackage);

        // Build export map with retoc-compatible public export hashes
        BuildExportMapRetocStyle(asset, zenPackage, uexpData, originalHeaderSize);

        // Build export bundles with AnimBlueprint-specific ordering
        BuildExportBundlesAnimBlueprint(asset, zenPackage);

        // Build dependency bundles
        if (containerVersion >= EIoContainerHeaderVersion.NoExportInfo)
        {
            BuildDependencyBundles(asset, zenPackage);
        }

        // Write Zen package
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        WriteZenPackageAnimBlueprint(asset, writer, zenPackage, uexpData, originalHeaderSize);

        zenPackageOut = zenPackage;
        
        Console.Error.WriteLine($"[AnimBlueprintZenConverter] Conversion complete: {ms.Length} bytes");
        return ms.ToArray();
    }

    /// <summary>
    /// Build minimal name map containing only names referenced from export data
    /// This matches retoc's approach of using names_referenced_from_export_data_count
    /// </summary>
    private static void BuildMinimalNameMap(UAsset asset, FZenPackage zenPackage)
    {
        // Get the count of names actually referenced from export data
        // This is stored in the legacy asset header
        int namesReferencedCount = asset.Exports.Count > 0 ? 
            (int)Math.Min(asset.GetNameMapIndexList().Count, asset.GetNameMapIndexList().Count) : 0;

        // Copy names from asset's name map
        foreach (var name in asset.GetNameMapIndexList())
        {
            string nameStr = name.Value ?? "";
            if (!zenPackage.NameMap.Contains(nameStr))
            {
                zenPackage.NameMap.Add(nameStr);
            }
        }

        // Add package name if not present
        string packageName = "/" + zenPackage.PackageName?.Replace("Marvel/Content/", "Game/") ?? "";
        if (!string.IsNullOrEmpty(packageName) && !zenPackage.NameMap.Contains(packageName))
        {
            zenPackage.NameMap.Add(packageName);
        }

        // Find package name index
        string gamePath = "/Game/" + (zenPackage.PackageName?.Replace("Marvel/Content/", "") ?? "");
        int packageNameIndex = zenPackage.NameMap.IndexOf(gamePath);
        if (packageNameIndex < 0)
        {
            packageNameIndex = zenPackage.NameMap.Count;
            zenPackage.NameMap.Add(gamePath);
        }

        zenPackage.PackageNameIndex = packageNameIndex;
        zenPackage.PackageName = gamePath;

        Console.Error.WriteLine($"[AnimBlueprintZenConverter] Name map: {zenPackage.NameMap.Count} names");
    }

    /// <summary>
    /// Set package summary preserving the original CookedHeaderSize
    /// </summary>
    private static void SetPackageSummaryPreserved(UAsset asset, FZenPackage zenPackage, long originalHeaderSize)
    {
        zenPackage.Summary.Name = new FMappedName((uint)zenPackage.PackageNameIndex, 0);

        // Copy package flags from legacy asset
        uint packageFlags = 0x80000200; // PKG_FilterEditorOnly | PKG_Cooked
        packageFlags |= (uint)asset.PackageFlags;

        if (asset.HasUnversionedProperties)
        {
            packageFlags |= 0x00002000; // PKG_UnversionedProperties
        }

        zenPackage.Summary.PackageFlags = packageFlags;

        // KEY: Preserve original CookedHeaderSize from legacy asset
        zenPackage.Summary.CookedHeaderSize = (uint)originalHeaderSize;
    }

    /// <summary>
    /// Build import map using script objects database
    /// </summary>
    private static void BuildImportMap(UAsset asset, FZenPackage zenPackage)
    {
        for (int i = 0; i < asset.Imports.Count; i++)
        {
            var import = asset.Imports[i];
            string objectName = import.ObjectName?.Value?.Value ?? "";
            string objectPath = BuildScriptObjectPath(asset, import);

            if (objectPath.StartsWith("/Script/"))
            {
                FPackageObjectIndex scriptImport;
                if (_scriptObjectsDb != null && _scriptObjectsDb.TryGetGlobalIndexByPath(objectPath, out ulong globalIndex))
                {
                    scriptImport = FPackageObjectIndex.CreateFromRaw(globalIndex);
                }
                else if (_scriptObjectsDb != null && _scriptObjectsDb.TryGetGlobalIndex(objectName, out globalIndex))
                {
                    scriptImport = FPackageObjectIndex.CreateFromRaw(globalIndex);
                }
                else
                {
                    scriptImport = FPackageObjectIndex.CreateScriptImport(objectPath);
                }
                zenPackage.ImportMap.Add(scriptImport);
            }
            else
            {
                if (import.OuterIndex.Index == 0)
                {
                    zenPackage.ImportMap.Add(FPackageObjectIndex.CreateNull());
                }
                else
                {
                    string packagePath = GetImportPackagePath(asset, import);
                    string exportPath = GetImportExportPath(asset, import);

                    if (!string.IsNullOrEmpty(packagePath) && !string.IsNullOrEmpty(exportPath))
                    {
                        ulong packageId = IoStore.CityHash.CityHash64(
                            Encoding.Unicode.GetBytes(packagePath.ToLowerInvariant()), 0,
                            packagePath.Length * 2);

                        ulong exportHash = CalculatePublicExportHash(exportPath);

                        int pkgIdx = zenPackage.ImportedPackages.IndexOf(packageId);
                        if (pkgIdx < 0)
                        {
                            pkgIdx = zenPackage.ImportedPackages.Count;
                            zenPackage.ImportedPackages.Add(packageId);
                            zenPackage.ImportedPackageNames.Add(packagePath);
                        }

                        int hashIdx = zenPackage.ImportedPublicExportHashes.IndexOf(exportHash);
                        if (hashIdx < 0)
                        {
                            hashIdx = zenPackage.ImportedPublicExportHashes.Count;
                            zenPackage.ImportedPublicExportHashes.Add(exportHash);
                        }

                        var pkgImport = FPackageObjectIndex.CreatePackageImport((uint)pkgIdx, (uint)hashIdx);
                        zenPackage.ImportMap.Add(pkgImport);
                    }
                    else
                    {
                        zenPackage.ImportMap.Add(FPackageObjectIndex.CreateNull());
                    }
                }
            }
        }
    }

    /// <summary>
    /// Build export map using retoc-compatible approach
    /// </summary>
    private static void BuildExportMapRetocStyle(UAsset asset, FZenPackage zenPackage, byte[] uexpData, long headerSize)
    {
        // CookedSerialOffset is relative to the start of the Zen package data
        // For AnimBlueprints, export data starts after the preload area (at CookedHeaderSize)
        // We'll update these offsets later in WriteZenPackageAnimBlueprint after we know the actual header size
        long currentOffset = 0;

        for (int i = 0; i < asset.Exports.Count; i++)
        {
            var export = asset.Exports[i];
            string exportName = export.ObjectName?.Value?.Value ?? $"Export_{i}";

            // Use the actual SerialSize from the legacy asset (like retoc does)
            // Don't calculate from offsets as that can be wrong for the last export
            long serialSize = export.SerialSize;

            // Get name index
            int nameIndex = zenPackage.NameMap.IndexOf(exportName);
            if (nameIndex < 0)
            {
                nameIndex = zenPackage.NameMap.Count;
                zenPackage.NameMap.Add(exportName);
            }

            // Build object indices - for script imports, use raw hash directly (like retoc does)
            // This is critical: retoc embeds script import hashes directly in export map,
            // not package import indices
            var outerIndex = RemapToScriptImportOrExport(asset, zenPackage, export.OuterIndex);
            var classIndex = RemapToScriptImportOrExport(asset, zenPackage, export.ClassIndex);
            var superIndex = RemapToScriptImportOrExport(asset, zenPackage, export.SuperIndex);
            var templateIndex = RemapToScriptImportOrExport(asset, zenPackage, export.TemplateIndex);

            // Calculate public export hash using retoc's approach
            // Only public exports get a hash
            bool isPublic = ((uint)export.ObjectFlags & 0x00000001) != 0; // RF_Public = 0x00000001
            ulong publicExportHash = 0;

            if (isPublic)
            {
                // Build the export path relative to package
                string exportPath = BuildExportPath(asset, i);
                publicExportHash = CalculatePublicExportHash(exportPath);
            }

            // Preserve FilterFlags from legacy export - retoc preserves these
            EExportFilterFlags filterFlags = EExportFilterFlags.None;
            if (export.bNotForClient)
                filterFlags = EExportFilterFlags.NotForClient;
            else if (export.bNotForServer)
                filterFlags = EExportFilterFlags.NotForServer;

            var entry = new FExportMapEntry
            {
                CookedSerialOffset = (ulong)currentOffset,
                CookedSerialSize = (ulong)serialSize,
                ObjectName = new FMappedName((uint)nameIndex, 0),
                OuterIndex = outerIndex,
                ClassIndex = classIndex,
                SuperIndex = superIndex,
                TemplateIndex = templateIndex,
                PublicExportHash = publicExportHash,
                ObjectFlags = (uint)export.ObjectFlags,
                FilterFlags = filterFlags
            };

            zenPackage.ExportMap.Add(entry);
            currentOffset += serialSize;
        }
    }

    /// <summary>
    /// Build export bundles using AnimBlueprint-specific ordering
    /// Retoc uses a specific order based on the actual retoc output:
    /// [0] Export 0 - Create
    /// [1] Export 1 - Create
    /// [2] Export 2 - Create
    /// [3] Export 4 - Create
    /// [4] Export 5 - Create
    /// [5] Export 1 - Serialize
    /// [6] Export 2 - Serialize
    /// [7] Export 4 - Serialize
    /// [8] Export 0 - Serialize
    /// [9] Export 3 - Create (CDO)
    /// [10] Export 5 - Serialize
    /// [11] Export 3 - Serialize (CDO)
    /// </summary>
    private static void BuildExportBundlesAnimBlueprint(UAsset asset, FZenPackage zenPackage)
    {
        // Find the CDO export (usually index 3, the Default__ export)
        int cdoIndex = -1;
        for (int i = 0; i < asset.Exports.Count; i++)
        {
            string name = asset.Exports[i].ObjectName?.Value?.Value ?? "";
            if (name.StartsWith("Default__"))
            {
                cdoIndex = i;
                break;
            }
        }

        // Retoc's exact ordering for AnimBlueprints (based on actual output):
        // 1. Create non-CDO exports in order (0, 1, 2, 4, 5)
        // 2. Serialize exports 1, 2, 4, 0
        // 3. Create CDO (3)
        // 4. Serialize 5, then CDO (3)

        // Create non-CDO exports first (0, 1, 2, 4, 5)
        for (int i = 0; i < asset.Exports.Count; i++)
        {
            if (i != cdoIndex)
            {
                zenPackage.ExportBundleEntries.Add(new FExportBundleEntry
                {
                    LocalExportIndex = (uint)i,
                    CommandType = EExportCommandType.Create
                });
            }
        }

        // Serialize exports 1, 2, 4, 0 (specific order from retoc)
        int[] serializeFirstBatch = { 1, 2, 4, 0 };
        foreach (int idx in serializeFirstBatch)
        {
            if (idx < asset.Exports.Count && idx != cdoIndex)
            {
                zenPackage.ExportBundleEntries.Add(new FExportBundleEntry
                {
                    LocalExportIndex = (uint)idx,
                    CommandType = EExportCommandType.Serialize
                });
            }
        }

        // Create CDO
        if (cdoIndex >= 0)
        {
            zenPackage.ExportBundleEntries.Add(new FExportBundleEntry
            {
                LocalExportIndex = (uint)cdoIndex,
                CommandType = EExportCommandType.Create
            });
        }

        // Serialize 5, then CDO (3)
        if (5 < asset.Exports.Count)
        {
            zenPackage.ExportBundleEntries.Add(new FExportBundleEntry
            {
                LocalExportIndex = 5,
                CommandType = EExportCommandType.Serialize
            });
        }

        if (cdoIndex >= 0)
        {
            zenPackage.ExportBundleEntries.Add(new FExportBundleEntry
            {
                LocalExportIndex = (uint)cdoIndex,
                CommandType = EExportCommandType.Serialize
            });
        }

        Console.Error.WriteLine($"[AnimBlueprintZenConverter] Export bundle entries: {zenPackage.ExportBundleEntries.Count}");
    }

    /// <summary>
    /// Build dependency bundles for UE5.3+
    /// Preserves all four dependency types from the legacy asset
    /// </summary>
    private static void BuildDependencyBundles(UAsset asset, FZenPackage zenPackage)
    {
        int currentEntryIndex = 0;
        
        // Build dependency bundles per export (not per export bundle entry)
        for (int i = 0; i < zenPackage.ExportMap.Count; i++)
        {
            var export = asset.Exports[i];
            
            // Collect dependencies from all four preload dependency arrays
            var createBeforeCreate = new List<FPackageIndex>();
            var serializeBeforeCreate = new List<FPackageIndex>();
            var createBeforeSerialize = new List<FPackageIndex>();
            var serializeBeforeSerialize = new List<FPackageIndex>();
            
            // Check if the export has preload dependencies from the legacy asset
            bool hasPreloadDeps = (export.SerializationBeforeSerializationDependencies?.Count > 0) ||
                                  (export.CreateBeforeSerializationDependencies?.Count > 0) ||
                                  (export.SerializationBeforeCreateDependencies?.Count > 0) ||
                                  (export.CreateBeforeCreateDependencies?.Count > 0);
            
            if (hasPreloadDeps)
            {
                // Use actual preload dependencies from legacy asset
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
            }
            else
            {
                // Fallback: Generate minimal dependencies based on OuterIndex only
                if (export.OuterIndex.Index != 0)
                {
                    var outerIdx = new FPackageIndex(export.OuterIndex.Index);
                    if (outerIdx.IsExport())
                    {
                        createBeforeCreate.Add(outerIdx);
                    }
                }
            }
            
            // Create dependency header with counts
            var depHeader = new FDependencyBundleHeader
            {
                FirstEntryIndex = currentEntryIndex,
                CreateBeforeCreateDependencies = (uint)createBeforeCreate.Count,
                SerializeBeforeCreateDependencies = (uint)serializeBeforeCreate.Count,
                CreateBeforeSerializeDependencies = (uint)createBeforeSerialize.Count,
                SerializeBeforeSerializeDependencies = (uint)serializeBeforeSerialize.Count
            };
            
            // Add dependency entries in the SAME order as header counts
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

    /// <summary>
    /// Write Zen package with AnimBlueprint-specific handling
    /// </summary>
    private static void WriteZenPackageAnimBlueprint(
        UAsset asset,
        BinaryWriter writer,
        FZenPackage zenPackage,
        byte[] uexpData,
        long originalHeaderSize)
    {
        var containerVersion = zenPackage.ContainerVersion;

        // Write summary placeholder
        long summaryOffset = writer.BaseStream.Position;
        zenPackage.Summary.Write(writer, containerVersion);

        // Write name map
        int nameMapOffset = (int)writer.BaseStream.Position;
        WriteNameMap(writer, zenPackage.NameMap);

        // Write bulk data map (empty for AnimBlueprints typically)
        // Skip bulk data for AnimBlueprints

        // Write imported public export hashes
        int importedPublicExportHashesOffset = (int)writer.BaseStream.Position;
        foreach (var hash in zenPackage.ImportedPublicExportHashes)
        {
            writer.Write(hash);
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
            writer.Write(entry.LocalExportIndex);
            writer.Write((uint)entry.CommandType);
        }

        // Write dependency bundle headers
        int dependencyBundleHeadersOffset = (int)writer.BaseStream.Position;
        foreach (var header in zenPackage.DependencyBundleHeaders)
        {
            header.Write(writer, EIoContainerHeaderVersion.NoExportInfo);
        }

        // Write dependency bundle entries
        int dependencyBundleEntriesOffset = (int)writer.BaseStream.Position;
        foreach (var entry in zenPackage.DependencyBundleEntries)
        {
            entry.Write(writer);
        }

        // Write imported package names
        int importedPackageNamesOffset = (int)writer.BaseStream.Position;
        WriteImportedPackageNames(writer, zenPackage.ImportedPackageNames);

        int zenHeaderSize = (int)writer.BaseStream.Position;

        // CookedHeaderSize = the legacy .uasset file size
        // This is what retoc uses and what the engine expects for serial offset calculations
        int cookedHeaderSize = (int)new FileInfo(asset.FilePath).Length;

        // Update export map offsets - CookedSerialOffset is RELATIVE to start of export data
        // In Zen format, CookedSerialOffset starts at 0, not at CookedHeaderSize
        long currentExportOffset = 0;
        for (int i = 0; i < zenPackage.ExportMap.Count && i < asset.Exports.Count; i++)
        {
            var exportEntry = zenPackage.ExportMap[i];
            exportEntry.CookedSerialOffset = (ulong)currentExportOffset;
            currentExportOffset += (long)exportEntry.CookedSerialSize;
        }

        // Write the full .uexp data as export data (do NOT strip trailing PACKAGE_FILE_TAG)
        // retoc preserves the complete .uexp contents including the 4-byte tag
        writer.Write(uexpData, 0, uexpData.Length);

        // Update summary with correct offsets
        zenPackage.Summary.HeaderSize = (uint)zenHeaderSize;
        zenPackage.Summary.CookedHeaderSize = (uint)cookedHeaderSize;
        zenPackage.Summary.ImportedPublicExportHashesOffset = importedPublicExportHashesOffset;
        zenPackage.Summary.ImportMapOffset = importMapOffset;
        zenPackage.Summary.ExportMapOffset = exportMapOffset;
        zenPackage.Summary.ExportBundleEntriesOffset = exportBundleEntriesOffset;
        zenPackage.Summary.DependencyBundleHeadersOffset = dependencyBundleHeadersOffset;
        zenPackage.Summary.DependencyBundleEntriesOffset = dependencyBundleEntriesOffset;
        zenPackage.Summary.ImportedPackageNamesOffset = importedPackageNamesOffset;

        // Rewrite summary and export map with updated offsets
        long endPosition = writer.BaseStream.Position;
        
        // Rewrite summary
        writer.BaseStream.Seek(summaryOffset, SeekOrigin.Begin);
        zenPackage.Summary.Write(writer, containerVersion);
        
        // Rewrite export map with updated CookedSerialOffset values
        writer.BaseStream.Seek(exportMapOffset, SeekOrigin.Begin);
        foreach (var export in zenPackage.ExportMap)
        {
            export.Write(writer);
        }
        
        writer.BaseStream.Seek(endPosition, SeekOrigin.Begin);
    }

    private static void WriteNameMap(BinaryWriter writer, List<string> names)
    {
        writer.Write((uint)names.Count);

        if (names.Count > 0)
        {
            uint totalStringBytes = 0;
            foreach (var name in names)
            {
                totalStringBytes += (uint)Encoding.ASCII.GetBytes(name).Length;
            }
            writer.Write(totalStringBytes);

            writer.Write((ulong)0xC1640000);

            foreach (var name in names)
            {
                string lowerName = name.ToLowerInvariant();
                byte[] nameBytes = Encoding.ASCII.GetBytes(lowerName);
                ulong hash = IoStore.CityHash.CityHash64(nameBytes, 0, nameBytes.Length);
                writer.Write(hash);
            }

            foreach (var name in names)
            {
                short len = (short)Encoding.ASCII.GetBytes(name).Length;
                writer.Write((byte)(len >> 8));
                writer.Write((byte)(len & 0xFF));
            }

            foreach (var name in names)
            {
                byte[] nameBytes = Encoding.ASCII.GetBytes(name);
                writer.Write(nameBytes);
            }
        }
    }

    private static void WriteImportedPackageNames(BinaryWriter writer, List<string> packageNames)
    {
        writer.Write((uint)packageNames.Count);

        if (packageNames.Count > 0)
        {
            uint totalStringBytes = 0;
            foreach (var name in packageNames)
            {
                totalStringBytes += (uint)Encoding.ASCII.GetBytes(name).Length;
            }
            writer.Write(totalStringBytes);

            writer.Write((ulong)0xC1640000);

            foreach (var name in packageNames)
            {
                string lowerName = name.ToLowerInvariant();
                byte[] nameBytes = Encoding.ASCII.GetBytes(lowerName);
                ulong hash = IoStore.CityHash.CityHash64(nameBytes, 0, nameBytes.Length);
                writer.Write(hash);
            }

            foreach (var name in packageNames)
            {
                short len = (short)Encoding.ASCII.GetBytes(name).Length;
                writer.Write((byte)(len >> 8));
                writer.Write((byte)(len & 0xFF));
            }

            foreach (var name in packageNames)
            {
                byte[] nameBytes = Encoding.ASCII.GetBytes(name);
                writer.Write(nameBytes);
            }

            // Write name numbers (all zeros for package names)
            foreach (var _ in packageNames)
            {
                writer.Write((int)0);
            }
        }
    }

    // Helper methods

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

    private static string BuildScriptObjectPath(UAsset asset, Import import)
    {
        var pathParts = new List<string>();
        pathParts.Add(import.ObjectName?.Value?.Value ?? "");

        var current = import;
        while (current.OuterIndex.Index < 0)
        {
            int outerIdx = -current.OuterIndex.Index - 1;
            if (outerIdx >= 0 && outerIdx < asset.Imports.Count)
            {
                current = asset.Imports[outerIdx];
                pathParts.Insert(0, current.ObjectName?.Value?.Value ?? "");
            }
            else
            {
                break;
            }
        }

        // Join path parts - the first part (package) already starts with /
        string result = string.Join("/", pathParts);
        // Ensure single leading slash
        if (!result.StartsWith("/"))
            result = "/" + result;
        return result;
    }

    private static string GetImportPackagePath(UAsset asset, Import import)
    {
        var current = import;
        while (current.OuterIndex.Index < 0)
        {
            int outerIdx = -current.OuterIndex.Index - 1;
            if (outerIdx >= 0 && outerIdx < asset.Imports.Count)
            {
                current = asset.Imports[outerIdx];
            }
            else
            {
                break;
            }
        }

        if (current.OuterIndex.Index == 0)
        {
            return current.ObjectName?.Value?.Value ?? "";
        }

        return "";
    }

    private static string GetImportExportPath(UAsset asset, Import import)
    {
        var pathParts = new List<string>();
        pathParts.Add(import.ObjectName?.Value?.Value ?? "");

        var current = import;
        while (current.OuterIndex.Index < 0)
        {
            int outerIdx = -current.OuterIndex.Index - 1;
            if (outerIdx >= 0 && outerIdx < asset.Imports.Count)
            {
                var outer = asset.Imports[outerIdx];
                if (outer.OuterIndex.Index == 0)
                {
                    break;
                }
                current = outer;
                pathParts.Insert(0, current.ObjectName?.Value?.Value ?? "");
            }
            else
            {
                break;
            }
        }

        return string.Join("/", pathParts);
    }

    private static string BuildExportPath(UAsset asset, int exportIndex)
    {
        var export = asset.Exports[exportIndex];
        var pathParts = new List<string>();
        pathParts.Add(export.ObjectName?.Value?.Value ?? "");

        var currentIndex = export.OuterIndex;
        while (currentIndex.Index > 0)
        {
            int outerIdx = currentIndex.Index - 1;
            if (outerIdx >= 0 && outerIdx < asset.Exports.Count)
            {
                var outer = asset.Exports[outerIdx];
                pathParts.Insert(0, outer.ObjectName?.Value?.Value ?? "");
                currentIndex = outer.OuterIndex;
            }
            else
            {
                break;
            }
        }

        return string.Join("/", pathParts);
    }

    /// <summary>
    /// Remap UAssetAPI's FPackageIndex to FPackageObjectIndex using raw script import hashes
    /// This matches retoc's approach where script imports are embedded directly as hashes
    /// in the export map, not as package import indices
    /// </summary>
    private static FPackageObjectIndex RemapToScriptImportOrExport(UAsset asset, FZenPackage zenPackage, UAssetAPI.UnrealTypes.FPackageIndex index)
    {
        if (index.Index == 0)
        {
            return FPackageObjectIndex.CreateNull();
        }
        else if (index.Index > 0)
        {
            // Export reference
            return FPackageObjectIndex.CreateExport((uint)(index.Index - 1));
        }
        else
        {
            // Import reference - get the raw script import hash
            int importIdx = -index.Index - 1;
            if (importIdx >= 0 && importIdx < zenPackage.ImportMap.Count)
            {
                // Return the raw import value (which contains the script import hash)
                return zenPackage.ImportMap[importIdx];
            }
            return FPackageObjectIndex.CreateNull();
        }
    }

    /// <summary>
    /// Remap UAssetAPI's FPackageIndex to our FPackageObjectIndex
    /// </summary>
    private static FPackageObjectIndex RemapUAssetPackageIndex(UAsset asset, FZenPackage zenPackage, UAssetAPI.UnrealTypes.FPackageIndex index)
    {
        if (index.Index == 0)
        {
            return FPackageObjectIndex.CreateNull();
        }
        else if (index.Index > 0)
        {
            return FPackageObjectIndex.CreateExport((uint)(index.Index - 1));
        }
        else
        {
            int importIdx = -index.Index - 1;
            if (importIdx >= 0 && importIdx < zenPackage.ImportMap.Count)
            {
                return zenPackage.ImportMap[importIdx];
            }
            return FPackageObjectIndex.CreateNull();
        }
    }

    private static FPackageObjectIndex RemapPackageIndex(UAsset asset, FZenPackage zenPackage, FPackageIndex index)
    {
        if (index.Index == 0)
        {
            return FPackageObjectIndex.CreateNull();
        }
        else if (index.Index > 0)
        {
            return FPackageObjectIndex.CreateExport((uint)(index.Index - 1));
        }
        else
        {
            int importIdx = -index.Index - 1;
            if (importIdx >= 0 && importIdx < zenPackage.ImportMap.Count)
            {
                return zenPackage.ImportMap[importIdx];
            }
            return FPackageObjectIndex.CreateNull();
        }
    }

    private static ulong CalculatePublicExportHash(string exportPath)
    {
        string lowerPath = exportPath.ToLowerInvariant();
        byte[] bytes = Encoding.Unicode.GetBytes(lowerPath);
        return IoStore.CityHash.CityHash64(bytes, 0, bytes.Length);
    }

    private static UAsset LoadAsset(string filePath, string? usmapPath)
    {
        UAssetAPI.Unversioned.Usmap? mappings = null;

        if (!string.IsNullOrEmpty(usmapPath) && File.Exists(usmapPath))
        {
            lock (_usmapLock)
            {
                if (!_usmapCache.TryGetValue(usmapPath, out mappings))
                {
                    mappings = new UAssetAPI.Unversioned.Usmap(usmapPath);
                    _usmapCache[usmapPath] = mappings;
                }
            }
        }

        // Skip export parsing and schema pulling - ZenConverter only needs header data and raw bytes.
        // This avoids schema errors for Blueprint assets where parent BPs aren't on disk.
        var flags = CustomSerializationFlags.SkipParsingExports | CustomSerializationFlags.SkipPreloadDependencyLoading;
        var asset = new UAsset(filePath, EngineVersion.VER_UE5_3, mappings, flags);
        asset.UseSeparateBulkDataFiles = true;
        return asset;
    }

    private static bool TryLoadScriptObjectsDatabase(string? path = null)
    {
        string dbPath = path ?? @"E:\WindsurfCoding\Repak_Gui-Revamped\retoc-rivals\tests\UE5.3\ScriptObjects.bin";
        if (File.Exists(dbPath))
        {
            try
            {
                var db = ScriptObjectsDatabase.Load(dbPath);
                _scriptObjectsDb = db;
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AnimBlueprintZenConverter] Failed to load script objects database: {ex.Message}");
            }
        }
        return false;
    }

    public static void SetScriptObjectsDatabase(ScriptObjectsDatabase db)
    {
        lock (_scriptObjectsLock)
        {
            _scriptObjectsDb = db;
        }
    }
}
