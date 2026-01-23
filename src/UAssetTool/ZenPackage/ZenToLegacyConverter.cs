using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.ExportTypes;

namespace UAssetTool.ZenPackage;

/// <summary>
/// Converts Zen packages to legacy .uasset/.uexp format
/// Ported from retoc-rivals/src/asset_conversion.rs
/// </summary>
public class ZenToLegacyConverter
{
    // Constants matching Rust code
    private const string CORE_OBJECT_PACKAGE_NAME = "/Script/CoreUObject";
    private const string PACKAGE_CLASS_NAME = "Package";
    private const string CLASS_CLASS_NAME = "Class";
    private const string OBJECT_CLASS_NAME = "Object";
    private const string PRESTREAM_PACKAGE_CLASS_NAME = "PrestreamPackage";
    
    private readonly FZenPackageHeader _zenPackage;
    private readonly byte[] _rawPackageData;
    private readonly FZenPackageContext? _context;
    private readonly ScriptObjectsDatabase? _scriptObjects;
    private readonly ulong _packageId;
    
    // Legacy package being built
    private readonly LegacyPackageBuilder _builder;
    
    // Lookup tables
    private readonly Dictionary<ResolvedZenImport, int> _resolvedImportLookup = new();
    private readonly Dictionary<ulong, int> _zenImportLookup = new(); // FPackageObjectIndex.Value -> import index
    private readonly Dictionary<int, int> _originalImportOrder = new(); // zen import index -> legacy import index
    
    private bool _hasFailedImportMapEntries;
    private bool _needsToRebuildExportsData;
    private bool _debugMode = false; // Enable debug output for dependency tracing
    
    public void SetDebugMode(bool enabled) => _debugMode = enabled;

    /// <summary>
    /// Create converter with full context for proper import resolution
    /// </summary>
    public ZenToLegacyConverter(FZenPackageContext context, ulong packageId)
    {
        _context = context;
        _packageId = packageId;
        _scriptObjects = context.ScriptObjects;
        
        var cached = context.GetCachedPackage(packageId);
        if (cached == null)
            throw new ArgumentException($"Package {packageId:X16} not found in context");
        
        _zenPackage = cached.Header;
        _rawPackageData = cached.RawData;
        _builder = new LegacyPackageBuilder();
    }

    /// <summary>
    /// Create converter without context (limited import resolution)
    /// </summary>
    public ZenToLegacyConverter(FZenPackageHeader zenPackage, byte[] rawPackageData, ScriptObjectsDatabase? scriptObjects = null)
    {
        _zenPackage = zenPackage;
        _rawPackageData = rawPackageData;
        _scriptObjects = scriptObjects;
        _context = null;
        _packageId = 0;
        _builder = new LegacyPackageBuilder();
    }

    /// <summary>
    /// Convert Zen package to legacy format
    /// </summary>
    public LegacyAssetBundle Convert()
    {
        BeginBuildSummary();
        CopyPackageSections();
        BuildImportMap();
        BuildExportMap();
        ResolveExportDependencies();
        FinalizeAsset();
        
        var bundle = SerializeAsset();
        
        // Extract bulk data from IoStore if available
        if (_context != null && _packageId != 0)
        {
            byte[]? bulkData = _context.ReadBulkData(_packageId);
            if (bulkData != null && bulkData.Length > 0)
            {
                bundle.BulkData = bulkData;
                Console.WriteLine($"[ZenToLegacy] Extracted {bulkData.Length} bytes of bulk data");
            }
        }
        
        return bundle;
    }

    /// <summary>
    /// Get the package IDs of all imported game packages (excludes /Script/ engine packages).
    /// Used for dependency extraction.
    /// </summary>
    public IEnumerable<ulong> GetImportedPackageIds()
    {
        for (int i = 0; i < _zenPackage.ImportedPackages.Count; i++)
        {
            if (i < _zenPackage.ImportedPackageNames.Count)
            {
                string packageName = _zenPackage.ImportedPackageNames[i];
                if (packageName.StartsWith("/Script/"))
                    continue;
            }
            yield return _zenPackage.ImportedPackages[i];
        }
    }

    /// <summary>
    /// Get imported package names with their IDs for debugging.
    /// </summary>
    public IEnumerable<(ulong Id, string Name)> GetImportedPackageInfo()
    {
        for (int i = 0; i < _zenPackage.ImportedPackages.Count; i++)
        {
            ulong pkgId = _zenPackage.ImportedPackages[i];
            string? name = null;
            
            if (_context != null)
                name = _context.GetPackagePath(pkgId);
            
            if (string.IsNullOrEmpty(name) && i < _zenPackage.ImportedPackageNames.Count)
                name = _zenPackage.ImportedPackageNames[i];
            
            if (string.IsNullOrEmpty(name))
                name = $"(unknown_{pkgId:X16})";
            
            yield return (pkgId, name);
        }
    }

    private void BeginBuildSummary()
    {
        // Use full path from context if available, otherwise fall back to zen package name
        string packageName = _zenPackage.PackageName();
        if (_context != null && _packageId != 0)
        {
            string? fullPath = _context.GetPackagePath(_packageId);
            if (!string.IsNullOrEmpty(fullPath))
                packageName = fullPath;
        }
        _builder.PackageName = packageName;
        _builder.PackageFlags = _zenPackage.Summary.PackageFlags;
        
        _builder.VersioningInfo = new LegacyVersioningInfo
        {
            FileVersionUE4 = _zenPackage.VersioningInfo.FileVersionUE4,
            FileVersionUE5 = _zenPackage.VersioningInfo.FileVersionUE5,
            LicenseeVersion = _zenPackage.VersioningInfo.LicenseeVersion,
            IsUnversioned = _zenPackage.IsUnversioned,
            CustomVersions = new List<FCustomVersion>(_zenPackage.VersioningInfo.CustomVersions)
        };
    }

    private void CopyPackageSections()
    {
        // Copy name map
        _builder.NameMap = new List<string>(_zenPackage.NameMap);
        _builder.NamesReferencedFromExportDataCount = _builder.NameMap.Count;
        
        // Copy bulk data resources from zen package
        foreach (var zenBulkData in _zenPackage.BulkData)
        {
            _builder.DataResources.Add(new LegacyDataResource
            {
                Flags = 0,
                SerialOffset = zenBulkData.SerialOffset,
                DuplicateSerialOffset = zenBulkData.DuplicateSerialOffset,
                SerialSize = zenBulkData.SerialSize,
                RawSize = zenBulkData.SerialSize,
                OuterIndex = FPackageIndex.CreateNull(),
                LegacyBulkDataFlags = zenBulkData.Flags
            });
        }
        
        // If no bulk data but we have exports, create empty data resources for each export
        // This matches the reference behavior where each export has a corresponding data resource
        if (_builder.DataResources.Count == 0 && _zenPackage.ExportMap.Count > 0)
        {
            for (int i = 0; i < _zenPackage.ExportMap.Count; i++)
            {
                _builder.DataResources.Add(new LegacyDataResource
                {
                    Flags = 0,
                    SerialOffset = 0,
                    DuplicateSerialOffset = 0,
                    SerialSize = 0,
                    RawSize = 0,
                    OuterIndex = FPackageIndex.CreateNull(),
                    LegacyBulkDataFlags = 0
                });
            }
        }
    }

