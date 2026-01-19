using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.ExportTypes;

// Aliases to avoid conflicts with local types
using UAssetFPackageIndex = UAssetAPI.UnrealTypes.FPackageIndex;
using UAssetEObjectFlags = UAssetAPI.UnrealTypes.EObjectFlags;

namespace UAssetTool.ZenPackage;

/// <summary>
/// Converts Zen packages to legacy .uasset/.uexp format using UAssetAPI for proper serialization.
/// This ensures the output files are valid and readable by UAssetAPI and other tools.
/// </summary>
public class ZenToUAssetConverter
{
    private const string CORE_UOBJECT_PACKAGE = "/Script/CoreUObject";
    private const string CLASS_NAME = "Class";
    private const string PACKAGE_NAME = "Package";
    private const string OBJECT_NAME = "Object";
    
    private readonly FZenPackageHeader _zenPackage;
    private readonly byte[] _rawPackageData;
    private readonly FZenPackageContext? _context;
    private readonly ScriptObjectsDatabase? _scriptObjects;
    private readonly ulong _packageId;
    
    // Lookup tables for import resolution
    private readonly Dictionary<ulong, int> _zenImportToLegacyIndex = new();
    private readonly Dictionary<string, int> _packageImportCache = new();
    
    private UAsset? _legacyAsset;

    public ZenToUAssetConverter(FZenPackageContext context, ulong packageId)
    {
        _context = context;
        _packageId = packageId;
        _scriptObjects = context.ScriptObjects;
        
        var cached = context.GetCachedPackage(packageId);
        if (cached == null)
            throw new ArgumentException($"Package {packageId:X16} not found in context");
        
        _zenPackage = cached.Header;
        _rawPackageData = cached.RawData;
    }

    public ZenToUAssetConverter(FZenPackageHeader zenPackage, byte[] rawPackageData, ScriptObjectsDatabase? scriptObjects = null)
    {
        _zenPackage = zenPackage;
        _rawPackageData = rawPackageData;
        _scriptObjects = scriptObjects;
        _context = null;
        _packageId = 0;
    }

    /// <summary>
    /// Get the full package path, preferring context path over truncated name map path
    /// </summary>
    private string GetFullPackagePath()
    {
        // Try to get full path from context first
        if (_context != null && _packageId != 0)
        {
            string? contextPath = _context.GetPackagePath(_packageId);
            if (!string.IsNullOrEmpty(contextPath))
                return contextPath;
        }
        
        // Try to find full path in name map - look for paths starting with /Game/
        foreach (var name in _zenPackage.NameMap)
        {
            if (name.StartsWith("/Game/") || name.StartsWith("/Script/") || name.StartsWith("/Engine/"))
            {
                // Check if this looks like a package path (contains the package name)
                string packageName = _zenPackage.PackageName();
                if (name.EndsWith("/" + packageName) || name.EndsWith(packageName))
                    return name;
            }
        }
        
        // Try imported package names as they often contain related package paths
        foreach (var importedPath in _zenPackage.ImportedPackageNames)
        {
            if (importedPath.StartsWith("/Game/"))
            {
                // Use the directory of an imported package as a hint
                string dir = importedPath.Contains('/') ? 
                    importedPath.Substring(0, importedPath.LastIndexOf('/')) : importedPath;
                string packageName = _zenPackage.PackageName();
                return dir + "/" + packageName;
            }
        }
        
        // Fall back to name from Zen package (may be truncated)
        string fallbackPath = _zenPackage.PackageName();
        if (!fallbackPath.StartsWith("/"))
            fallbackPath = "/" + fallbackPath;
        return fallbackPath;
    }

    /// <summary>
    /// Get the package IDs of all imported game packages (excludes /Script/ engine packages).
    /// Used for dependency extraction.
    /// </summary>
    public IEnumerable<ulong> GetImportedPackageIds()
    {
        // Filter out script packages (engine classes) - only return actual game packages
        for (int i = 0; i < _zenPackage.ImportedPackages.Count; i++)
        {
            // Check if we have the package name and it's not a script package
            if (i < _zenPackage.ImportedPackageNames.Count)
            {
                string packageName = _zenPackage.ImportedPackageNames[i];
                // Skip /Script/ packages (engine classes like /Script/Engine, /Script/CoreUObject)
                if (packageName.StartsWith("/Script/"))
                    continue;
            }
            yield return _zenPackage.ImportedPackages[i];
        }
    }