    private void BuildImportMap()
    {
        if (_debugMode)
        {
            Console.WriteLine($"[DEBUG] BuildImportMap: {_zenPackage.ImportMap.Count} imports to resolve");
        }
        
        for (int importIndex = 0; importIndex < _zenPackage.ImportMap.Count; importIndex++)
        {
            var importObjectIndex = _zenPackage.ImportMap[importIndex];
            
            // Skip holes in the zen import map
            if (importObjectIndex.IsNull())
                continue;
            
            if (_debugMode)
            {
                string importType = importObjectIndex.IsScriptImport() ? "Script" : 
                                   importObjectIndex.IsPackageImport() ? "Package" : "Other";
                Console.WriteLine($"[DEBUG] Import[{importIndex}]: Type={importType}, Value={importObjectIndex.Value:X16}");
            }
            
            try
            {
                int resultPackageIndex = ResolveLocalPackageObject(importObjectIndex);
                
                // ResolveLocalPackageObject returns negative for imports, positive for exports
                // We need the actual import index (0-based positive) for _originalImportOrder
                if (resultPackageIndex >= 0) // Not an import (is export or null)
                {
                    throw new Exception($"Import map object did not resolve into an import");
                }
                
                // Convert from package index (-1, -2, ...) to import array index (0, 1, ...)
                int resultImportMapIndex = -(resultPackageIndex + 1);
                _originalImportOrder[importIndex] = resultImportMapIndex;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to resolve import {importIndex}: {ex.Message}");
                _hasFailedImportMapEntries = true;
                
                // Create placeholder import
                int nullPackageImport = CreateAndAddUnknownPackageImport();
                int importMapIndex = _builder.Imports.Count;
                var nullObjectImport = CreateUnknownObjectImportEntry(nullPackageImport);
                _builder.Imports.Add(nullObjectImport);
                _zenImportLookup[importObjectIndex.Value] = importMapIndex;
                _originalImportOrder[importIndex] = importMapIndex;
            }
        }
    }

    private void BuildExportMap()
    {
        // For now, don't rebuild exports - just strip header and use raw data
        // The complex rebuild logic has bugs that cause data duplication
        _needsToRebuildExportsData = false; // TODO: Fix RebuildExportData logic
        
        bool debugMode = Environment.GetEnvironmentVariable("DEBUG") == "1";
        
        for (int exportIndex = 0; exportIndex < _zenPackage.ExportMap.Count; exportIndex++)
        {
            var zenExport = _zenPackage.ExportMap[exportIndex];
            
            // Resolve indices with fallback
            FPackageIndex classIndex = ResolveWithFallbackAsPackageIndex(zenExport.ClassIndex, "class_index");
            FPackageIndex superIndex = ResolveWithFallbackAsPackageIndex(zenExport.SuperIndex, "super_index");
            FPackageIndex templateIndex = ResolveWithFallbackAsPackageIndex(zenExport.TemplateIndex, "template_index");
            FPackageIndex outerIndex = ResolveWithFallbackAsPackageIndex(zenExport.OuterIndex, "outer_index");
            
            string exportNameString = _zenPackage.GetName(zenExport.ObjectName);
            
            // Debug: Print export names to trace shifting
            if (debugMode && exportIndex < 20)
            {
                Console.WriteLine($"[DEBUG] Export[{exportIndex}]: ObjectName.Index={zenExport.ObjectName.Index}, Name=\"{exportNameString}\"");
            }
            var (objectName, objectNameNumber) = StoreOrFindNameWithNumber(exportNameString);
            uint objectFlags = zenExport.ObjectFlags;
            
            long serialSize = (long)zenExport.CookedSerialSize;
            long serialOffset = _needsToRebuildExportsData ? -1 : (long)zenExport.CookedSerialOffset;
            
            bool isNotForClient = zenExport.FilterFlags == EExportFilterFlags.NotForClient;
            bool isNotForServer = zenExport.FilterFlags == EExportFilterFlags.NotForServer;
            
            uint assetObjectFlags = (uint)(EObjectFlags.Public | EObjectFlags.Standalone | EObjectFlags.Transactional);
            bool isAsset = zenExport.OuterIndex.IsNull() && (objectFlags & assetObjectFlags) == assetObjectFlags;
            bool generatePublicHash = _zenPackage.ContainerHeaderVersion >= EIoContainerHeaderVersion.LocalizedPackages &&
                                     (objectFlags & (uint)EObjectFlags.Public) == 0 && zenExport.IsPublicExport();
            
            var newExport = new LegacyObjectExport
            {
                ClassIndex = classIndex,
                SuperIndex = superIndex,
                TemplateIndex = templateIndex,
                OuterIndex = outerIndex,
                ObjectName = objectName,
                ObjectNameNumber = objectNameNumber,
                ObjectFlags = objectFlags,
                SerialSize = serialSize,
                SerialOffset = serialOffset,
                IsNotForClient = isNotForClient,
                IsNotForServer = isNotForServer,
                IsAsset = isAsset,
                GeneratePublicHash = generatePublicHash,
                FirstExportDependencyIndex = -1,
                ScriptSerializationStartOffset = 0,
                ScriptSerializationEndOffset = serialSize
            };
            
            _builder.Exports.Add(newExport);
        }
        
        // Assign serial offsets if we need to rebuild
        if (_needsToRebuildExportsData)
        {
            long currentOffset = 0;
            for (int i = 0; i < _builder.Exports.Count; i++)
            {
                _builder.Exports[i].SerialOffset = currentOffset;
                currentOffset += _builder.Exports[i].SerialSize;
            }
        }
    }

    private void ResolveExportDependencies()
    {
        // Create standalone dependency objects first (matching Rust)
        var exportDependencies = new List<StandaloneExportDependencies>();
        for (int i = 0; i < _builder.Exports.Count; i++)
        {
            exportDependencies.Add(new StandaloneExportDependencies());
        }
        
        // If we have dependency bundle entries, this is new style (UE5.3+) dependencies
        if (_zenPackage.DependencyBundleEntries.Count > 0)
        {
            ResolveDependencyBundles(exportDependencies);
        }
        
        // Apply standalone dependencies to the package global list (matching Rust)
        ApplyDependenciesToPackage(exportDependencies);
    }

    private void ResolveDependencyBundles(List<StandaloneExportDependencies> exportDependencies)
    {
        for (int exportIndex = 0; exportIndex < _zenPackage.DependencyBundleHeaders.Count && exportIndex < exportDependencies.Count; exportIndex++)
        {
            var bundleHeader = _zenPackage.DependencyBundleHeaders[exportIndex];
            var dependencies = exportDependencies[exportIndex];
            
            int firstDepIndex = bundleHeader.FirstEntryIndex;
            
            // Create before create
            int lastCbC = firstDepIndex + (int)bundleHeader.CreateBeforeCreateDependencies;
            for (int i = firstDepIndex; i < lastCbC && i < _zenPackage.DependencyBundleEntries.Count; i++)
            {
                var entry = _zenPackage.DependencyBundleEntries[i];
                dependencies.CreateBeforeCreate.Add(RemapZenPackageIndex(entry.LocalImportOrExportIndex));
            }
            
            // Serialize before create
            int lastSbC = lastCbC + (int)bundleHeader.SerializeBeforeCreateDependencies;
            for (int i = lastCbC; i < lastSbC && i < _zenPackage.DependencyBundleEntries.Count; i++)
            {
                var entry = _zenPackage.DependencyBundleEntries[i];
                dependencies.SerializeBeforeCreate.Add(RemapZenPackageIndex(entry.LocalImportOrExportIndex));
            }
            
            // Create before serialize
            int lastCbS = lastSbC + (int)bundleHeader.CreateBeforeSerializeDependencies;
            for (int i = lastSbC; i < lastCbS && i < _zenPackage.DependencyBundleEntries.Count; i++)
            {
                var entry = _zenPackage.DependencyBundleEntries[i];
                dependencies.CreateBeforeSerialize.Add(RemapZenPackageIndex(entry.LocalImportOrExportIndex));
            }
            
            // Serialize before serialize
            int lastSbS = lastCbS + (int)bundleHeader.SerializeBeforeSerializeDependencies;
            for (int i = lastCbS; i < lastSbS && i < _zenPackage.DependencyBundleEntries.Count; i++)
            {
                var entry = _zenPackage.DependencyBundleEntries[i];
                dependencies.SerializeBeforeSerialize.Add(RemapZenPackageIndex(entry.LocalImportOrExportIndex));
            }
        }
    }

    private FPackageIndex RemapZenPackageIndex(FPackageIndex index)
    {
        if (index.IsImport())
        {
            int zenImportIndex = index.ToImportIndex();
            if (_originalImportOrder.TryGetValue(zenImportIndex, out int remapped))
            {
                return FPackageIndex.CreateImport(remapped);
            }
            // Zen import index not in our mapping - log for debugging
            if (_debugMode)
            {
                Console.WriteLine($"[DEBUG] Unmapped zen import index: {zenImportIndex}, ImportMap size: {_zenPackage.ImportMap.Count}, OriginalImportOrder keys: {string.Join(",", _originalImportOrder.Keys.Take(20))}");
            }
            return index;
        }
        return index;
    }

    private void ApplyDependenciesToPackage(List<StandaloneExportDependencies> exportDependencies)
    {
        // Matching Rust's apply_standalone_dependencies_to_package exactly
        for (int exportIndex = 0; exportIndex < _builder.Exports.Count; exportIndex++)
        {
            var export = _builder.Exports[exportIndex];
            var deps = exportDependencies[exportIndex];
            
            // Ensure that we have outer and super as create before create dependencies (matching Rust lines 899-905)
            if (!export.OuterIndex.IsNull() && !deps.CreateBeforeCreate.Contains(export.OuterIndex))
            {
                deps.CreateBeforeCreate.Add(export.OuterIndex);
            }
            if (!export.SuperIndex.IsNull() && !deps.CreateBeforeCreate.Contains(export.SuperIndex))
            {
                // Note: Rust checks create_before_create but pushes to serialize_before_serialize (line 903-904)
                deps.SerializeBeforeSerialize.Add(export.SuperIndex);
            }
            
            // Ensure that we have class and archetype as serialize before create dependencies (matching Rust lines 906-912)
            if (!export.ClassIndex.IsNull() && !deps.SerializeBeforeCreate.Contains(export.ClassIndex))
            {
                deps.SerializeBeforeCreate.Add(export.ClassIndex);
            }
            if (!export.TemplateIndex.IsNull() && !deps.SerializeBeforeCreate.Contains(export.TemplateIndex))
            {
                deps.SerializeBeforeCreate.Add(export.TemplateIndex);
            }
            
            // If we have no dependencies altogether, do not write first dependency import on the export (matching Rust line 914-916)
            if (deps.CreateBeforeCreate.Count == 0 && deps.SerializeBeforeCreate.Count == 0 &&
                deps.CreateBeforeSerialize.Count == 0 && deps.SerializeBeforeSerialize.Count == 0)
                continue;
            
            // Set the index of the preload dependencies start, and the numbers of each dependency (matching Rust lines 919-924)
            export.FirstExportDependencyIndex = _builder.PreloadDependencies.Count;
            export.SerializeBeforeSerializeDependencies = deps.SerializeBeforeSerialize.Count;
            export.CreateBeforeSerializeDependencies = deps.CreateBeforeSerialize.Count;
            export.SerializeBeforeCreateDependencies = deps.SerializeBeforeCreate.Count;
            export.CreateBeforeCreateDependencies = deps.CreateBeforeCreate.Count;
            
            // Append preload dependencies for this export now to the legacy package (matching Rust lines 926-930)
            _builder.PreloadDependencies.AddRange(deps.SerializeBeforeSerialize);
            _builder.PreloadDependencies.AddRange(deps.CreateBeforeSerialize);
            _builder.PreloadDependencies.AddRange(deps.SerializeBeforeCreate);
            _builder.PreloadDependencies.AddRange(deps.CreateBeforeCreate);
        }
    }

    private void FinalizeAsset()
    {
        // Remap import indices to final positions
        int importMapSize = Math.Max(_builder.Imports.Count, _zenPackage.ImportMap.Count);
        var importRemapMap = new Dictionary<int, int>();
        var newImportMap = new List<LegacyObjectImport>();
        
        var predefinedPositions = new HashSet<int>(_originalImportOrder.Values);
        int currentLegacyImportIndex = 0;
        
        for (int finalImportIndex = 0; finalImportIndex < importMapSize; finalImportIndex++)
        {
            if (_originalImportOrder.TryGetValue(finalImportIndex, out int existingPosition))
            {
                importRemapMap[existingPosition] = finalImportIndex;
                if (existingPosition < _builder.Imports.Count)
                    newImportMap.Add(_builder.Imports[existingPosition]);
                else
                    newImportMap.Add(CreateUnknownPackageImportEntry());
                continue;
            }
            
            while (predefinedPositions.Contains(currentLegacyImportIndex))
                currentLegacyImportIndex++;
            
            if (currentLegacyImportIndex >= _builder.Imports.Count)
            {
                newImportMap.Add(CreateUnknownPackageImportEntry());
                continue;
            }
            
            int existingPos = currentLegacyImportIndex++;
            importRemapMap[existingPos] = finalImportIndex;
            newImportMap.Add(_builder.Imports[existingPos]);
        }
        
        _builder.Imports = newImportMap;
        
        // Remap all references
        FPackageIndex Remap(FPackageIndex idx)
        {
            if (idx.IsImport() && importRemapMap.TryGetValue(idx.ToImportIndex(), out int newIdx))
                return FPackageIndex.CreateImport(newIdx);
            return idx;
        }
        
        foreach (var export in _builder.Exports)
        {
            export.ClassIndex = Remap(export.ClassIndex);
            export.SuperIndex = Remap(export.SuperIndex);
            export.TemplateIndex = Remap(export.TemplateIndex);
            export.OuterIndex = Remap(export.OuterIndex);
        }
        
        foreach (var import in _builder.Imports)
        {
            import.OuterIndex = Remap(import.OuterIndex);
        }
        
        foreach (var res in _builder.DataResources)
        {
            res.OuterIndex = Remap(res.OuterIndex);
        }
        
        for (int i = 0; i < _builder.PreloadDependencies.Count; i++)
        {
            _builder.PreloadDependencies[i] = Remap(_builder.PreloadDependencies[i]);
        }
    }

    private LegacyAssetBundle SerializeAsset()
    {
        // First pass: write header to calculate size
        byte[] assetData;
        using (var ms = new MemoryStream())
        using (var writer = new BinaryWriter(ms))
        {
            WriteLegacyPackageHeader(writer);
            assetData = ms.ToArray();
        }
        
        int headerSize = assetData.Length;
        
        // Fix SerialOffset in exports - should be header size + position in .uexp
        long currentOffset = 0;
        foreach (var export in _builder.Exports)
        {
            export.SerialOffset = headerSize + currentOffset;
            currentOffset += export.SerialSize;
        }
        
        // Second pass: write header again with correct serial offsets
        using (var ms = new MemoryStream())
        using (var writer = new BinaryWriter(ms))
        {
            WriteLegacyPackageHeader(writer);
            assetData = ms.ToArray();
        }
        
        // Build .uexp data
        byte[] exportsData = RebuildExportData();
        
        return new LegacyAssetBundle
        {
            AssetData = assetData,
            ExportsData = exportsData
        };
    }