    /// <summary>
    /// Get imported package names with their IDs for debugging.
    /// Uses context to look up package paths when available.
    /// </summary>
    public IEnumerable<(ulong Id, string Name)> GetImportedPackageInfo()
    {
        for (int i = 0; i < _zenPackage.ImportedPackages.Count; i++)
        {
            ulong pkgId = _zenPackage.ImportedPackages[i];
            string? name = null;
            
            // Try to get name from context first (most reliable)
            if (_context != null)
            {
                name = _context.GetPackagePath(pkgId);
            }
            
            // Fall back to ImportedPackageNames if available
            if (string.IsNullOrEmpty(name) && i < _zenPackage.ImportedPackageNames.Count)
            {
                name = _zenPackage.ImportedPackageNames[i];
            }
            
            if (string.IsNullOrEmpty(name))
            {
                name = $"(unknown_{pkgId:X16})";
            }
            
            yield return (pkgId, name);
        }
    }

    /// <summary>
    /// Convert Zen package to legacy UAsset format using UAssetAPI serialization
    /// </summary>
    public LegacyAssetBundle Convert()
    {
        // Initialize UAsset with proper settings for UE5.3 unversioned
        _legacyAsset = new UAsset(EngineVersion.VER_UE5_3);
        _legacyAsset.IsUnversioned = true;
        _legacyAsset.UseSeparateBulkDataFiles = true;
        _legacyAsset.WillSerializeNameHashes = true;
        
        // Set package path
        string packagePath = GetFullPackagePath();
        _legacyAsset.FolderName = new FString(packagePath);
        
        // Set package flags (FilterEditorOnly + Cooked)
        _legacyAsset.PackageFlags = EPackageFlags.PKG_FilterEditorOnly | EPackageFlags.PKG_Cooked;
        
        // Initialize collections
        _legacyAsset.ClearNameIndexList();
        _legacyAsset.Imports = new List<Import>();
        _legacyAsset.Exports = new List<Export>();
        
        // Populate name map from Zen package
        foreach (var name in _zenPackage.NameMap)
        {
            _legacyAsset.AddNameReference(new FString(name));
        }
        
        // Build imports
        BuildImportsInternal();
        
        // Build exports with raw data
        BuildExportsInternal();
        
        // Use UAssetAPI's Write method for proper serialization
        return WriteUsingUAssetAPI();
    }
    
    private LegacyAssetBundle WriteUsingUAssetAPI()
    {
        // Initialize all internal fields required for Write() 
        _legacyAsset!.InitializeForWriting();
        
        _legacyAsset!.Write(out MemoryStream assetStream, out MemoryStream expStream);
        
        return new LegacyAssetBundle
        {
            AssetData = assetStream?.ToArray() ?? Array.Empty<byte>(),
            ExportsData = expStream?.ToArray() ?? Array.Empty<byte>(),
            BulkData = ExtractBulkData()
        };
    }
    
    private void BuildExportsInternal()
    {
        for (int i = 0; i < _zenPackage.ExportMap.Count; i++)
        {
            var zenExport = _zenPackage.ExportMap[i];
            string exportName = _zenPackage.GetName(zenExport.ObjectName);
            byte[] exportData = ExtractExportData(zenExport, i);
            
            // Resolve class index
            var classIndex = UAssetFPackageIndex.FromRawIndex(0);
            if (!zenExport.ClassIndex.IsNull() && _zenImportToLegacyIndex.TryGetValue(zenExport.ClassIndex.Value, out int classIdx))
            {
                classIndex = UAssetFPackageIndex.FromRawIndex(-(classIdx + 1));
            }
            
            // Resolve outer index
            var outerIndex = UAssetFPackageIndex.FromRawIndex(0);
            if (!zenExport.OuterIndex.IsNull())
            {
                if (zenExport.OuterIndex.IsExport())
                {
                    outerIndex = UAssetFPackageIndex.FromRawIndex((int)zenExport.OuterIndex.GetExportIndex() + 1);
                }
                else if (_zenImportToLegacyIndex.TryGetValue(zenExport.OuterIndex.Value, out int outerIdx))
                {
                    outerIndex = UAssetFPackageIndex.FromRawIndex(-(outerIdx + 1));
                }
            }
            
            // Create RawExport with the export data
            var rawExport = new RawExport(exportData, _legacyAsset!, Array.Empty<byte>())
            {
                ClassIndex = classIndex,
                SuperIndex = UAssetFPackageIndex.FromRawIndex(0),
                TemplateIndex = UAssetFPackageIndex.FromRawIndex(0),
                OuterIndex = outerIndex,
                ObjectName = new FName(_legacyAsset, exportName),
                ObjectFlags = (UAssetEObjectFlags)zenExport.ObjectFlags,
                SerialSize = exportData.Length,
                bNotForClient = false,
                bNotForServer = false
            };
            
            _legacyAsset!.Exports.Add(rawExport);
        }
    }
    
    private LegacyAssetBundle WriteLegacyPackage()
    {
        using var headerStream = new MemoryStream();
        using var headerWriter = new BinaryWriter(headerStream);
        using var expStream = new MemoryStream();
        using var expWriter = new BinaryWriter(expStream);
        
        // Collect data first
        var nameMap = _zenPackage.NameMap;
        var imports = CollectImports();
        var exports = CollectExports(expWriter);
        
        // Calculate offsets
        int headerSize = CalculateHeaderSize(nameMap, imports, exports);
        
        // Write header
        WriteHeader(headerWriter, nameMap, imports, exports, headerSize, (int)expStream.Length);
        
        return new LegacyAssetBundle
        {
            AssetData = headerStream.ToArray(),
            ExportsData = expStream.ToArray(),
            BulkData = ExtractBulkData()
        };
    }
    
    private void BuildImportsInternal()
    {
        // Add CoreUObject package first
        GetOrCreatePackageImport(CORE_UOBJECT_PACKAGE);
        
        // Process Zen import map
        for (int i = 0; i < _zenPackage.ImportMap.Count; i++)
        {
            var zenImport = _zenPackage.ImportMap[i];
            if (zenImport.IsNull())
                continue;
            
            try
            {
                ResolveZenImport(zenImport);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: Failed to resolve import {i}: {ex.Message}");
                CreatePlaceholderImport($"__UnresolvedImport_{i}__");
            }
        }
    }
    
    private List<LegacyImportEntry> CollectImports()
    {
        var result = new List<LegacyImportEntry>();
        foreach (var import in _legacyAsset!.Imports)
        {
            result.Add(new LegacyImportEntry
            {
                ClassPackage = import.ClassPackage.ToString(),
                ClassName = import.ClassName.ToString(),
                OuterIndex = import.OuterIndex.Index,
                ObjectName = import.ObjectName.ToString()
            });
        }
        return result;
    }
    
    private List<LegacyExportEntry> CollectExports(BinaryWriter expWriter)
    {
        var result = new List<LegacyExportEntry>();
        
        for (int i = 0; i < _zenPackage.ExportMap.Count; i++)
        {
            var zenExport = _zenPackage.ExportMap[i];
            string exportName = _zenPackage.GetName(zenExport.ObjectName);
            byte[] exportData = ExtractExportData(zenExport, i);
            
            // Resolve indices
            int classIndex = 0;
            if (!zenExport.ClassIndex.IsNull() && _zenImportToLegacyIndex.TryGetValue(zenExport.ClassIndex.Value, out int classIdx))
            {
                classIndex = -(classIdx + 1);
            }
            
            int outerIndex = 0;
            if (!zenExport.OuterIndex.IsNull())
            {
                if (zenExport.OuterIndex.IsExport())
                {
                    outerIndex = (int)zenExport.OuterIndex.GetExportIndex() + 1;
                }
                else if (_zenImportToLegacyIndex.TryGetValue(zenExport.OuterIndex.Value, out int outerIdx))
                {
                    outerIndex = -(outerIdx + 1);
                }
            }
            
            long serialOffset = expWriter.BaseStream.Position;
            expWriter.Write(exportData);
            
            result.Add(new LegacyExportEntry
            {
                ClassIndex = classIndex,
                SuperIndex = 0,
                TemplateIndex = 0,
                OuterIndex = outerIndex,
                ObjectName = exportName,
                ObjectFlags = zenExport.ObjectFlags,
                SerialSize = exportData.Length,
                SerialOffset = serialOffset
            });
        }
        
        return result;
    }
    
    private int CalculateHeaderSize(List<string> nameMap, List<LegacyImportEntry> imports, List<LegacyExportEntry> exports)
    {
        // Rough estimate - will be refined
        int size = 0x100; // Base header
        size += nameMap.Sum(n => 4 + Encoding.UTF8.GetByteCount(n) + 1 + 4); // Name map with hashes
        size += imports.Count * 28; // Import entries
        size += exports.Count * 104; // Export entries (UE5)
        size = (size + 15) & ~15; // Align to 16 bytes
        return size;
    }
    