    private void WriteLegacyPackageHeader(BinaryWriter writer)
    {
        const uint PACKAGE_FILE_TAG = 0x9E2A83C1;
        const int LEGACY_FILE_VERSION_UE5 = -8;
        
        // Write magic
        writer.Write(PACKAGE_FILE_TAG);
        
        // LegacyFileVersion: -8 for UE5 packages
        writer.Write(LEGACY_FILE_VERSION_UE5);
        
        // LegacyUE3Version - always 0
        writer.Write(0);
        
        // FileVersionUE4 - 0 for unversioned
        int fileVersionUE4 = _builder.VersioningInfo.IsUnversioned ? 0 : _builder.VersioningInfo.FileVersionUE4;
        writer.Write(fileVersionUE4);
        
        // FileVersionUE5 - 0 for unversioned (only for LEGACY_FILE_VERSION_UE5)
        int fileVersionUE5 = _builder.VersioningInfo.IsUnversioned ? 0 : _builder.VersioningInfo.FileVersionUE5;
        writer.Write(fileVersionUE5);
        
        // FileVersionLicenseeUE - 0 for unversioned
        int licenseeVersion = _builder.VersioningInfo.IsUnversioned ? 0 : _builder.VersioningInfo.LicenseeVersion;
        writer.Write(licenseeVersion);
        
        // Custom versions - empty array for unversioned
        if (_builder.VersioningInfo.IsUnversioned || _builder.VersioningInfo.CustomVersions.Count == 0)
        {
            writer.Write(0); // CustomVersionCount = 0
        }
        else
        {
            writer.Write(_builder.VersioningInfo.CustomVersions.Count);
            foreach (var cv in _builder.VersioningInfo.CustomVersions)
            {
                // FCustomVersion: Key (GUID), Version (int)
                writer.Write(cv.Key.ToByteArray());
                writer.Write(cv.Version);
            }
        }
        
        // TotalHeaderSize - placeholder
        long headerSizePos = writer.BaseStream.Position;
        writer.Write(0); // Placeholder
        
        // FolderName (package name)
        WriteFString(writer, _builder.PackageName);
        
        // Package flags
        writer.Write(_builder.PackageFlags);
        
        // NameCount and NameOffset
        writer.Write(_builder.NameMap.Count);
        long nameOffsetPos = writer.BaseStream.Position;
        writer.Write(0); // NameOffset placeholder
        
        // SoftObjectPathsCount/Offset - always written for UE5
        writer.Write(0); // SoftObjectPathsCount
        long softObjOffsetPos = writer.BaseStream.Position;
        writer.Write(0); // SoftObjectPathsOffset placeholder
        
        // GatherableTextDataCount/Offset - always written
        writer.Write(0);
        writer.Write(0);
        
        // ExportCount and ExportOffset
        writer.Write(_builder.Exports.Count);
        long exportOffsetPos = writer.BaseStream.Position;
        writer.Write(0); // ExportOffset placeholder
        
        // ImportCount and ImportOffset
        writer.Write(_builder.Imports.Count);
        long importOffsetPos = writer.BaseStream.Position;
        writer.Write(0); // ImportOffset placeholder
        
        // DependsOffset
        long dependsOffsetPos = writer.BaseStream.Position;
        writer.Write(0); // Placeholder
        
        // SoftPackageReferencesCount/Offset - count=0, offset=0 for cooked packages
        writer.Write(0);
        writer.Write(0);
        
        // SearchableNamesOffset
        writer.Write(0);
        
        // ThumbnailTableOffset
        writer.Write(0);
        
        // PackageGuid (16 bytes)
        writer.Write(Guid.NewGuid().ToByteArray());
        
        // Generations array
        writer.Write(1); // GenerationCount
        writer.Write(_builder.Exports.Count); // ExportCount for generation
        writer.Write(_builder.NameMap.Count); // NameCount for generation
        
        // EngineVersion (FEngineVersion) - all zeros for cooked packages
        writer.Write((ushort)0); // Major
        writer.Write((ushort)0); // Minor  
        writer.Write((ushort)0); // Patch
        writer.Write(0); // Changelist
        WriteFString(writer, ""); // Branch
        
        // CompatibleWithEngineVersion - all zeros for cooked packages
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(0);
        WriteFString(writer, "");
        
        // CompressionFlags
        writer.Write(0);
        
        // CompressedChunks (empty array)
        writer.Write(0);
        
        // PackageSource
        writer.Write(0);
        
        // AdditionalPackagesToCook (empty array)
        writer.Write(0);
        
        // AssetRegistryDataOffset
        writer.Write(0);
        
        // BulkDataStartOffset - should be the end of last export (header size + total export data size)
        // This is set later after we know the actual sizes
        long bulkDataStartOffsetPos = writer.BaseStream.Position;
        writer.Write((long)0); // Placeholder
        
        // WorldTileInfoDataOffset
        writer.Write(0);
        
        // ChunkIDs (empty array)
        writer.Write(0);
        
        // PreloadDependencyCount/Offset
        writer.Write(_builder.PreloadDependencies.Count);
        long preloadDepOffsetPos = writer.BaseStream.Position;
        writer.Write(0); // Placeholder
        
        // NamesReferencedFromExportDataCount
        writer.Write(_builder.NamesReferencedFromExportDataCount);
        
        // PayloadTocOffset
        writer.Write((long)-1);
        
        // DataResourceOffset only (Rust doesn't write count, just offset)
        long dataResourceOffsetPos = writer.BaseStream.Position;
        writer.Write(0); // Placeholder
        
        // ---- Write actual data sections ----
        
        // Write name map
        long nameOffset = writer.BaseStream.Position;
        foreach (var name in _builder.NameMap)
        {
            WriteFString(writer, name);
            // Name hash (not really used in modern UE)
            writer.Write(0);
        }
        
        // Write import map
        long importOffset = writer.BaseStream.Position;
        foreach (var import in _builder.Imports)
        {
            WriteImport(writer, import);
        }
        
        // Write export map
        long exportOffset = writer.BaseStream.Position;
        foreach (var export in _builder.Exports)
        {
            WriteExport(writer, export);
        }
        
        // Depends offset is right after exports
        // For cooked packages, write empty depends array (count=0) for each export
        long dependsOffset = writer.BaseStream.Position;
        for (int i = 0; i < _builder.Exports.Count; i++)
        {
            writer.Write(0); // Empty dependency count for this export
        }
        
        // Asset registry data - just write count=0 for cooked packages
        writer.Write(0); // asset_object_data_count
        
        // Write preload dependencies
        long preloadDepOffset = writer.BaseStream.Position;
        foreach (var dep in _builder.PreloadDependencies)
        {
            writer.Write(dep.Index);
        }
        
        // Write data resources (UE5.3+ format)
        // Format: Version (uint32) + Count (int32) + entries
        long dataResourceOffset = writer.BaseStream.Position;
        writer.Write((uint)0); // DataResourceVersion = 0 (Initial)
        writer.Write(_builder.DataResources.Count);
        foreach (var res in _builder.DataResources)
        {
            WriteDataResource(writer, res);
        }
        
        // Update header size
        long headerSize = writer.BaseStream.Position;
        
        // Go back and fill in offsets
        writer.BaseStream.Seek(headerSizePos, SeekOrigin.Begin);
        writer.Write((int)headerSize);
        
        writer.BaseStream.Seek(nameOffsetPos, SeekOrigin.Begin);
        writer.Write((int)nameOffset);
        
        // SoftObjectPathsOffset should point to ImportOffset when count is 0
        writer.BaseStream.Seek(softObjOffsetPos, SeekOrigin.Begin);
        writer.Write((int)importOffset);
        
        writer.BaseStream.Seek(importOffsetPos, SeekOrigin.Begin);
        writer.Write((int)importOffset);
        
        writer.BaseStream.Seek(exportOffsetPos, SeekOrigin.Begin);
        writer.Write((int)exportOffset);
        
        writer.BaseStream.Seek(dependsOffsetPos, SeekOrigin.Begin);
        writer.Write((int)dependsOffset);
        
        // SoftPackageReferencesOffset - leave as 0 for cooked packages
        // (don't need to fill in, already written as 0)
        
        writer.BaseStream.Seek(preloadDepOffsetPos, SeekOrigin.Begin);
        writer.Write((int)preloadDepOffset);
        
        writer.BaseStream.Seek(dataResourceOffsetPos, SeekOrigin.Begin);
        writer.Write((int)dataResourceOffset);
        
        // Fill in BulkDataStartOffset - it's the end of last export (header + export data)
        long totalExportSize = 0;
        foreach (var exp in _builder.Exports)
        {
            totalExportSize += exp.SerialSize;
        }
        long bulkDataStartOffset = headerSize + totalExportSize;
        writer.BaseStream.Seek(bulkDataStartOffsetPos, SeekOrigin.Begin);
        writer.Write(bulkDataStartOffset);
        
        // Seek to end
        writer.BaseStream.Seek(0, SeekOrigin.End);
    }

    private byte[] RebuildExportData()
    {
        if (!_needsToRebuildExportsData)
        {
            // Just strip the header - zen data already includes footer
            int headerSize = (int)_zenPackage.Summary.HeaderSize;
            byte[] result = new byte[_rawPackageData.Length - headerSize];
            Array.Copy(_rawPackageData, headerSize, result, 0, _rawPackageData.Length - headerSize);
            return result;
        }
        
        // Calculate total size
        long totalSize = 0;
        foreach (var export in _builder.Exports)
        {
            totalSize += export.SerialSize;
        }
        totalSize += 4; // Footer
        
        byte[] data = new byte[totalSize];
        int headerSize2 = (int)_zenPackage.Summary.HeaderSize;
        
        // Copy export data in order
        foreach (var bundleHeader in _zenPackage.ExportBundleHeaders)
        {
            long currentSerialOffset = bundleHeader.SerialOffset != ulong.MaxValue
                ? headerSize2 + (long)bundleHeader.SerialOffset
                : headerSize2;
            
            for (int i = 0; i < bundleHeader.EntryCount; i++)
            {
                int entryIndex = (int)(bundleHeader.FirstEntryIndex + i);
                if (entryIndex >= _zenPackage.ExportBundleEntries.Count) break;
                
                var entry = _zenPackage.ExportBundleEntries[entryIndex];
                if (entry.CommandType != EExportCommandType.Serialize) continue;
                
                int exportIndex = (int)entry.LocalExportIndex;
                if (exportIndex >= _builder.Exports.Count) continue;
                
                var export = _builder.Exports[exportIndex];
                int exportSerialSize = (int)export.SerialSize;
                int targetOffset = (int)export.SerialOffset;
                
                if (currentSerialOffset + exportSerialSize <= _rawPackageData.Length &&
                    targetOffset + exportSerialSize <= data.Length - 4)
                {
                    Array.Copy(_rawPackageData, (int)currentSerialOffset, data, targetOffset, exportSerialSize);
                }
                
                currentSerialOffset += exportSerialSize;
            }
        }
        
        // Write footer
        using (var ms = new MemoryStream(data))
        {
            ms.Seek(totalSize - 4, SeekOrigin.Begin);
            using var writer = new BinaryWriter(ms);
            writer.Write(0x9E2A83C1); // PACKAGE_FILE_TAG
        }
        
        return data;
    }

    private int ResolveLocalPackageObject(FPackageObjectIndex packageObject)
    {
        if (packageObject.IsNull())
            return 0; // Null package index
        
        if (packageObject.IsExport())
            return (int)packageObject.GetExportIndex() + 1; // Positive = export
        
        // Check cache
        if (_zenImportLookup.TryGetValue(packageObject.Value, out int cached))
            return -(cached + 1); // Negative = import
        
        // Resolve the import
        var resolved = ResolveGenericZenImport(packageObject);
        int importIndex = FindOrAddResolvedImport(resolved);
        _zenImportLookup[packageObject.Value] = importIndex;
        
        return -(importIndex + 1);
    }

    private int ResolveWithFallback(FPackageObjectIndex packageObject, string context)
    {
        try
        {
            return ResolveLocalPackageObject(packageObject);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to resolve {context}: {ex.Message}");
            _hasFailedImportMapEntries = true;
            
            int nullPackage = CreateAndAddUnknownPackageImport();
            var nullImport = CreateUnknownObjectImportEntry(nullPackage);
            int importIndex = _builder.Imports.Count;
            _builder.Imports.Add(nullImport);
            _zenImportLookup[packageObject.Value] = importIndex;
            
            return -(importIndex + 1);
        }
    }

    private FPackageIndex ResolveWithFallbackAsPackageIndex(FPackageObjectIndex packageObject, string context)
    {
        int resolved = ResolveWithFallback(packageObject, context);
        return new FPackageIndex(resolved);
    }

    private ResolvedZenImport ResolveGenericZenImport(FPackageObjectIndex import)
    {
        if (import.IsScriptImport())
        {
            return ResolveScriptImport(import);
        }
        else if (import.IsPackageImport())
        {
            return ResolvePackageImport(import);
        }
        else if (import.IsExport())
        {
            return ResolveExportAsImport(import);
        }
        
        throw new Exception($"Cannot resolve null package object index: {import}");
    }

    private ResolvedZenImport ResolveScriptImport(FPackageObjectIndex import)
    {
        if (_scriptObjects == null)
        {
            // Without script objects, create a generic import
            return new ResolvedZenImport
            {
                ClassPackage = CORE_OBJECT_PACKAGE_NAME,
                ClassName = OBJECT_CLASS_NAME,
                ObjectName = $"__ScriptImport_{import.Value:X16}__"
            };
        }
        
        var scriptObject = _scriptObjects.GetScriptObject(import);
        if (scriptObject == null)
        {
            return new ResolvedZenImport
            {
                ClassPackage = CORE_OBJECT_PACKAGE_NAME,
                ClassName = OBJECT_CLASS_NAME,
                ObjectName = $"__ScriptImport_{import.Value:X16}__"
            };
        }
        
        string objectName = _scriptObjects.GetName(scriptObject.ObjectName);
        
        if (_debugMode)
        {
            Console.WriteLine($"[DEBUG] ResolveScriptImport: Index={import.Value:X16}");
            Console.WriteLine($"  ObjectName.Index={scriptObject.ObjectName.Index}, Type={scriptObject.ObjectName.Type}");
            Console.WriteLine($"  Resolved name='{objectName}'");
        }
        
        if (scriptObject.OuterIndex.IsNull())
        {
            // This is a package
            return new ResolvedZenImport
            {
                ClassPackage = CORE_OBJECT_PACKAGE_NAME,
                ClassName = PACKAGE_CLASS_NAME,
                ObjectName = objectName
            };
        }
        
        // Resolve outer
        var resolvedOuter = ResolveScriptImport(scriptObject.OuterIndex);
        
        // Check if this is a class (pointed to by a CDO)
        if (_scriptObjects.IsClass(import))
        {
            return new ResolvedZenImport
            {
                ClassPackage = CORE_OBJECT_PACKAGE_NAME,
                ClassName = CLASS_CLASS_NAME,
                ObjectName = objectName,
                Outer = resolvedOuter
            };
        }
        
        // Check if this is a CDO (Default__ prefix, outer is package, has cdo_class_index)
        bool isCdoObject = resolvedOuter.Outer == null && objectName.StartsWith("Default__");
        if (isCdoObject && !scriptObject.CdoClassIndex.IsNull())
        {
            // Resolve the CDO's class
            var resolvedClass = ResolveScriptImport(scriptObject.CdoClassIndex);
            
            // Find the package by traversing up the outer chain
            string classPackage = GetPackageFromImport(resolvedClass);
            
            return new ResolvedZenImport
            {
                ClassPackage = classPackage,
                ClassName = resolvedClass.ObjectName,
                ObjectName = objectName,
                Outer = resolvedOuter
            };
        }
        
        // Default to Object
        return new ResolvedZenImport
        {
            ClassPackage = CORE_OBJECT_PACKAGE_NAME,
            ClassName = OBJECT_CLASS_NAME,
            ObjectName = objectName,
            Outer = resolvedOuter
        };
    }