    private void WriteHeader(BinaryWriter writer, List<string> nameMap, List<LegacyImportEntry> imports, 
                            List<LegacyExportEntry> exports, int headerSize, int exportsDataSize)
    {
        // UE5.3 Unversioned format - matching Marvel Rivals assets
        const uint PACKAGE_FILE_TAG = 0x9E2A83C1;
        const int LEGACY_FILE_VERSION = -8; // UE5
        const uint PKG_FILTER_EDITOR_ONLY = 0x80000000;
        
        // Placeholder positions
        long totalHeaderSizePos;
        long nameOffsetPos;
        long exportOffsetPos;
        long importOffsetPos;
        long dependsOffsetPos;
        long softPackageRefsOffsetPos;
        long searchableNamesOffsetPos;
        long thumbnailOffsetPos;
        long assetRegistryOffsetPos;
        long preloadDependencyOffsetPos;
        
        // --- Write header ---
        
        // Tag
        writer.Write(PACKAGE_FILE_TAG);
        
        // LegacyFileVersion = -8 (UE5)
        writer.Write(LEGACY_FILE_VERSION);
        
        // LegacyUE3Version = 0 for unversioned
        writer.Write(0);
        
        // ObjectVersion = 0 for unversioned
        writer.Write(0);
        
        // ObjectVersionUE5 = 0 for unversioned (since LegacyFileVersion <= -8)
        writer.Write(0);
        
        // FileVersionLicenseeUE
        writer.Write(0);
        
        // Custom version count = 0 for unversioned
        writer.Write(0);
        
        // TotalHeaderSize (SectionSixOffset)
        totalHeaderSizePos = writer.BaseStream.Position;
        writer.Write(0); // placeholder
        
        // FolderName - write full package path from context
        string packagePath = GetFullPackagePath();
        WriteFString(writer, packagePath);
        
        // PackageFlags
        writer.Write(PKG_FILTER_EDITOR_ONLY);
        
        // NameCount
        writer.Write(nameMap.Count);
        
        // NameOffset
        nameOffsetPos = writer.BaseStream.Position;
        writer.Write(0); // placeholder
        
        // SoftObjectPathsCount, SoftObjectPathsOffset (UE5 - ADD_SOFTOBJECTPATH_LIST)
        writer.Write(0);
        writer.Write(0);
        
        // GatherableTextDataCount, GatherableTextDataOffset
        writer.Write(0);
        writer.Write(0);
        
        // ExportCount
        writer.Write(exports.Count);
        
        // ExportOffset
        exportOffsetPos = writer.BaseStream.Position;
        writer.Write(0); // placeholder
        
        // ImportCount
        writer.Write(imports.Count);
        
        // ImportOffset
        importOffsetPos = writer.BaseStream.Position;
        writer.Write(0); // placeholder
        
        // DependsOffset
        dependsOffsetPos = writer.BaseStream.Position;
        writer.Write(0); // placeholder
        
        // SoftPackageReferencesCount, SoftPackageReferencesOffset
        writer.Write(0);
        softPackageRefsOffsetPos = writer.BaseStream.Position;
        writer.Write(0); // placeholder
        
        // SearchableNamesOffset
        searchableNamesOffsetPos = writer.BaseStream.Position;
        writer.Write(0); // placeholder
        
        // ThumbnailTableOffset
        thumbnailOffsetPos = writer.BaseStream.Position;
        writer.Write(0); // placeholder
        
        // PackageGuid (16 bytes)
        writer.Write(Guid.NewGuid().ToByteArray());
        
        // Generations (1 generation)
        writer.Write(1);
        writer.Write(exports.Count);
        writer.Write(nameMap.Count);
        
        // SavedByEngineVersion (FEngineVersion)
        writer.Write((ushort)5); // major
        writer.Write((ushort)3); // minor
        writer.Write((ushort)2); // patch
        writer.Write((uint)0); // changelist
        WriteFString(writer, "++UE5+Release-5.3");
        
        // CompatibleWithEngineVersion
        writer.Write((ushort)5);
        writer.Write((ushort)3);
        writer.Write((ushort)2);
        writer.Write((uint)0);
        WriteFString(writer, "++UE5+Release-5.3");
        
        // CompressionFlags
        writer.Write((uint)0);
        
        // CompressedChunks count
        writer.Write(0);
        
        // PackageSource
        writer.Write((uint)0);
        
        // AdditionalPackagesToCook count
        writer.Write(0);
        
        // AssetRegistryDataOffset
        assetRegistryOffsetPos = writer.BaseStream.Position;
        writer.Write(0); // placeholder
        
        // BulkDataStartOffset
        writer.Write((long)-1);
        
        // WorldTileInfoDataOffset
        writer.Write(0);
        
        // ChunkIDs count
        writer.Write(0);
        
        // PreloadDependencyCount, PreloadDependencyOffset
        writer.Write(0);
        preloadDependencyOffsetPos = writer.BaseStream.Position;
        writer.Write(0); // placeholder
        
        // NamesReferencedFromExportDataCount
        writer.Write(nameMap.Count);
        
        // PayloadTocOffset (UE5.1+ DATA_RESOURCES version)
        writer.Write((long)-1);
        
        // DataResourceOffset (UE5.1+)
        writer.Write(0);
        
        // --- Write name map ---
        long nameOffset = writer.BaseStream.Position;
        foreach (var name in nameMap)
        {
            // Write name as FString with save hash
            WriteFString(writer, name);
            // Non-case preserving hash (uint32)
            writer.Write((uint)0);
        }
        
        // --- Write imports ---
        long importOffset = writer.BaseStream.Position;
        foreach (var imp in imports)
        {
            // ClassPackage (FName)
            writer.Write(FindOrAddName(nameMap, imp.ClassPackage));
            writer.Write(0);
            // ClassName (FName)
            writer.Write(FindOrAddName(nameMap, imp.ClassName));
            writer.Write(0);
            // OuterIndex (FPackageIndex)
            writer.Write(imp.OuterIndex);
            // ObjectName (FName)
            writer.Write(FindOrAddName(nameMap, imp.ObjectName));
            writer.Write(0);
        }
        
        // --- Write exports ---
        long exportOffset = writer.BaseStream.Position;
        foreach (var exp in exports)
        {
            // ClassIndex (FPackageIndex)
            writer.Write(exp.ClassIndex);
            // SuperIndex (FPackageIndex)
            writer.Write(0);
            // TemplateIndex (FPackageIndex) - UE4.26+
            writer.Write(0);
            // OuterIndex (FPackageIndex)
            writer.Write(exp.OuterIndex);
            // ObjectName (FName)
            writer.Write(FindOrAddName(nameMap, exp.ObjectName));
            writer.Write(0);
            // ObjectFlags
            writer.Write(exp.ObjectFlags);
            // SerialSize
            writer.Write((long)exp.SerialSize);
            // SerialOffset
            writer.Write((long)exp.SerialOffset);
            // bForcedExport
            writer.Write(0);
            // bNotForClient
            writer.Write(0);
            // bNotForServer
            writer.Write(0);
            // PackageGuid
            writer.Write(Guid.Empty.ToByteArray());
            // PackageFlags
            writer.Write((uint)0);
            // bNotAlwaysLoadedForEditorGame
            writer.Write(0);
            // bIsAsset
            writer.Write(0);
            // bGeneratePublicHash
            writer.Write(0);
            // FirstExportDependency
            writer.Write(0);
            // SerializationBeforeSerializationDependencies
            writer.Write(0);
            // CreateBeforeSerializationDependencies
            writer.Write(0);
            // SerializationBeforeCreateDependencies
            writer.Write(0);
            // CreateBeforeCreateDependencies
            writer.Write(0);
        }
        
        // --- Write depends map ---
        long dependsOffset = writer.BaseStream.Position;
        for (int i = 0; i < exports.Count; i++)
        {
            writer.Write(0); // empty array count
        }
        
        // Soft package refs, searchable names, thumbnail, asset registry all at end
        long endOffset = writer.BaseStream.Position;
        
        // --- Fix up offsets ---
        int totalHeaderSize = (int)endOffset;
        
        writer.BaseStream.Seek(totalHeaderSizePos, SeekOrigin.Begin);
        writer.Write(totalHeaderSize);
        
        writer.BaseStream.Seek(nameOffsetPos, SeekOrigin.Begin);
        writer.Write((int)nameOffset);
        
        writer.BaseStream.Seek(exportOffsetPos, SeekOrigin.Begin);
        writer.Write((int)exportOffset);
        
        writer.BaseStream.Seek(importOffsetPos, SeekOrigin.Begin);
        writer.Write((int)importOffset);
        
        writer.BaseStream.Seek(dependsOffsetPos, SeekOrigin.Begin);
        writer.Write((int)dependsOffset);
        
        writer.BaseStream.Seek(softPackageRefsOffsetPos, SeekOrigin.Begin);
        writer.Write((int)endOffset);
        
        writer.BaseStream.Seek(searchableNamesOffsetPos, SeekOrigin.Begin);
        writer.Write((int)endOffset);
        
        writer.BaseStream.Seek(thumbnailOffsetPos, SeekOrigin.Begin);
        writer.Write((int)endOffset);
        
        writer.BaseStream.Seek(assetRegistryOffsetPos, SeekOrigin.Begin);
        writer.Write((int)endOffset);
        
        writer.BaseStream.Seek(preloadDependencyOffsetPos, SeekOrigin.Begin);
        writer.Write((int)endOffset);
        
        writer.BaseStream.Seek(endOffset, SeekOrigin.Begin);
    }
    