    private ResolvedZenImport ResolvePackageImport(FPackageObjectIndex import)
    {
        var packageImport = import.GetPackageImport();
        
        if (packageImport.ImportedPackageIndex >= _zenPackage.ImportedPackages.Count)
        {
            throw new Exception($"Package index out of bounds: {packageImport.ImportedPackageIndex}");
        }
        
        ulong importedPackageId = _zenPackage.ImportedPackages[packageImport.ImportedPackageIndex];
        string packageName = packageImport.ImportedPackageIndex < _zenPackage.ImportedPackageNames.Count
            ? _zenPackage.ImportedPackageNames[packageImport.ImportedPackageIndex]
            : $"/Game/__Package_{importedPackageId:X16}__";
        
        // Create the package import (outer)
        var packageOuter = new ResolvedZenImport
        {
            ClassPackage = CORE_OBJECT_PACKAGE_NAME,
            ClassName = PACKAGE_CLASS_NAME,
            ObjectName = packageName
        };
        
        // If we have context, resolve the actual export from the imported package
        if (_context != null)
        {
            var targetPackage = _context.GetPackage(importedPackageId);
            if (targetPackage == null && _debugMode)
            {
                Console.WriteLine($"[DEBUG] Package not found: {importedPackageId:X16} ({packageName})");
            }
            if (targetPackage != null)
            {
                // Find the export by its public hash
                if (packageImport.ImportedPublicExportHashIndex < _zenPackage.ImportedPublicExportHashes.Count)
                {
                    ulong exportHash = _zenPackage.ImportedPublicExportHashes[packageImport.ImportedPublicExportHashIndex];
                    
                    // Search for export with matching hash in target package
                    bool foundExport = false;
                    foreach (var export in targetPackage.ExportMap)
                    {
                        if (export.PublicExportHash == exportHash)
                        {
                            foundExport = true;
                            // Debug: Check what name we're getting
                            if (_debugMode)
                            {
                                Console.WriteLine($"[DEBUG] Resolving export from {packageName}:");
                                Console.WriteLine($"  ObjectName.Index={export.ObjectName.Index}, Type={export.ObjectName.Type}");
                                Console.WriteLine($"  TargetPackage.NameMap.Count={targetPackage.NameMap.Count}");
                                if (export.ObjectName.Index < targetPackage.NameMap.Count)
                                    Console.WriteLine($"  Name at index={targetPackage.NameMap[(int)export.ObjectName.Index]}");
                            }
                            
                            // Use GetName with scriptObjects for Global name type support
                            string exportName = targetPackage.GetName(export.ObjectName, _scriptObjects);
                            
                            // Resolve the class of this export
                            string className = OBJECT_CLASS_NAME;
                            string classPackage = CORE_OBJECT_PACKAGE_NAME;
                            
                            if (!export.ClassIndex.IsNull() && export.ClassIndex.IsScriptImport() && _scriptObjects != null)
                            {
                                var classObj = _scriptObjects.GetScriptObject(export.ClassIndex);
                                if (classObj != null)
                                {
                                    className = _scriptObjects.GetName(classObj.ObjectName);
                                    // Get the class's outer (package)
                                    if (!classObj.OuterIndex.IsNull())
                                    {
                                        var outerObj = _scriptObjects.GetScriptObject(classObj.OuterIndex);
                                        if (outerObj != null)
                                        {
                                            classPackage = _scriptObjects.GetName(outerObj.ObjectName);
                                        }
                                    }
                                }
                            }
                            
                            // Build the resolved import with proper class info
                            return new ResolvedZenImport
                            {
                                ClassPackage = classPackage,
                                ClassName = className,
                                ObjectName = exportName,
                                Outer = packageOuter
                            };
                        }
                    }
                    
                    if (!foundExport)
                    {
                        if (_debugMode)
                        {
                            Console.WriteLine($"[DEBUG] Export hash not found: {exportHash:X16} in {packageName} (has {targetPackage.ExportMap.Count} exports)");
                            foreach (var exp in targetPackage.ExportMap)
                            {
                                Console.WriteLine($"  Export: hash={exp.PublicExportHash:X16}");
                            }
                        }
                        
                        // Fallback: if only one export, use it directly
                        if (targetPackage.ExportMap.Count == 1)
                        {
                            var export = targetPackage.ExportMap[0];
                            string exportName = targetPackage.GetName(export.ObjectName, _scriptObjects);
                            string className = OBJECT_CLASS_NAME;
                            string classPackage = CORE_OBJECT_PACKAGE_NAME;
                            
                            if (!export.ClassIndex.IsNull() && export.ClassIndex.IsScriptImport() && _scriptObjects != null)
                            {
                                var classObj = _scriptObjects.GetScriptObject(export.ClassIndex);
                                if (classObj != null)
                                {
                                    className = _scriptObjects.GetName(classObj.ObjectName);
                                    if (!classObj.OuterIndex.IsNull())
                                    {
                                        var outerObj = _scriptObjects.GetScriptObject(classObj.OuterIndex);
                                        if (outerObj != null)
                                            classPackage = _scriptObjects.GetName(outerObj.ObjectName);
                                    }
                                }
                            }
                            
                            return new ResolvedZenImport
                            {
                                ClassPackage = classPackage,
                                ClassName = className,
                                ObjectName = exportName,
                                Outer = packageOuter
                            };
                        }
                        
                        // Fallback: use export hash index to pick an export if within range
                        int exportIdx = (int)packageImport.ImportedPublicExportHashIndex;
                        if (exportIdx < targetPackage.ExportMap.Count)
                        {
                            var export = targetPackage.ExportMap[exportIdx];
                            string exportName = targetPackage.GetName(export.ObjectName, _scriptObjects);
                            string className = OBJECT_CLASS_NAME;
                            string classPackage = CORE_OBJECT_PACKAGE_NAME;
                            
                            if (!export.ClassIndex.IsNull() && export.ClassIndex.IsScriptImport() && _scriptObjects != null)
                            {
                                var classObj = _scriptObjects.GetScriptObject(export.ClassIndex);
                                if (classObj != null)
                                {
                                    className = _scriptObjects.GetName(classObj.ObjectName);
                                    if (!classObj.OuterIndex.IsNull())
                                    {
                                        var outerObj = _scriptObjects.GetScriptObject(classObj.OuterIndex);
                                        if (outerObj != null)
                                            classPackage = _scriptObjects.GetName(outerObj.ObjectName);
                                    }
                                }
                            }
                            
                            return new ResolvedZenImport
                            {
                                ClassPackage = classPackage,
                                ClassName = className,
                                ObjectName = exportName,
                                Outer = packageOuter
                            };
                        }
                    }
                }
            }
        }
        
        // Fallback: create a generic object import with export hash index as name suffix
        // Try to get a meaningful name from the export hash index
        string fallbackName = $"Export_{packageImport.ImportedPublicExportHashIndex}";
        if (_debugMode)
        {
            Console.WriteLine($"[DEBUG] Fallback import: pkg={packageName}, hashIdx={packageImport.ImportedPublicExportHashIndex}");
        }
        return new ResolvedZenImport
        {
            ClassPackage = CORE_OBJECT_PACKAGE_NAME,
            ClassName = OBJECT_CLASS_NAME,
            ObjectName = fallbackName,
            Outer = packageOuter
        };
    }

    private ResolvedZenImport ResolveExportAsImport(FPackageObjectIndex import)
    {
        int exportIndex = (int)import.GetExportIndex();
        if (exportIndex >= _zenPackage.ExportMap.Count)
        {
            throw new Exception($"Export index out of bounds: {exportIndex}");
        }
        
        var export = _zenPackage.ExportMap[exportIndex];
        string exportName = _zenPackage.GetName(export.ObjectName);
        
        // Resolve the class of this export
        string className = OBJECT_CLASS_NAME;
        string classPackage = CORE_OBJECT_PACKAGE_NAME;
        
        if (!export.ClassIndex.IsNull())
        {
            // Resolve the class import to get class name and package
            var resolvedClass = ResolveGenericZenImport(export.ClassIndex);
            className = resolvedClass.ObjectName;
            // Get the package by traversing up the outer chain
            classPackage = GetPackageFromImport(resolvedClass);
        }
        
        ResolvedZenImport? outer = null;
        if (!export.OuterIndex.IsNull())
        {
            outer = ResolveGenericZenImport(export.OuterIndex);
        }
        else
        {
            // Top-level export, outer is the package
            outer = new ResolvedZenImport
            {
                ClassPackage = CORE_OBJECT_PACKAGE_NAME,
                ClassName = PACKAGE_CLASS_NAME,
                ObjectName = _zenPackage.SourcePackageName()
            };
        }
        
        return new ResolvedZenImport
        {
            ClassPackage = classPackage,
            ClassName = className,
            ObjectName = exportName,
            Outer = outer
        };
    }

    private int FindOrAddResolvedImport(ResolvedZenImport import)
    {
        // Check if we already have this import
        if (_resolvedImportLookup.TryGetValue(import, out int existing))
            return existing;
        
        // Resolve outer first
        int outerIndex = 0;
        if (import.Outer != null)
        {
            outerIndex = -(FindOrAddResolvedImport(import.Outer) + 1);
        }
        
        // Store names with number suffix handling
        var (classPackageIdx, classPackageNum) = StoreOrFindNameWithNumber(import.ClassPackage);
        var (classNameIdx, classNameNum) = StoreOrFindNameWithNumber(import.ClassName);
        var (objectNameIdx, objectNameNum) = StoreOrFindNameWithNumber(import.ObjectName);
        
        // Create import entry
        int newIndex = _builder.Imports.Count;
        _builder.Imports.Add(new LegacyObjectImport
        {
            ClassPackage = classPackageIdx,
            ClassPackageNumber = classPackageNum,
            ClassName = classNameIdx,
            ClassNameNumber = classNameNum,
            OuterIndex = new FPackageIndex(outerIndex),
            ObjectName = objectNameIdx,
            ObjectNameNumber = objectNameNum,
            IsOptional = false
        });
        
        _resolvedImportLookup[import] = newIndex;
        return newIndex;
    }

    private int StoreOrFindName(string name)
    {
        int idx = _builder.NameMap.IndexOf(name);
        if (idx >= 0) return idx;
        
        idx = _builder.NameMap.Count;
        _builder.NameMap.Add(name);
        return idx;
    }
    
    /// <summary>
    /// Parse a name with potential numeric suffix (e.g., "PyAbility_105701")
    /// Returns the base name and the number (stored as number+1 in FName format)
    /// </summary>
    private (string baseName, int number) ParseNameWithNumber(string fullName)
    {
        // Check if the name ends with _N where N is a number
        int lastUnderscore = fullName.LastIndexOf('_');
        if (lastUnderscore > 0 && lastUnderscore < fullName.Length - 1)
        {
            string suffix = fullName.Substring(lastUnderscore + 1);
            if (int.TryParse(suffix, out int num))
            {
                // Found numeric suffix - return base name and number+1 (FName format)
                return (fullName.Substring(0, lastUnderscore), num + 1);
            }
        }
        // No numeric suffix
        return (fullName, 0);
    }
    
    /// <summary>
    /// Store name with numeric suffix handling - stores base name and returns (index, number)
    /// </summary>
    private (int index, int number) StoreOrFindNameWithNumber(string fullName)
    {
        var (baseName, number) = ParseNameWithNumber(fullName);
        int index = StoreOrFindName(baseName);
        return (index, number);
    }

    /// <summary>
    /// Traverse the outer chain to find the package (the top-level object with no outer)
    /// </summary>
    private string GetPackageFromImport(ResolvedZenImport import)
    {
        // If no outer, this IS the package
        if (import.Outer == null)
            return import.ObjectName;
        
        // Traverse up the chain
        var current = import.Outer;
        while (current.Outer != null)
        {
            current = current.Outer;
        }
        return current.ObjectName;
    }

    private int CreateAndAddUnknownPackageImport()
    {
        int newIndex = _builder.Imports.Count;
        _builder.Imports.Add(CreateUnknownPackageImportEntry());
        return newIndex;
    }

    private LegacyObjectImport CreateUnknownPackageImportEntry()
    {
        return new LegacyObjectImport
        {
            ClassPackage = StoreOrFindName(CORE_OBJECT_PACKAGE_NAME),
            ClassName = StoreOrFindName(PACKAGE_CLASS_NAME),
            OuterIndex = FPackageIndex.CreateNull(),
            ObjectName = StoreOrFindName("/Engine/UnknownPackage"),
            IsOptional = false
        };
    }

    private LegacyObjectImport CreateUnknownObjectImportEntry(int outerIndex)
    {
        return new LegacyObjectImport
        {
            ClassPackage = StoreOrFindName(CORE_OBJECT_PACKAGE_NAME),
            ClassName = StoreOrFindName(OBJECT_CLASS_NAME),
            OuterIndex = FPackageIndex.CreateImport(outerIndex),
            ObjectName = StoreOrFindName("UnknownExport"),
            IsOptional = false
        };
    }