    private void WriteFString(BinaryWriter writer, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            writer.Write(0);
            return;
        }
        
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length + 1);
        writer.Write(bytes);
        writer.Write((byte)0);
    }
    
    private int FindOrAddName(List<string> nameMap, string name)
    {
        int idx = nameMap.IndexOf(name);
        if (idx >= 0) return idx;
        nameMap.Add(name);
        return nameMap.Count - 1;
    }
    
    private class LegacyImportEntry
    {
        public string ClassPackage { get; set; } = "";
        public string ClassName { get; set; } = "";
        public int OuterIndex { get; set; }
        public string ObjectName { get; set; } = "";
    }
    
    private class LegacyExportEntry
    {
        public int ClassIndex { get; set; }
        public int SuperIndex { get; set; }
        public int TemplateIndex { get; set; }
        public int OuterIndex { get; set; }
        public string ObjectName { get; set; } = "";
        public uint ObjectFlags { get; set; }
        public long SerialSize { get; set; }
        public long SerialOffset { get; set; }
    }

    private void BuildImports()
    {
        // First pass: Create package imports for all referenced packages
        // Add CoreUObject package first (commonly needed)
        int coreUObjectIdx = GetOrCreatePackageImport(CORE_UOBJECT_PACKAGE);
        
        // Add Class import from CoreUObject
        var classImport = new Import(
            CORE_UOBJECT_PACKAGE,
            CLASS_NAME,
            new UAssetFPackageIndex(-(coreUObjectIdx + 1)), // outer is CoreUObject package
            CLASS_NAME,
            false,
            _legacyAsset!
        );
        _legacyAsset!.Imports.Add(classImport);
        
        // Process Zen import map
        for (int i = 0; i < _zenPackage.ImportMap.Count; i++)
        {
            var zenImport = _zenPackage.ImportMap[i];
            if (zenImport.IsNull())
                continue;
            
            try
            {
                int legacyIdx = ResolveZenImport(zenImport);
                _zenImportToLegacyIndex[zenImport.Value] = legacyIdx;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: Failed to resolve import {i}: {ex.Message}");
                // Create a placeholder import
                int placeholderIdx = CreatePlaceholderImport($"__UnresolvedImport_{i}__");
                _zenImportToLegacyIndex[zenImport.Value] = placeholderIdx;
            }
        }
    }

    private int ResolveZenImport(FPackageObjectIndex importIndex)
    {
        // Check cache first
        if (_zenImportToLegacyIndex.TryGetValue(importIndex.Value, out int cached))
            return cached;
        
        if (importIndex.IsScriptImport())
        {
            return ResolveScriptImport(importIndex);
        }
        else if (importIndex.IsPackageImport())
        {
            return ResolvePackageImport(importIndex);
        }
        else if (importIndex.IsExport())
        {
            // This shouldn't happen in import resolution
            throw new Exception($"Export index in import map: {importIndex}");
        }
        
        throw new Exception($"Unknown import type: {importIndex}");
    }

    private int ResolveScriptImport(FPackageObjectIndex importIndex)
    {
        if (_scriptObjects == null)
        {
            return CreatePlaceholderImport($"__Script_{importIndex.Value:X16}__");
        }
        
        var scriptObj = _scriptObjects.GetScriptObject(importIndex);
        if (scriptObj == null)
        {
            return CreatePlaceholderImport($"__Script_{importIndex.Value:X16}__");
        }
        
        string objectName = _scriptObjects.GetName(scriptObj.ObjectName);
        
        // Resolve the outer chain
        UAssetFPackageIndex outerIndex;
        string classPackage;
        string className;
        
        if (scriptObj.OuterIndex.IsNull())
        {
            // This is a top-level package
            return GetOrCreatePackageImport(objectName);
        }
        
        // Resolve outer recursively
        int outerLegacyIdx = ResolveScriptImport(scriptObj.OuterIndex);
        outerIndex = new UAssetFPackageIndex(-(outerLegacyIdx + 1));
        
        // Determine class
        if (_scriptObjects.IsClass(importIndex))
        {
            className = CLASS_NAME;
            classPackage = CORE_UOBJECT_PACKAGE;
        }
        else
        {
            className = OBJECT_NAME;
            classPackage = CORE_UOBJECT_PACKAGE;
        }
        
        // Create the import
        var import = new Import(
            classPackage,
            className,
            outerIndex,
            objectName,
            false,
            _legacyAsset!
        );
        
        int idx = _legacyAsset!.Imports.Count;
        _legacyAsset.Imports.Add(import);
        _zenImportToLegacyIndex[importIndex.Value] = idx;
        
        return idx;
    }

    private int ResolvePackageImport(FPackageObjectIndex importIndex)
    {
        var pkgImport = importIndex.GetPackageImport();
        
        if (pkgImport.ImportedPackageIndex >= _zenPackage.ImportedPackages.Count)
        {
            return CreatePlaceholderImport($"__Package_{importIndex.Value:X16}__");
        }
        
        ulong targetPackageId = _zenPackage.ImportedPackages[pkgImport.ImportedPackageIndex];
        string packageName = pkgImport.ImportedPackageIndex < _zenPackage.ImportedPackageNames.Count
            ? _zenPackage.ImportedPackageNames[pkgImport.ImportedPackageIndex]
            : $"/Game/__Package_{targetPackageId:X16}__";
        
        // Get or create package import
        int packageIdx = GetOrCreatePackageImport(packageName);
        
        // Try to resolve the actual export from the target package
        if (_context != null)
        {
            var targetPackage = _context.GetPackage(targetPackageId);
            if (targetPackage != null && pkgImport.ImportedPublicExportHashIndex < _zenPackage.ImportedPublicExportHashes.Count)
            {
                ulong exportHash = _zenPackage.ImportedPublicExportHashes[pkgImport.ImportedPublicExportHashIndex];
                
                foreach (var export in targetPackage.ExportMap)
                {
                    if (export.PublicExportHash == exportHash)
                    {
                        // Use GetName with scriptObjects for Global name type support
                        string exportName = targetPackage.GetName(export.ObjectName, _scriptObjects);
                        
                        // Get class info
                        string className = OBJECT_NAME;
                        string classPackage = CORE_UOBJECT_PACKAGE;
                        
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
                        
                        // Create import for the resolved export
                        var import = new Import(
                            classPackage,
                            className,
                            new UAssetFPackageIndex(-(packageIdx + 1)),
                            exportName,
                            false,
                            _legacyAsset!
                        );
                        
                        int idx = _legacyAsset!.Imports.Count;
                        _legacyAsset.Imports.Add(import);
                        return idx;
                    }
                }
            }
        }
        
        // Fallback: create a generic import under the package
        return CreatePlaceholderImport($"__Import_{importIndex.Value:X16}__", packageIdx);
    }

    private int GetOrCreatePackageImport(string packagePath)
    {
        if (_packageImportCache.TryGetValue(packagePath, out int cached))
            return cached;
        
        var import = new Import(
            CORE_UOBJECT_PACKAGE,
            PACKAGE_NAME,
            new UAssetFPackageIndex(0), // null outer
            packagePath,
            false,
            _legacyAsset!
        );
        
        int idx = _legacyAsset!.Imports.Count;
        _legacyAsset.Imports.Add(import);
        _packageImportCache[packagePath] = idx;
        
        return idx;
    }

    private int CreatePlaceholderImport(string name, int? outerPackageIdx = null)
    {
        int coreIdx = GetOrCreatePackageImport(CORE_UOBJECT_PACKAGE);
        
        UAssetFPackageIndex outerIndex = outerPackageIdx.HasValue 
            ? new UAssetFPackageIndex(-(outerPackageIdx.Value + 1))
            : new UAssetFPackageIndex(-(coreIdx + 1));
        
        var import = new Import(
            CORE_UOBJECT_PACKAGE,
            OBJECT_NAME,
            outerIndex,
            name,
            false,
            _legacyAsset!
        );
        
        int idx = _legacyAsset!.Imports.Count;
        _legacyAsset.Imports.Add(import);
        return idx;
    }

    private void BuildExports()
    {
        for (int i = 0; i < _zenPackage.ExportMap.Count; i++)
        {
            var zenExport = _zenPackage.ExportMap[i];
            
            try
            {
                var export = CreateExport(zenExport, i);
                _legacyAsset!.Exports.Add(export);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: Failed to create export {i}: {ex.Message}");
                // Create a minimal placeholder export
                var placeholder = new RawExport();
                placeholder.Asset = _legacyAsset!;
                placeholder.ObjectName = new FName(_legacyAsset, $"__Export_{i}__");
                _legacyAsset!.Exports.Add(placeholder);
            }
        }
    }

    private Export CreateExport(FExportMapEntry zenExport, int exportIndex)
    {
        string exportName = _zenPackage.GetName(zenExport.ObjectName);
        
        // Determine export type based on class
        string className = OBJECT_NAME;
        if (!zenExport.ClassIndex.IsNull() && zenExport.ClassIndex.IsScriptImport() && _scriptObjects != null)
        {
            var classObj = _scriptObjects.GetScriptObject(zenExport.ClassIndex);
            if (classObj != null)
                className = _scriptObjects.GetName(classObj.ObjectName);
        }
        
        // Get export data
        byte[] exportData = ExtractExportData(zenExport, exportIndex);
        
        // Create appropriate export type
        Export export;
        
        // Use RawExport to preserve exact data
        var rawExport = new RawExport();
        rawExport.Asset = _legacyAsset!;
        rawExport.Data = exportData;
        export = rawExport;
        
        // Set common properties
        export.ObjectName = new FName(_legacyAsset, exportName);
        export.ObjectFlags = (UAssetEObjectFlags)zenExport.ObjectFlags;
        export.SerialSize = exportData.Length;
        
        // Resolve class index
        if (!zenExport.ClassIndex.IsNull())
        {
            if (_zenImportToLegacyIndex.TryGetValue(zenExport.ClassIndex.Value, out int classIdx))
            {
                export.ClassIndex = new UAssetFPackageIndex(-(classIdx + 1));
            }
        }
        
        // Resolve outer index
        if (!zenExport.OuterIndex.IsNull())
        {
            if (zenExport.OuterIndex.IsExport())
            {
                int outerExportIdx = (int)zenExport.OuterIndex.GetExportIndex();
                export.OuterIndex = new UAssetFPackageIndex(outerExportIdx + 1);
            }
            else if (_zenImportToLegacyIndex.TryGetValue(zenExport.OuterIndex.Value, out int outerIdx))
            {
                export.OuterIndex = new UAssetFPackageIndex(-(outerIdx + 1));
            }
        }
        
        // Resolve super index
        if (!zenExport.SuperIndex.IsNull())
        {
            if (_zenImportToLegacyIndex.TryGetValue(zenExport.SuperIndex.Value, out int superIdx))
            {
                export.SuperIndex = new UAssetFPackageIndex(-(superIdx + 1));
            }
        }
        
        // Resolve template index
        if (!zenExport.TemplateIndex.IsNull())
        {
            if (zenExport.TemplateIndex.IsExport())
            {
                int templateExportIdx = (int)zenExport.TemplateIndex.GetExportIndex();
                export.TemplateIndex = new UAssetFPackageIndex(templateExportIdx + 1);
            }
            else if (_zenImportToLegacyIndex.TryGetValue(zenExport.TemplateIndex.Value, out int templateIdx))
            {
                export.TemplateIndex = new UAssetFPackageIndex(-(templateIdx + 1));
            }
        }
        
        return export;
    }

    private byte[] ExtractExportData(FExportMapEntry zenExport, int exportIndex)
    {
        // Find this export in the export bundles
        long cookedOffset = (long)zenExport.CookedSerialOffset;
        long cookedSize = (long)zenExport.CookedSerialSize;
        
        // Calculate actual offset within raw data
        long dataOffset = _zenPackage.Summary.HeaderSize + cookedOffset;
        
        if (dataOffset < 0 || dataOffset + cookedSize > _rawPackageData.Length)
        {
            Console.Error.WriteLine($"Export {exportIndex} data out of bounds: offset={dataOffset}, size={cookedSize}, total={_rawPackageData.Length}");
            return Array.Empty<byte>();
        }
        
        byte[] data = new byte[cookedSize];
        Array.Copy(_rawPackageData, dataOffset, data, 0, cookedSize);
        return data;
    }

    private byte[] ExtractBulkData()
    {
        // For now, return empty - bulk data handling needs container-level support
        // The raw export data should include inline bulk data
        return Array.Empty<byte>();
    }
}