    private void WriteString(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length + 1);
        writer.Write(bytes);
        writer.Write((byte)0);
    }

    private void WriteFString(BinaryWriter writer, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            writer.Write(0); // Length 0
            return;
        }
        
        // FString format: int32 length (including null terminator), then UTF-8 bytes, then null
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length + 1); // +1 for null terminator
        writer.Write(bytes);
        writer.Write((byte)0); // null terminator
    }

    private void WriteImport(BinaryWriter writer, LegacyObjectImport import)
    {
        // FMinimalName: index (i32) + number (i32)
        writer.Write(import.ClassPackage);
        writer.Write(import.ClassPackageNumber);
        writer.Write(import.ClassName);
        writer.Write(import.ClassNameNumber);
        writer.Write(import.OuterIndex.Index);
        writer.Write(import.ObjectName);
        writer.Write(import.ObjectNameNumber);
        
        // PackageName - written when FilterEditorOnly is false (for One File Per Actor support)
        // For cooked packages this is always an empty FMinimalName
        bool isFilterEditorOnly = (_builder.PackageFlags & 0x80000000) != 0;
        if (!isFilterEditorOnly)
        {
            writer.Write(0); // PackageName index (0 = None)
            writer.Write(0); // PackageName number
        }
        
        // bOptional - written as int32 for UE5.3+ (OptionalResources version)
        writer.Write(import.IsOptional ? 1 : 0);
    }

    private void WriteExport(BinaryWriter writer, LegacyObjectExport export)
    {
        // FPackageIndex (4 bytes each)
        writer.Write(export.ClassIndex.Index);
        writer.Write(export.SuperIndex.Index);
        writer.Write(export.TemplateIndex.Index);
        writer.Write(export.OuterIndex.Index);
        
        // FMinimalName: index (i32) + number (i32)
        writer.Write(export.ObjectName);
        writer.Write(export.ObjectNameNumber);
        
        writer.Write(export.ObjectFlags);
        writer.Write(export.SerialSize);   // i64
        writer.Write(export.SerialOffset); // i64
        
        // is_forced_export: i32 (always 0 for modern packages)
        writer.Write(0);
        
        // is_not_for_client, is_not_for_server: i32
        writer.Write(export.IsNotForClient ? 1 : 0);
        writer.Write(export.IsNotForServer ? 1 : 0);
        
        // For UE5.3 (unversioned), is_inherited_instance is serialized as i32
        writer.Write(0); // is_inherited_instance
        
        // package_flags: u32 (always 0 for modern packages)
        writer.Write((uint)0);
        
        // is_not_always_loaded_for_editor_game, is_asset: i32
        writer.Write(0); // is_not_always_loaded_for_editor_game
        writer.Write(export.IsAsset ? 1 : 0);
        
        // generate_public_hash: i32 (for UE5.3+)
        writer.Write(export.GeneratePublicHash ? 1 : 0);
        
        // Dependency indices (i32 each)
        writer.Write(export.FirstExportDependencyIndex);
        writer.Write(export.SerializeBeforeSerializeDependencies);
        writer.Write(export.CreateBeforeSerializeDependencies);
        writer.Write(export.SerializeBeforeCreateDependencies);
        writer.Write(export.CreateBeforeCreateDependencies);
        
        // Script serialization offsets - only for versioned packages
        // For unversioned (IsUnversioned=true), these are NOT written
    }

    private void WriteDataResource(BinaryWriter writer, LegacyDataResource res)
    {
        writer.Write(res.Flags);
        writer.Write(res.SerialOffset);
        writer.Write(res.DuplicateSerialOffset);
        writer.Write(res.SerialSize);
        writer.Write(res.RawSize);
        writer.Write(res.OuterIndex.Index);
        writer.Write(res.LegacyBulkDataFlags);
    }
}

/// <summary>
/// Resolved import info
/// </summary>
public class ResolvedZenImport : IEquatable<ResolvedZenImport>
{
    public string ClassPackage { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string ObjectName { get; set; } = "";
    public ResolvedZenImport? Outer { get; set; }

    public bool Equals(ResolvedZenImport? other)
    {
        if (other == null) return false;
        return ClassPackage == other.ClassPackage &&
               ClassName == other.ClassName &&
               ObjectName == other.ObjectName &&
               Equals(Outer, other.Outer);
    }

    public override bool Equals(object? obj) => Equals(obj as ResolvedZenImport);
    
    public override int GetHashCode()
    {
        return HashCode.Combine(ClassPackage, ClassName, ObjectName, Outer?.GetHashCode() ?? 0);
    }
}

/// <summary>
/// Standalone export dependencies
/// </summary>
public class StandaloneExportDependencies
{
    public List<FPackageIndex> SerializeBeforeSerialize { get; } = new();
    public List<FPackageIndex> CreateBeforeSerialize { get; } = new();
    public List<FPackageIndex> SerializeBeforeCreate { get; } = new();
    public List<FPackageIndex> CreateBeforeCreate { get; } = new();
}

/// <summary>
/// Legacy package builder
/// </summary>
public class LegacyPackageBuilder
{
    public string PackageName { get; set; } = "";
    public uint PackageFlags { get; set; }
    public LegacyVersioningInfo VersioningInfo { get; set; } = new();
    public List<string> NameMap { get; set; } = new();
    public int NamesReferencedFromExportDataCount { get; set; }
    public List<LegacyObjectImport> Imports { get; set; } = new();
    public List<LegacyObjectExport> Exports { get; set; } = new();
    public List<FPackageIndex> PreloadDependencies { get; set; } = new();
    public List<LegacyDataResource> DataResources { get; set; } = new();
}

public class LegacyVersioningInfo
{
    public int FileVersionUE4 { get; set; }
    public int FileVersionUE5 { get; set; }
    public int LicenseeVersion { get; set; }
    public bool IsUnversioned { get; set; }
    public List<FCustomVersion> CustomVersions { get; set; } = new();
}

public class LegacyObjectImport
{
    public int ClassPackage { get; set; }
    public int ClassPackageNumber { get; set; }
    public int ClassName { get; set; }
    public int ClassNameNumber { get; set; }
    public FPackageIndex OuterIndex { get; set; } = FPackageIndex.CreateNull();
    public int ObjectName { get; set; }
    public int ObjectNameNumber { get; set; }
    public bool IsOptional { get; set; }
}

public class LegacyObjectExport
{
    public FPackageIndex ClassIndex { get; set; } = FPackageIndex.CreateNull();
    public FPackageIndex SuperIndex { get; set; } = FPackageIndex.CreateNull();
    public FPackageIndex TemplateIndex { get; set; } = FPackageIndex.CreateNull();
    public FPackageIndex OuterIndex { get; set; } = FPackageIndex.CreateNull();
    public int ObjectName { get; set; }
    public int ObjectNameNumber { get; set; }
    public uint ObjectFlags { get; set; }
    public long SerialSize { get; set; }
    public long SerialOffset { get; set; }
    public bool IsNotForClient { get; set; }
    public bool IsNotForServer { get; set; }
    public bool IsAsset { get; set; }
    public bool GeneratePublicHash { get; set; }
    public int FirstExportDependencyIndex { get; set; } = -1;
    public int SerializeBeforeSerializeDependencies { get; set; }
    public int CreateBeforeSerializeDependencies { get; set; }
    public int SerializeBeforeCreateDependencies { get; set; }
    public int CreateBeforeCreateDependencies { get; set; }
    public long ScriptSerializationStartOffset { get; set; }
    public long ScriptSerializationEndOffset { get; set; }
}

public class LegacyDataResource
{
    public uint Flags { get; set; }
    public long SerialOffset { get; set; }
    public long DuplicateSerialOffset { get; set; }
    public long SerialSize { get; set; }
    public long RawSize { get; set; }
    public FPackageIndex OuterIndex { get; set; } = FPackageIndex.CreateNull();
    public uint LegacyBulkDataFlags { get; set; }
}

/// <summary>
/// Output from conversion
/// </summary>
public class LegacyAssetBundle
{
    public byte[] AssetData { get; set; } = Array.Empty<byte>();
    public byte[] ExportsData { get; set; } = Array.Empty<byte>();
    public byte[]? BulkData { get; set; }
    public byte[]? OptionalBulkData { get; set; }
    public byte[]? MemoryMappedBulkData { get; set; }
}

