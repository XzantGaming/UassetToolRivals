#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.ExportTypes;
using UAssetAPI.ExportTypes.Texture;
using UAssetAPI.Unversioned;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;

namespace UAssetTool;

/// <summary>
/// Unified UAsset Tool - Combines detection, fixing, and patching for all UE asset types.
/// Supports both interactive JSON mode (stdin/stdout) and CLI mode.
/// </summary>
public class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Set UTF-8 encoding for console to properly handle Unicode characters (Chinese, Korean, etc.)
        Console.InputEncoding = System.Text.Encoding.UTF8;
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        
        // CLI mode: command-line arguments
        if (args.Length > 0)
        {
            return RunCliMode(args);
        }
        
        // Interactive JSON mode: read from stdin
        return await RunInteractiveMode();
    }

    #region CLI Mode
    
    private static int RunCliMode(string[] args)
    {
        string command = args[0].ToLower();
        
        try
        {
            return command switch
            {
                "detect" => CliDetect(args),
                "fix" => CliFix(args),
                "batch_detect" => CliBatchDetect(args),
                "dump" => CliDump(args),
                "to_zen" => CliToZen(args),
                "to_iostore" => CliToIoStore(args),
                "inspect_zen" => CliInspectZen(args),
                "create_pak" => CliCreatePak(args),
                "create_companion_pak" => CliCreateCompanionPak(args),
                "create_iostore_bundle" => CliCreateIoStoreBundle(args),
                "create_mod_iostore" => CliCreateModIoStore(args),
                "extract_iostore" => CliExtractIoStore(args),
                "extract_iostore_legacy" => CliExtractIoStoreLegacy(args),
                "is_iostore_compressed" => CliIsIoStoreCompressed(args),
                "extract_script_objects" => CliExtractScriptObjects(args),
                "recompress_iostore" => CliRecompressIoStore(args),
                "cityhash" => CliCityHash(args),
                "from_json" => CliFromJson(args),
                "dump_zen_from_game" => CliDumpZenFromGame(args),
                "extract_pak" => CliExtractPak(args),
                "modify_colors" => CliModifyColors(args),
                "niagara_list" => CliNiagaraList(args),
                "niagara_details" => CliNiagaraDetails(args),
                "niagara_edit" => CliNiagaraEdit(args),
                "help" or "--help" or "-h" => CliHelp(),
                _ => throw new Exception($"Unknown command: {command}")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (Environment.GetEnvironmentVariable("DEBUG") == "1")
            {
                Console.Error.WriteLine(ex.StackTrace);
            }
            return 1;
        }
    }
    
    private static int CliHelp()
    {
        Console.WriteLine("UAssetTool - Unified UE Asset Tool");
        Console.WriteLine();
        Console.WriteLine("Usage: UAssetTool <command> [args]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  detect <uasset_path> [usmap_path]       - Detect asset type");
        Console.WriteLine("  fix <uasset_path> [usmap_path]          - Fix SerializeSize for meshes");
        Console.WriteLine("  batch_detect <directory> [usmap_path]   - Detect all assets in directory");
        Console.WriteLine("  dump <uasset_path> <usmap_path>         - Dump detailed asset info");
        Console.WriteLine("  to_zen <uasset_path> [usmap_path]       - Convert legacy asset to Zen format");
        Console.WriteLine("  to_iostore <output_path> <zen_files...> - Create IoStore from Zen packages");
        Console.WriteLine("  inspect_zen <zen_asset_path>            - Inspect Zen-formatted asset");
        Console.WriteLine("  create_pak <output.pak> <files...>      - Create encrypted PAK file");
        Console.WriteLine("  create_companion_pak <output.pak> <file_list...> - Create companion PAK for IoStore");
        Console.WriteLine("  create_iostore_bundle <output_base> <files...>   - Create complete IoStore bundle (.utoc/.ucas/.pak)");
        Console.WriteLine("  create_mod_iostore <output_base> <uasset_files...> - Convert legacy assets to Zen and create IoStore bundle");
        Console.WriteLine("  is_iostore_compressed <utoc_path>              - Check if IoStore is compressed");
        Console.WriteLine("  extract_script_objects <paks_path> <output>    - Extract ScriptObjects.bin from game");
        Console.WriteLine("  recompress_iostore <utoc_path>                 - Recompress IoStore with Oodle");
        Console.WriteLine("  from_json <json_path> <output_uasset> [usmap]  - Convert JSON back to uasset");
        Console.WriteLine("  extract_pak <pak_path> <output_dir> [options]   - Extract assets from legacy PAK file");
        Console.WriteLine();
        Console.WriteLine("Zen Conversion Pipeline (2 steps for debugging):");
        Console.WriteLine("  Step 1: to_zen    - Legacy .uasset/.uexp -> .uzenasset");
        Console.WriteLine("  Step 2: to_iostore - .uzenasset files -> .utoc/.ucas");
        Console.WriteLine();
        Console.WriteLine("Interactive mode: Run without arguments to use JSON stdin/stdout");
        return 0;
    }
    
    private static int CliToZen(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: UAssetTool to_zen <uasset_path> [usmap_path]");
            return 1;
        }

        string uassetPath = args[1];
        string? usmapPath = args.Length > 2 ? args[2] : null;

        if (!File.Exists(uassetPath))
        {
            Console.Error.WriteLine($"File not found: {uassetPath}");
            return 1;
        }

        try
        {
            Console.Error.WriteLine($"[CliToZen] Converting {uassetPath} to Zen format...");
            
            byte[] zenData = ZenPackage.ZenConverter.ConvertLegacyToZen(uassetPath, usmapPath);
            
            string outputPath = Path.ChangeExtension(uassetPath, ".uzenasset");
            File.WriteAllBytes(outputPath, zenData);
            
            Console.WriteLine($"SUCCESS: Converted to {outputPath} ({zenData.Length} bytes)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            if (Environment.GetEnvironmentVariable("DEBUG") == "1")
            {
                Console.Error.WriteLine(ex.StackTrace);
            }
            return 1;
        }
    }
    
    private static int CliInspectZen(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: UAssetTool inspect_zen <zen_asset_path>");
            return 1;
        }

        string zenPath = args[1];
        if (!File.Exists(zenPath))
        {
            Console.Error.WriteLine($"File not found: {zenPath}");
            return 1;
        }

        try
        {
            ZenPackage.ZenInspector.InspectZenAsset(zenPath);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            if (Environment.GetEnvironmentVariable("DEBUG") == "1")
            {
                Console.Error.WriteLine(ex.StackTrace);
            }
            return 1;
        }
    }
    
    private static int CliToIoStore(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: UAssetTool to_iostore <output_path> <zen_file1> [zen_file2] ... [--game-path <path>]");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Arguments:");
            Console.Error.WriteLine("  output_path    - Output path for .utoc/.ucas files (without extension)");
            Console.Error.WriteLine("  zen_files      - One or more .uzenasset files to pack");
            Console.Error.WriteLine("  --game-path    - Game path prefix (default: Marvel/Content/)");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Example:");
            Console.Error.WriteLine("  UAssetTool to_iostore MyMod_P file1.uzenasset file2.uzenasset");
            return 1;
        }

        string outputPath = args[1];
        string gamePathPrefix = "Marvel/Content/";
        var zenFiles = new List<string>();

        // Parse arguments
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--game-path" && i + 1 < args.Length)
            {
                gamePathPrefix = args[++i];
            }
            else if (File.Exists(args[i]))
            {
                zenFiles.Add(args[i]);
            }
            else
            {
                Console.Error.WriteLine($"Warning: File not found: {args[i]}");
            }
        }

        if (zenFiles.Count == 0)
        {
            Console.Error.WriteLine("Error: No valid .uzenasset files provided");
            return 1;
        }

        try
        {
            string containerName = Path.GetFileNameWithoutExtension(outputPath);
            string utocPath = outputPath + ".utoc";
            Console.Error.WriteLine($"[CliToIoStore] Creating IoStore container: {containerName}");
            Console.Error.WriteLine($"[CliToIoStore] Output: {utocPath} / {outputPath}.ucas");
            Console.Error.WriteLine($"[CliToIoStore] Zen files: {zenFiles.Count}");

            using var writer = new IoStore.IoStoreWriter(
                utocPath,
                IoStore.EIoStoreTocVersion.PerfectHashWithOverflow,
                IoStore.EIoContainerHeaderVersion.NoExportInfo,
                "../../../");

            foreach (var zenFile in zenFiles)
            {
                // Derive game path from filename
                string fileName = Path.GetFileNameWithoutExtension(zenFile);
                if (fileName.EndsWith(".uzenasset", StringComparison.OrdinalIgnoreCase))
                    fileName = fileName[..^10];
                
                string gamePath = "../../../" + gamePathPrefix + fileName;
                
                // Read zen file and write as chunk
                byte[] zenData = File.ReadAllBytes(zenFile);
                var packageId = IoStore.FPackageId.FromName("/" + gamePathPrefix + fileName);
                var chunkId = IoStore.FIoChunkId.FromPackageId(packageId, 0, IoStore.EIoChunkType.ExportBundleData);
                var storeEntry = new IoStore.StoreEntry { ExportCount = 1, ExportBundleCount = 1 };
                writer.WritePackageChunk(chunkId, gamePath, zenData, storeEntry);
            }

            writer.Complete();

            Console.WriteLine($"SUCCESS: Created IoStore container at {utocPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            if (Environment.GetEnvironmentVariable("DEBUG") == "1")
            {
                Console.Error.WriteLine(ex.StackTrace);
            }
            return 1;
        }
    }

    private static int CliCreatePak(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: UAssetTool create_pak <output.pak> <file1> [file2] ...");
            Console.Error.WriteLine("  Creates an encrypted PAK file with the specified files.");
            Console.Error.WriteLine("  Files are added with their relative paths.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Options:");
            Console.Error.WriteLine("  --mount-point <path>  - Mount point (default: ../../../)");
            Console.Error.WriteLine("  --aes-key <hex>       - AES key in hex format");
            return 1;
        }

        string outputPath = args[1];
        string mountPoint = "../../../";
        string? aesKey = null;
        var files = new List<(string relativePath, string absolutePath)>();

        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--mount-point" && i + 1 < args.Length)
            {
                mountPoint = args[++i];
            }
            else if (args[i] == "--aes-key" && i + 1 < args.Length)
            {
                aesKey = args[++i];
            }
            else if (File.Exists(args[i]))
            {
                string absPath = Path.GetFullPath(args[i]);
                string relPath = Path.GetFileName(args[i]);
                files.Add((relPath, absPath));
            }
            else
            {
                Console.Error.WriteLine($"Warning: File not found: {args[i]}");
            }
        }

        if (files.Count == 0)
        {
            Console.Error.WriteLine("Error: No valid files provided");
            return 1;
        }

        try
        {
            using var pakWriter = new IoStore.PakWriter(mountPoint, 0, aesKey);

            foreach (var (relPath, absPath) in files)
            {
                byte[] data = File.ReadAllBytes(absPath);
                pakWriter.AddEntry(relPath, data);
                Console.Error.WriteLine($"  Added: {relPath} ({data.Length} bytes)");
            }

            pakWriter.Write(outputPath);
            Console.WriteLine($"SUCCESS: Created PAK file at {outputPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }

    private static int CliCreateCompanionPak(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: UAssetTool create_companion_pak <output.pak> <file_path1> [file_path2] ...");
            Console.Error.WriteLine("  Creates a companion PAK file for IoStore bundles.");
            Console.Error.WriteLine("  The PAK contains a 'chunknames' entry listing all provided file paths.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Options:");
            Console.Error.WriteLine("  --mount-point <path>  - Mount point (default: ../../../)");
            Console.Error.WriteLine("  --path-hash-seed <n>  - Path hash seed (default: 0)");
            Console.Error.WriteLine("  --aes-key <hex>       - AES key in hex format");
            return 1;
        }

        string outputPath = args[1];
        string mountPoint = "../../../";
        ulong pathHashSeed = 0;
        string? aesKey = null;
        var filePaths = new List<string>();

        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--mount-point" && i + 1 < args.Length)
            {
                mountPoint = args[++i];
            }
            else if (args[i] == "--path-hash-seed" && i + 1 < args.Length)
            {
                pathHashSeed = ulong.Parse(args[++i]);
            }
            else if (args[i] == "--aes-key" && i + 1 < args.Length)
            {
                aesKey = args[++i];
            }
            else
            {
                // Add as a file path (doesn't need to exist - just a path string)
                filePaths.Add(args[i]);
            }
        }

        if (filePaths.Count == 0)
        {
            Console.Error.WriteLine("Error: No file paths provided");
            return 1;
        }

        try
        {
            IoStore.ChunkNamesPakWriter.Create(outputPath, filePaths, mountPoint, pathHashSeed, aesKey);
            Console.WriteLine($"SUCCESS: Created companion PAK at {outputPath}");
            Console.WriteLine($"  Files listed: {filePaths.Count}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }

    private static int CliCreateIoStoreBundle(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: UAssetTool create_iostore_bundle <output_base> <file1> [file2] ...");
            Console.Error.WriteLine("  Creates a complete IoStore bundle (.utoc, .ucas, .pak) from the specified files.");
            Console.Error.WriteLine("  output_base should be the base name without extension (e.g., 'MyMod_P')");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Options:");
            Console.Error.WriteLine("  --mount-point <path>  - Mount point (default: ../../../)");
            Console.Error.WriteLine("  --compress            - Enable Oodle compression (default: enabled)");
            Console.Error.WriteLine("  --no-compress         - Disable compression");
            Console.Error.WriteLine("  --encrypt             - Enable AES encryption");
            Console.Error.WriteLine("  --aes-key <hex>       - AES key in hex format");
            return 1;
        }

        string outputBase = args[1];
        string mountPoint = "../../../";
        bool enableCompression = true;
        bool enableEncryption = false;
        string? aesKey = null;
        var files = new List<(string relativePath, string absolutePath)>();

        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--mount-point" && i + 1 < args.Length)
            {
                mountPoint = args[++i];
            }
            else if (args[i] == "--compress")
            {
                enableCompression = true;
            }
            else if (args[i] == "--no-compress")
            {
                enableCompression = false;
            }
            else if (args[i] == "--encrypt")
            {
                enableEncryption = true;
            }
            else if (args[i] == "--aes-key" && i + 1 < args.Length)
            {
                aesKey = args[++i];
                enableEncryption = true;
            }
            else if (File.Exists(args[i]))
            {
                string absPath = Path.GetFullPath(args[i]);
                string relPath = Path.GetFileName(args[i]);
                files.Add((relPath, absPath));
            }
            else
            {
                Console.Error.WriteLine($"Warning: File not found: {args[i]}");
            }
        }

        if (files.Count == 0)
        {
            Console.Error.WriteLine("Error: No valid files provided");
            return 1;
        }

        try
        {
            string utocPath = outputBase + ".utoc";
            string pakPath = outputBase + ".pak";

            Console.Error.WriteLine($"[CreateIoStoreBundle] Creating IoStore bundle: {outputBase}");
            Console.Error.WriteLine($"[CreateIoStoreBundle]   Files: {files.Count}");
            Console.Error.WriteLine($"[CreateIoStoreBundle]   Compression: {(enableCompression ? "Oodle" : "None")}");
            Console.Error.WriteLine($"[CreateIoStoreBundle]   Encryption: {(enableEncryption ? "AES-256" : "None")}");

            // Create IoStore container
            using var ioStoreWriter = new IoStore.IoStoreWriter(
                utocPath,
                IoStore.EIoStoreTocVersion.PerfectHashWithOverflow,
                IoStore.EIoContainerHeaderVersion.NoExportInfo,
                mountPoint,
                enableCompression,
                enableEncryption,
                aesKey);

            var filePaths = new List<string>();

            foreach (var (relPath, absPath) in files)
            {
                byte[] data = File.ReadAllBytes(absPath);

                // Create package ID from filename
                string packageName = relPath;
                if (packageName.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
                    packageName = packageName[..^7];
                else if (packageName.EndsWith(".uexp", StringComparison.OrdinalIgnoreCase))
                    packageName = packageName[..^5];

                var packageId = IoStore.FPackageId.FromName("/" + packageName);
                var chunkId = IoStore.FIoChunkId.FromPackageId(packageId, 0, IoStore.EIoChunkType.ExportBundleData);

                var storeEntry = new IoStore.StoreEntry
                {
                    ExportCount = 1,
                    ExportBundleCount = 1,
                    LoadOrder = 0
                };

                string gamePath = mountPoint + relPath;
                ioStoreWriter.WritePackageChunk(chunkId, gamePath, data, storeEntry);
                filePaths.Add(relPath);

                Console.Error.WriteLine($"  Added: {relPath} ({data.Length} bytes)");
            }

            ioStoreWriter.Complete();

            // Create companion PAK
            IoStore.ChunkNamesPakWriter.Create(pakPath, filePaths, mountPoint, 0, aesKey);

            Console.WriteLine($"SUCCESS: Created IoStore bundle:");
            Console.WriteLine($"  {utocPath}");
            Console.WriteLine($"  {Path.ChangeExtension(utocPath, ".ucas")}");
            Console.WriteLine($"  {pakPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            if (Environment.GetEnvironmentVariable("DEBUG") == "1")
            {
                Console.Error.WriteLine(ex.StackTrace);
            }
            return 1;
        }
    }

    private static int CliCreateModIoStore(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: UAssetTool create_mod_iostore <output_base> <uasset1> [uasset2] ...");
            Console.Error.WriteLine("  Converts legacy .uasset/.uexp files to Zen format and creates IoStore bundle.");
            Console.Error.WriteLine("  This is the complete pipeline for Marvel Rivals mod creation.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Options:");
            Console.Error.WriteLine("  --usmap <path>        - Path to .usmap file for property parsing");
            Console.Error.WriteLine("  --mount-point <path>  - Mount point (default: ../../../)");
            Console.Error.WriteLine("  --game-path <prefix>  - Game path prefix (default: Marvel/Content/)");
            Console.Error.WriteLine("  --compress            - Enable Oodle compression (default: enabled)");
            Console.Error.WriteLine("  --no-compress         - Disable compression");
            Console.Error.WriteLine("  --encrypt             - Enable AES encryption");
            Console.Error.WriteLine("  --aes-key <hex>       - AES key in hex format");
            return 1;
        }

        string outputBase = args[1];
        string mountPoint = "../../../";
        string gamePathPrefix = "Marvel/Content/";
        string? usmapPath = null;
        bool enableCompression = true;
        bool enableEncryption = false;
        string? aesKey = null;
        var uassetFiles = new List<string>();

        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--usmap" && i + 1 < args.Length)
            {
                usmapPath = args[++i];
            }
            else if (args[i] == "--mount-point" && i + 1 < args.Length)
            {
                mountPoint = args[++i];
            }
            else if (args[i] == "--game-path" && i + 1 < args.Length)
            {
                gamePathPrefix = args[++i];
            }
            else if (args[i] == "--compress")
            {
                enableCompression = true;
            }
            else if (args[i] == "--no-compress")
            {
                enableCompression = false;
            }
            else if (args[i] == "--encrypt")
            {
                enableEncryption = true;
            }
            else if (args[i] == "--aes-key" && i + 1 < args.Length)
            {
                aesKey = args[++i];
                enableEncryption = true;
            }
            else if (args[i].EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) && File.Exists(args[i]))
            {
                uassetFiles.Add(Path.GetFullPath(args[i]));
            }
            else if (File.Exists(args[i]))
            {
                Console.Error.WriteLine($"Warning: Skipping non-.uasset file: {args[i]}");
            }
            else
            {
                Console.Error.WriteLine($"Warning: File not found: {args[i]}");
            }
        }

        if (uassetFiles.Count == 0)
        {
            Console.Error.WriteLine("Error: No valid .uasset files provided");
            return 1;
        }

        try
        {
            string utocPath = outputBase + ".utoc";
            string pakPath = outputBase + ".pak";

            Console.Error.WriteLine($"[CreateModIoStore] Creating IoStore mod bundle: {outputBase}");
            Console.Error.WriteLine($"[CreateModIoStore]   Assets: {uassetFiles.Count}");
            Console.Error.WriteLine($"[CreateModIoStore]   Compression: {(enableCompression ? "Oodle" : "None")}");
            Console.Error.WriteLine($"[CreateModIoStore]   Encryption: {(enableEncryption ? "AES-256" : "None")}");

            // Create IoStore container
            using var ioStoreWriter = new IoStore.IoStoreWriter(
                utocPath,
                IoStore.EIoStoreTocVersion.PerfectHashWithOverflow,
                IoStore.EIoContainerHeaderVersion.NoExportInfo,
                mountPoint,
                enableCompression,
                enableEncryption,
                aesKey);

            var filePaths = new List<string>();

            foreach (var uassetPath in uassetFiles)
            {
                string assetName = Path.GetFileNameWithoutExtension(uassetPath);
                Console.Error.WriteLine($"  Converting: {assetName}");

                // Convert legacy asset to Zen format and get the package path and FZenPackage
                byte[] zenData;
                string packagePath;
                ZenPackage.FZenPackage zenPackage;
                try
                {
                    (zenData, packagePath, zenPackage) = ZenPackage.ZenConverter.ConvertLegacyToZenFull(
                        uassetPath,
                        usmapPath,
                        ZenPackage.EIoContainerHeaderVersion.NoExportInfo);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"    ERROR converting {assetName}: {ex.Message}");
                    continue;
                }

                Console.Error.WriteLine($"    Zen size: {zenData.Length} bytes");
                Console.Error.WriteLine($"    Package path: {packagePath}");

                // Create package ID using the /Game/... format to match the package name in Zen
                // The package name in Zen is stored as /Game/Marvel/Characters/...
                // We need to convert Marvel/Content/Marvel/Characters/... back to /Game/Marvel/Characters/...
                string gamePackagePath;
                if (packagePath.StartsWith("Marvel/Content/"))
                {
                    // Convert Marvel/Content/X to /Game/X
                    gamePackagePath = "/Game/" + packagePath.Substring("Marvel/Content/".Length);
                }
                else
                {
                    gamePackagePath = "/" + packagePath;
                }
                Console.Error.WriteLine($"    Game package path (for ID): {gamePackagePath}");
                var packageId = IoStore.FPackageId.FromName(gamePackagePath);
                var chunkId = IoStore.FIoChunkId.FromPackageId(packageId, 0, IoStore.EIoChunkType.ExportBundleData);

                // Create store entry with imported packages from the Zen package
                var storeEntry = new IoStore.StoreEntry
                {
                    ExportCount = zenPackage.ExportMap.Count,
                    ExportBundleCount = 1,
                    LoadOrder = 0
                };
                
                // Add imported packages to store entry so game can resolve them
                foreach (ulong importedPkgId in zenPackage.ImportedPackages)
                {
                    storeEntry.ImportedPackages.Add(new IoStore.FPackageId(importedPkgId));
                }
                if (storeEntry.ImportedPackages.Count > 0)
                {
                    Console.Error.WriteLine($"    StoreEntry has {storeEntry.ImportedPackages.Count} imported packages");
                }

                // Write to IoStore using the actual package path (with .uasset extension for directory index)
                string fullPath = mountPoint + packagePath + ".uasset";
                ioStoreWriter.WritePackageChunk(chunkId, fullPath, zenData, storeEntry);
                
                // Add both .uasset and .uexp to chunknames using the actual package path
                filePaths.Add(packagePath + ".uasset");
                filePaths.Add(packagePath + ".uexp");
                
                // If .ubulk exists, write it as a separate BulkData chunk
                string ubulkPath = Path.ChangeExtension(uassetPath, ".ubulk");
                if (File.Exists(ubulkPath))
                {
                    byte[] ubulkData = File.ReadAllBytes(ubulkPath);
                    var bulkChunkId = IoStore.FIoChunkId.FromPackageId(packageId, 0, IoStore.EIoChunkType.BulkData);
                    string bulkFullPath = mountPoint + packagePath + ".ubulk";
                    ioStoreWriter.WriteChunk(bulkChunkId, bulkFullPath, ubulkData);
                    filePaths.Add(packagePath + ".ubulk");
                    Console.Error.WriteLine($"    Added BulkData chunk: {ubulkData.Length} bytes");
                }

                Console.Error.WriteLine($"    Added to IoStore: {gamePackagePath}");
            }

            if (filePaths.Count == 0)
            {
                Console.Error.WriteLine("Error: No assets were successfully converted");
                return 1;
            }

            ioStoreWriter.Complete();

            // Create companion PAK
            IoStore.ChunkNamesPakWriter.Create(pakPath, filePaths, mountPoint, 0, aesKey);

            Console.WriteLine($"SUCCESS: Created IoStore mod bundle:");
            Console.WriteLine($"  {utocPath}");
            Console.WriteLine($"  {Path.ChangeExtension(utocPath, ".ucas")}");
            Console.WriteLine($"  {pakPath}");
            Console.WriteLine($"  Assets converted: {filePaths.Count}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            if (Environment.GetEnvironmentVariable("DEBUG") == "1")
            {
                Console.Error.WriteLine(ex.StackTrace);
            }
            return 1;
        }
    }

    private static int CliExtractIoStore(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: UAssetTool extract_iostore <utoc_path> <output_dir> [--chunk-id <id>] [--package <name>] [--aes <hex>]");
            Console.Error.WriteLine("Examples:");
            Console.Error.WriteLine("  UAssetTool extract_iostore pakchunk0-WindowsClient.utoc ./extracted");
            Console.Error.WriteLine("  UAssetTool extract_iostore pakchunk0-WindowsClient.utoc ./extracted --package /Game/Marvel/Characters/1033/1033001/Weapons/Stick_L/Meshes/SM_WP_1033001_Stick_L");
            Console.Error.WriteLine("  UAssetTool extract_iostore pakchunk0-WindowsClient.utoc ./extracted --aes 0C263D8C22DCB085894899C3A3796383E9BF9DE0CBFB08C9BF2DEF2E84F29D74 --package /Game/...");
            return 1;
        }

        string utocPath = args[1];
        string outputDir = args[2];
        string? packageName = null;
        string? chunkIdHex = null;
        string? aesKeyHex = null;

        for (int i = 3; i < args.Length; i++)
        {
            if (args[i] == "--package" && i + 1 < args.Length)
                packageName = args[++i];
            else if (args[i] == "--chunk-id" && i + 1 < args.Length)
                chunkIdHex = args[++i];
            else if ((args[i] == "--aes" || args[i] == "--aes-key") && i + 1 < args.Length)
                aesKeyHex = args[++i];
        }

        try
        {
            byte[]? aesKey = aesKeyHex != null ? Convert.FromHexString(aesKeyHex) : null;
            using var reader = new IoStore.IoStoreReader(utocPath, aesKey);
            Console.WriteLine($"Opened IoStore: {reader.ContainerName}");
            Console.WriteLine($"  TOC Version: {reader.Toc.Version}");
            Console.WriteLine($"  Chunks: {reader.Toc.Chunks.Count}");
            Console.WriteLine($"  Compression Methods: {string.Join(", ", reader.Toc.CompressionMethods)}");

            Directory.CreateDirectory(outputDir);

            if (packageName != null)
            {
                // Extract specific package
                var packageId = IoStore.FPackageId.FromName(packageName);
                var chunkId = IoStore.FIoChunkId.FromPackageId(packageId, 0, IoStore.EIoChunkType.ExportBundleData);

                if (reader.HasChunk(chunkId))
                {
                    byte[] data = reader.ReadChunk(chunkId);
                    string outputPath = Path.Combine(outputDir, Path.GetFileName(packageName) + ".uasset");
                    File.WriteAllBytes(outputPath, data);
                    Console.WriteLine($"Extracted: {outputPath} ({data.Length} bytes)");
                }
                else
                {
                    Console.Error.WriteLine($"Package not found: {packageName}");
                    return 1;
                }
            }
            else if (chunkIdHex != null)
            {
                // Extract specific chunk by ID
                byte[] chunkIdBytes = Convert.FromHexString(chunkIdHex);
                var chunkId = IoStore.FIoChunkId.FromBytes(chunkIdBytes);

                byte[] data = reader.ReadChunk(chunkId);
                string outputPath = Path.Combine(outputDir, $"chunk_{chunkIdHex}.bin");
                File.WriteAllBytes(outputPath, data);
                Console.WriteLine($"Extracted: {outputPath} ({data.Length} bytes)");
            }
            else
            {
                // List all chunks
                Console.WriteLine("\nChunks in container:");
                int count = 0;
                foreach (var chunk in reader.GetChunks())
                {
                    string? path = reader.GetChunkPath(chunk);
                    Console.WriteLine($"  [{count}] {chunk} -> {path ?? "(no path)"}");
                    count++;
                    if (count >= 100)
                    {
                        Console.WriteLine($"  ... and {reader.Toc.Chunks.Count - count} more");
                        break;
                    }
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (Environment.GetEnvironmentVariable("DEBUG") == "1")
                Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    /// <summary>
    /// Check if an IoStore container is compressed.
    /// Equivalent to retoc::is_iostore_compressed()
    /// </summary>
    private static int CliIsIoStoreCompressed(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: UAssetTool is_iostore_compressed <utoc_path>");
            return 1;
        }

        string utocPath = args[1];
        
        try
        {
            bool isCompressed = IoStore.IoStoreReader.IsCompressed(utocPath);
            Console.WriteLine(isCompressed ? "true" : "false");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Extract ScriptObjects.bin from game IoStore containers.
    /// Equivalent to retoc::extract_script_objects()
    /// </summary>
    private static int CliExtractScriptObjects(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: UAssetTool extract_script_objects <paks_path> <output_file>");
            Console.Error.WriteLine("Example: UAssetTool extract_script_objects \"C:/Games/MarvelRivals/MarvelGame/Marvel/Content/Paks\" ScriptObjects.bin");
            return 1;
        }

        string paksPath = args[1];
        string outputPath = args[2];

        try
        {
            byte[]? data = IoStore.IoStoreReader.ExtractScriptObjects(paksPath);
            if (data == null)
            {
                Console.Error.WriteLine("ScriptObjects not found in any IoStore container");
                return 1;
            }

            File.WriteAllBytes(outputPath, data);
            Console.WriteLine($"Extracted ScriptObjects.bin: {data.Length} bytes");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Recompress an IoStore container with Oodle compression.
    /// Equivalent to retoc::recompress_iostore()
    /// </summary>
    private static int CliRecompressIoStore(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: UAssetTool recompress_iostore <utoc_path>");
            Console.Error.WriteLine("Recompresses an IoStore container with Oodle compression");
            return 1;
        }

        string utocPath = args[1];

        try
        {
            // Check if already compressed
            if (IoStore.IoStoreReader.IsCompressed(utocPath))
            {
                Console.WriteLine("IoStore is already compressed, skipping");
                return 0;
            }

            string result = IoStore.IoStoreRecompressor.Recompress(utocPath);
            Console.WriteLine($"Recompressed: {result}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (Environment.GetEnvironmentVariable("DEBUG") == "1")
                Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    /// <summary>
    /// Compute CityHash64 of a string (UTF-16LE encoded, lowercase).
    /// This replaces the external hash_helper.exe tool.
    /// Usage: UAssetTool cityhash <string>
    /// </summary>
    private static int CliCityHash(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: UAssetTool cityhash <string>");
            Console.Error.WriteLine("Computes CityHash64 of lowercase UTF-16LE encoded string");
            return 1;
        }
        
        string input = args[1];
        ulong hash = IoStore.CityHash.CityHash64(input.ToLowerInvariant());
        
        // Output just the hash in hex format (same as hash_helper.exe)
        Console.WriteLine($"{hash:X16}");
        return 0;
    }

    /// <summary>
    /// Extract IoStore packages to legacy .uasset/.uexp format using native C# conversion.
    /// Usage: UAssetTool extract_iostore_legacy <utoc_path> <output_dir> [--script-objects <path>]
    /// </summary>
    private static int CliExtractIoStoreLegacy(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: UAssetTool extract_iostore_legacy <paks_directory> <output_dir> [options]");
            Console.Error.WriteLine("Extracts IoStore packages and converts them to legacy .uasset/.uexp format");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Arguments:");
            Console.Error.WriteLine("  <paks_directory>         Path to game's Paks directory (loads all .utoc files)");
            Console.Error.WriteLine("  <output_dir>             Output directory for extracted assets");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Options:");
            Console.Error.WriteLine("  --script-objects <path>  Path to ScriptObjects.bin for import resolution");
            Console.Error.WriteLine("  --global <path>          Path to global.utoc for script objects");
            Console.Error.WriteLine("  --container <path>       Additional container to load for cross-package imports");
            Console.Error.WriteLine("  --filter <patterns...>   Only extract packages matching patterns (space-separated)");
            Console.Error.WriteLine("  --with-deps              Also extract imported/referenced packages");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Examples:");
            Console.Error.WriteLine("  extract_iostore_legacy \"C:/Game/Paks\" output --filter SK_1014 SK_1057 SK_1036");
            Console.Error.WriteLine("  extract_iostore_legacy \"C:/Game/Paks\" output --filter Characters/1014 Characters/1057");
            return 1;
        }

        string paksPath = args[1];
        string outputDir = args[2];
        string? scriptObjectsPath = null;
        string? globalUtocPath = null;
        List<string> additionalContainers = new();
        List<string> filterPatterns = new();
        bool extractDependencies = false;

        for (int i = 3; i < args.Length; i++)
        {
            if (args[i] == "--script-objects" && i + 1 < args.Length)
                scriptObjectsPath = args[++i];
            else if (args[i] == "--global" && i + 1 < args.Length)
                globalUtocPath = args[++i];
            else if (args[i] == "--container" && i + 1 < args.Length)
                additionalContainers.Add(args[++i]);
            else if (args[i] == "--filter" || args[i] == "--package")
            {
                // Collect all following args until next option (starts with --)
                while (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                {
                    filterPatterns.Add(args[++i]);
                }
            }
            else if (args[i] == "-deps" || args[i] == "--with-deps")
                extractDependencies = true;
        }

        // Validate paks path
        if (!Directory.Exists(paksPath))
        {
            Console.Error.WriteLine($"Paks directory not found: {paksPath}");
            return 1;
        }
        
        try
        {
            // Create package context for proper import resolution
            using var context = new ZenPackage.FZenPackageContext();
            
            // Set Marvel Rivals AES key for encrypted containers
            context.SetAesKey("0C263D8C22DCB085894899C3A3796383E9BF9DE0CBFB08C9BF2DEF2E84F29D74");
            
            Console.WriteLine($"Loading game containers from: {paksPath}");
            
            // Load global.utoc first for script objects
            string globalPath = Path.Combine(paksPath, "global.utoc");
            if (File.Exists(globalPath))
            {
                Console.WriteLine($"  Loading global.utoc...");
                context.LoadContainer(globalPath);
                context.LoadScriptObjectsFromContainer(0);
            }
            
            // Load other game containers (only top-level, not subfolders)
            // Include optional chunks when extracting dependencies
            var utocFiles = Directory.GetFiles(paksPath, "*.utoc", SearchOption.TopDirectoryOnly)
                .Where(f => !f.EndsWith("global.utoc", StringComparison.OrdinalIgnoreCase))
                .Where(f => extractDependencies || !f.Contains("optional", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f)
                .ToList();
            
            Console.WriteLine($"  Loading {utocFiles.Count} game containers...");
            foreach (var utocFile in utocFiles)
            {
                try
                {
                    context.LoadContainer(utocFile);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  Warning: Failed to load {Path.GetFileName(utocFile)}: {ex.Message}");
                }
            }
            
            // Load global container first (for script objects) - explicit path overrides game path
            if (!string.IsNullOrEmpty(globalUtocPath) && File.Exists(globalUtocPath))
            {
                Console.WriteLine($"Loading global container: {globalUtocPath}");
                context.LoadContainer(globalUtocPath);
                context.LoadScriptObjectsFromContainer(context.ContainerCount - 1);
            }
            
            // Load script objects from file if provided
            if (!string.IsNullOrEmpty(scriptObjectsPath) && File.Exists(scriptObjectsPath))
            {
                Console.WriteLine($"Loading ScriptObjects from: {scriptObjectsPath}");
                context.LoadScriptObjects(scriptObjectsPath);
            }
            
            // Load additional containers for cross-package imports
            foreach (var containerPath in additionalContainers)
            {
                if (File.Exists(containerPath))
                {
                    Console.WriteLine($"Loading additional container: {containerPath}");
                    context.LoadContainer(containerPath);
                }
            }
            
            Console.WriteLine($"Total containers loaded: {context.ContainerCount}");
            Console.WriteLine($"Total packages indexed: {context.PackageCount}");

            Directory.CreateDirectory(outputDir);

            int converted = 0;
            int failed = 0;
            int skipped = 0;
            
            // Track extracted packages to avoid duplicates
            HashSet<ulong> extractedPackages = new();
            HashSet<ulong> pendingDependencies = new();
            
            // Get package IDs to extract
            List<ulong> packageIds;
            bool skipFilterCheck = false; // Skip filter check in ExtractPackage if we already filtered
            if (filterPatterns.Count > 0)
            {
                packageIds = new List<ulong>();
                
                foreach (var filterPattern in filterPatterns)
                {
                    // Check if filter looks like an exact package path (starts with /Game/)
                    if (filterPattern.StartsWith("/Game/"))
                    {
                        // Direct lookup by package path - much faster than iterating all packages
                        ulong packageId = ZenPackage.FPackageId.FromName(filterPattern);
                        if (context.HasPackage(packageId))
                        {
                            if (!packageIds.Contains(packageId))
                                packageIds.Add(packageId);
                            Console.WriteLine($"Direct lookup: found package {filterPattern}");
                        }
                        else
                        {
                            // Try partial match as fallback
                            var found = context.FindPackageIdByPath(filterPattern);
                            if (found.HasValue && !packageIds.Contains(found.Value))
                            {
                                packageIds.Add(found.Value);
                                Console.WriteLine($"Found package by partial match: {context.GetPackagePath(found.Value)}");
                            }
                            else if (!found.HasValue)
                            {
                                Console.Error.WriteLine($"Warning: Package not found: {filterPattern}");
                            }
                        }
                    }
                    else
                    {
                        // Partial filter - search through all packages (slower)
                        int matchCount = 0;
                        foreach (var pkgId in context.GetAllPackageIds())
                        {
                            string? path = context.GetPackagePath(pkgId);
                            if (!string.IsNullOrEmpty(path) && path.Contains(filterPattern, StringComparison.OrdinalIgnoreCase))
                            {
                                if (!packageIds.Contains(pkgId))
                                {
                                    packageIds.Add(pkgId);
                                    matchCount++;
                                }
                            }
                        }
                        Console.WriteLine($"Filter '{filterPattern}' matched {matchCount} packages");
                    }
                }
                
                skipFilterCheck = true; // Already filtered
                Console.WriteLine($"Total packages matching filters [{string.Join(", ", filterPatterns)}]: {packageIds.Count}");
            }
            else
            {
                // No filter specified - require at least one filter to avoid extracting entire game
                Console.Error.WriteLine("Error: No filter specified. Use --filter to specify which packages to extract.");
                Console.Error.WriteLine("Example: --filter SK_1014 SK_1057");
                return 1;
            }

            // Helper function to extract a single package
            List<ulong> ExtractPackage(ulong packageId, bool isDependency)
            {
                List<ulong> imports = new();
                
                if (extractedPackages.Contains(packageId))
                    return imports;
                
                // Get full package path from TOC directory index if available
                string? fullPath = context.GetPackagePath(packageId);
                
                // Apply filter only for primary packages (not dependencies) and only if not already filtered
                if (!isDependency && !skipFilterCheck && filterPatterns.Count > 0)
                {
                    bool matchesAnyFilter = filterPatterns.Any(filter => 
                        !string.IsNullOrEmpty(fullPath) && fullPath.Contains(filter, StringComparison.OrdinalIgnoreCase));
                    if (!matchesAnyFilter)
                    {
                        return imports; // Don't count as skipped, just not matching filter
                    }
                }
                
                var cached = context.GetCachedPackage(packageId);
                if (cached == null)
                {
                    string skipMsg = !string.IsNullOrEmpty(fullPath) ? fullPath : $"package ID {packageId:X16}";
                    if (isDependency || Environment.GetEnvironmentVariable("DEBUG") == "1")
                        Console.WriteLine($"  Skipped (not found): {skipMsg}");
                    skipped++;
                    return imports;
                }
                
                string packageName = !string.IsNullOrEmpty(fullPath) ? fullPath : cached.Header.PackageName();

                try
                {
                    string prefix = isDependency ? "[DEP] " : "";
                    if (Environment.GetEnvironmentVariable("DEBUG") == "1")
                        Console.WriteLine($"  {prefix}Converting: {packageName} ({cached.RawData.Length} bytes)");

                    // Convert to legacy format using proper Rust-ported converter
                    var converter = new ZenPackage.ZenToLegacyConverter(context, packageId);
                    if (Environment.GetEnvironmentVariable("DEBUG") == "1")
                        converter.SetDebugMode(true);
                    var legacyBundle = converter.Convert();

                    // Collect import package IDs for dependency extraction
                    if (extractDependencies)
                    {
                        // Show what imports we found for debugging
                        if (Environment.GetEnvironmentVariable("DEBUG") == "1")
                        {
                            Console.WriteLine($"    Imports for {packageName}:");
                            foreach (var (id, name) in converter.GetImportedPackageInfo())
                            {
                                Console.WriteLine($"      {id:X16} = {name}");
                            }
                        }
                        imports.AddRange(converter.GetImportedPackageIds());
                    }

                    // Write output files - use the package name (from TOC or fallback)
                    // Normalize path (handle /../ patterns)
                    string relPath = packageName;
                    // Remove leading /Game/Marvel/../../../ patterns to get clean path
                    int contentIdx = relPath.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
                    if (contentIdx > 0)
                        relPath = relPath.Substring(contentIdx + 1); // Keep "Content/..."
                    else if (relPath.StartsWith("/Game/"))
                        relPath = relPath.Substring(6); // Remove "/Game/"
                    relPath = relPath.Replace('/', Path.DirectorySeparatorChar);
                    if (relPath.StartsWith(Path.DirectorySeparatorChar))
                        relPath = relPath.Substring(1);
                    if (!relPath.EndsWith(".uasset"))
                        relPath += ".uasset";

                    string outputAssetPath = Path.Combine(outputDir, relPath);
                    string? outputAssetDir = Path.GetDirectoryName(outputAssetPath);
                    if (!string.IsNullOrEmpty(outputAssetDir))
                        Directory.CreateDirectory(outputAssetDir);

                    // Write .uasset
                    File.WriteAllBytes(outputAssetPath, legacyBundle.AssetData);

                    // Write .uexp
                    string outputUexpPath = Path.ChangeExtension(outputAssetPath, ".uexp");
                    File.WriteAllBytes(outputUexpPath, legacyBundle.ExportsData);

                    // Write bulk data files if present
                    if (legacyBundle.BulkData != null && legacyBundle.BulkData.Length > 0)
                    {
                        string outputBulkPath = Path.ChangeExtension(outputAssetPath, ".ubulk");
                        File.WriteAllBytes(outputBulkPath, legacyBundle.BulkData);
                    }

                    extractedPackages.Add(packageId);
                    converted++;
                    Console.WriteLine($"{prefix}Converted: {packageName}");
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.Error.WriteLine($"Failed to convert {packageName}: {ex.Message}");
                    if (Environment.GetEnvironmentVariable("DEBUG") == "1")
                        Console.Error.WriteLine(ex.StackTrace);
                }
                
                return imports;
            }

            // Process primary packages
            foreach (var packageId in packageIds)
            {
                var imports = ExtractPackage(packageId, isDependency: false);
                foreach (var importId in imports)
                {
                    if (!extractedPackages.Contains(importId))
                        pendingDependencies.Add(importId);
                }
            }

            // Process dependencies if enabled
            if (extractDependencies && pendingDependencies.Count > 0)
            {
                Console.WriteLine($"\nExtracting {pendingDependencies.Count} dependencies...");
                
                // Process dependencies iteratively (could go multiple levels deep)
                while (pendingDependencies.Count > 0)
                {
                    var currentBatch = pendingDependencies.ToList();
                    pendingDependencies.Clear();
                    
                    foreach (var depId in currentBatch)
                    {
                        var newImports = ExtractPackage(depId, isDependency: true);
                        foreach (var importId in newImports)
                        {
                            if (!extractedPackages.Contains(importId))
                                pendingDependencies.Add(importId);
                        }
                    }
                }
            }

            Console.WriteLine($"\nExtraction complete: {converted} converted, {failed} failed, {skipped} skipped");
            return failed > 0 && converted == 0 ? 1 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (Environment.GetEnvironmentVariable("DEBUG") == "1")
                Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    private static int CliDetect(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: UAssetTool detect <uasset_path> [usmap_path]");
            return 1;
        }

        string uassetPath = args[1];
        string? usmapPath = args.Length > 2 ? args[2] : null;

        if (!File.Exists(uassetPath))
        {
            Console.Error.WriteLine($"File not found: {uassetPath}");
            return 1;
        }

        var asset = LoadAsset(uassetPath, usmapPath);
        var assetType = DetectAssetType(asset);
        
        var result = new
        {
            path = uassetPath,
            asset_type = assetType,
            export_count = asset.Exports.Count,
            import_count = asset.Imports.Count
        };

        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    
    private static int CliFix(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: UAssetTool fix <uasset_path> [usmap_path]");
            return 1;
        }

        string uassetPath = args[1];
        string? usmapPath = args.Length > 2 ? args[2] : null;

        if (!File.Exists(uassetPath))
        {
            Console.Error.WriteLine($"File not found: {uassetPath}");
            return 1;
        }

        var result = FixSerializeSize(uassetPath, usmapPath);
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    
    private static int CliBatchDetect(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: UAssetTool batch_detect <directory> [usmap_path]");
            return 1;
        }

        string directory = args[1];
        string? usmapPath = args.Length > 2 ? args[2] : null;

        if (!Directory.Exists(directory))
        {
            Console.Error.WriteLine($"Directory not found: {directory}");
            return 1;
        }

        var results = new List<object>();
        var uassetFiles = Directory.GetFiles(directory, "*.uasset", SearchOption.AllDirectories);

        Console.Error.WriteLine($"Scanning {uassetFiles.Length} .uasset files...");

        Usmap? mappings = LoadMappings(usmapPath);

        foreach (var uassetPath in uassetFiles)
        {
            try
            {
                var asset = LoadAssetWithMappings(uassetPath, mappings);
                string assetType = DetectAssetType(asset);

                results.Add(new
                {
                    path = uassetPath,
                    asset_type = assetType,
                    file_name = Path.GetFileName(uassetPath)
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to process {uassetPath}: {ex.Message}");
            }
        }

        var grouped = results.GroupBy(r => ((dynamic)r).asset_type)
                            .Select(g => new
                            {
                                asset_type = g.Key,
                                count = g.Count(),
                                files = g.ToList()
                            })
                            .ToList();

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            total_files = uassetFiles.Length,
            by_type = grouped
        }, new JsonSerializerOptions { WriteIndented = true }));

        return 0;
    }
    
    private static int CliDump(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: UAssetTool dump <uasset_path> <usmap_path>");
            return 1;
        }

        string uassetPath = args[1];
        string usmapPath = args[2];

        if (!File.Exists(uassetPath))
        {
            Console.Error.WriteLine($"File not found: {uassetPath}");
            return 1;
        }

        var asset = LoadAsset(uassetPath, usmapPath);
        DumpAssetInfo(asset, uassetPath);
        return 0;
    }
    
    private static int CliFromJson(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: UAssetTool from_json <json_path> <output_uasset_path> [usmap_path]");
            return 1;
        }

        string jsonPath = args[1];
        string outputPath = args[2];
        string? usmapPath = args.Length > 3 ? args[3] : null;

        if (!File.Exists(jsonPath))
        {
            Console.Error.WriteLine($"JSON file not found: {jsonPath}");
            return 1;
        }

        // Read JSON with UTF-8 encoding to preserve Unicode characters
        string jsonData = File.ReadAllText(jsonPath, System.Text.Encoding.UTF8);
        
        // Load mappings if provided
        Usmap? mappings = null;
        if (!string.IsNullOrEmpty(usmapPath) && File.Exists(usmapPath))
            mappings = new Usmap(usmapPath);
        
        // Deserialize from JSON
        var asset = UAsset.DeserializeJson(jsonData);
        if (asset == null)
        {
            Console.Error.WriteLine("Failed to deserialize JSON");
            return 1;
        }
        
        asset.Mappings = mappings;
        
        // Ensure output directory exists
        string? outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);
        
        asset.Write(outputPath);
        
        Console.WriteLine($"Asset imported from JSON and saved to {outputPath}");
        return 0;
    }
    
    private static int CliModifyColors(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: UAssetTool modify_colors <directory_or_file> <usmap_path> [r g b a]");
            Console.Error.WriteLine("Default color: bright green (0, 10, 0, 1)");
            return 1;
        }

        string path = args[1];
        string usmapPath = args[2];
        
        // Default to bright green (HDR value for visibility)
        float r = 0f, g = 10f, b = 0f, a = 1f;
        if (args.Length >= 6)
        {
            float.TryParse(args[3], out r);
            float.TryParse(args[4], out g);
            float.TryParse(args[5], out b);
        }
        if (args.Length >= 7)
        {
            float.TryParse(args[6], out a);
        }

        Console.WriteLine($"Modifying colors to R={r}, G={g}, B={b}, A={a}");

        int totalModified = 0;
        
        if (Directory.Exists(path))
        {
            var files = Directory.GetFiles(path, "*.uasset", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                int count = ColorModifier.ModifyColors(file, usmapPath, r, g, b, a);
                if (count > 0) totalModified += count;
            }
        }
        else if (File.Exists(path))
        {
            totalModified = ColorModifier.ModifyColors(path, usmapPath, r, g, b, a);
        }
        else
        {
            Console.Error.WriteLine($"Path not found: {path}");
            return 1;
        }

        Console.WriteLine($"Total color values modified: {totalModified}");
        return 0;
    }
    
    private static int CliNiagaraList(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: UAssetTool niagara_list <directory> [usmap_path]");
            Console.Error.WriteLine("Output: JSON with list of NS files and their metadata");
            return 1;
        }

        string directory = args[1];
        string? usmapPath = args.Length > 2 ? args[2] : null;

        string json = NiagaraService.ListNiagaraFiles(directory, usmapPath);
        Console.WriteLine(json);
        return 0;
    }

    private static int CliNiagaraDetails(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: UAssetTool niagara_details <asset_path> [usmap_path]");
            Console.Error.WriteLine("Output: JSON with detailed color curve info for a specific NS file");
            return 1;
        }

        string assetPath = args[1];
        string? usmapPath = args.Length > 2 ? args[2] : null;

        string json = NiagaraService.GetNiagaraDetails(assetPath, usmapPath);
        Console.WriteLine(json);
        return 0;
    }

    private static int CliNiagaraEdit(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: UAssetTool niagara_edit <json_request> [usmap_path]");
            Console.Error.WriteLine("       UAssetTool niagara_edit <asset_path> <r> <g> <b> [a] [usmap_path]");
            Console.Error.WriteLine("JSON request format: {\"assetPath\":\"...\",\"r\":0,\"g\":10,\"b\":0,\"a\":1}");
            Console.Error.WriteLine("Optional: exportIndex, colorIndex to target specific colors");
            return 1;
        }

        // Check if first arg is JSON or a file path
        string firstArg = args[1];
        string? usmapPath = null;

        if (firstArg.TrimStart().StartsWith("{"))
        {
            // JSON request mode
            usmapPath = args.Length > 2 ? args[2] : null;
            string json = NiagaraService.EditNiagaraColors(firstArg, usmapPath);
            Console.WriteLine(json);
            return 0;
        }
        else
        {
            // Simple mode: asset_path r g b [a] [usmap]
            if (args.Length < 5)
            {
                Console.Error.WriteLine("Usage: UAssetTool niagara_edit <asset_path> <r> <g> <b> [a] [usmap_path]");
                return 1;
            }

            string assetPath = args[1];
            if (!float.TryParse(args[2], out float r) ||
                !float.TryParse(args[3], out float g) ||
                !float.TryParse(args[4], out float b))
            {
                Console.Error.WriteLine("Error: Invalid color values");
                return 1;
            }

            float a = 1.0f;
            int nextArg = 5;
            if (args.Length > 5 && float.TryParse(args[5], out float parsedA))
            {
                a = parsedA;
                nextArg = 6;
            }

            usmapPath = args.Length > nextArg ? args[nextArg] : null;

            var request = new NiagaraService.ColorEditRequest
            {
                AssetPath = assetPath,
                R = r,
                G = g,
                B = b,
                A = a
            };

            string json = NiagaraService.EditNiagaraColors(request, usmapPath);
            Console.WriteLine(json);
            return 0;
        }
    }
    
    #endregion

    #region Interactive JSON Mode
    
    private static async Task<int> RunInteractiveMode()
    {
        try
        {
            // Use StreamReader with explicit UTF-8 encoding to properly handle Unicode characters
            // This is necessary because Console.In may not properly decode UTF-8 from piped input
            using var reader = new StreamReader(Console.OpenStandardInput(), System.Text.Encoding.UTF8);
            
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try 
                {
                    var request = JsonSerializer.Deserialize<UAssetRequest>(line);
                    if (request == null)
                    {
                        WriteJsonResponse(false, "Invalid JSON request");
                        continue;
                    }

                    var response = ProcessRequest(request);
                    var responseJson = JsonSerializer.Serialize(response);
                    Console.WriteLine(responseJson.Replace("\r", "").Replace("\n", ""));
                }
                catch (JsonException)
                {
                    WriteJsonResponse(false, "Invalid JSON format");
                }
            }
        }
        catch (Exception ex)
        {
            WriteJsonResponse(false, $"Unhandled exception: {ex.Message}");
        }
        
        return 0;
    }
    
    private static UAssetResponse ProcessRequest(UAssetRequest request)
    {
        try
        {
            return request.Action switch
            {
                // Single file detection - all use unified DetectAssetType
                "detect_texture" => DetectSingleAsset(request.FilePath, request.UsmapPath, "texture"),
                "detect_mesh" => DetectSingleAsset(request.FilePath, request.UsmapPath, "skeletal_mesh"),
                "detect_skeletal_mesh" => DetectSingleAsset(request.FilePath, request.UsmapPath, "skeletal_mesh"),
                "detect_static_mesh" => DetectSingleAsset(request.FilePath, request.UsmapPath, "static_mesh"),
                "detect_blueprint" => DetectSingleAsset(request.FilePath, request.UsmapPath, "blueprint"),
                
                // Batch detection - all use unified workflow
                "batch_detect_skeletal_mesh" => BatchDetectAssetType(request.FilePaths, request.UsmapPath, "skeletal_mesh"),
                "batch_detect_static_mesh" => BatchDetectAssetType(request.FilePaths, request.UsmapPath, "static_mesh"),
                "batch_detect_texture" => BatchDetectAssetType(request.FilePaths, request.UsmapPath, "texture"),
                "batch_detect_blueprint" => BatchDetectAssetType(request.FilePaths, request.UsmapPath, "blueprint"),
                
                // Texture operations
                "get_texture_info" => GetTextureInfo(request.FilePath, request.UsmapPath),
                "strip_mipmaps_native" => StripMipmapsNative(request.FilePath, request.UsmapPath),
                "batch_strip_mipmaps_native" => BatchStripMipmapsNative(request.FilePaths, request.UsmapPath),
                "has_inline_texture_data" => HasInlineTextureData(request.FilePath, request.UsmapPath),
                "batch_has_inline_texture_data" => BatchHasInlineTextureData(request.FilePaths, request.UsmapPath),
                
                // Mesh operations
                "patch_mesh" => PatchMesh(request.FilePath, request.UexpPath),
                "get_mesh_info" => GetMeshInfo(request.FilePath, request.UsmapPath),
                "fix_serialize_size" => FixSerializeSizeJson(request.FilePath, request.UsmapPath),
                
                // Zen conversion operations
                "convert_to_zen" => ConvertToZen(request.FilePath, request.UsmapPath),
                "convert_from_zen" => ConvertFromZen(request.FilePath, request.UsmapPath),
                
                // GUI Backend - Asset Structure
                "get_asset_summary" => GetAssetSummary(request.FilePath, request.UsmapPath),
                "get_name_map" => GetNameMap(request.FilePath, request.UsmapPath),
                "get_imports" => GetImports(request.FilePath, request.UsmapPath),
                "get_exports" => GetExports(request.FilePath, request.UsmapPath),
                "get_export_properties" => GetExportProperties(request.FilePath, request.UsmapPath, request.ExportIndex),
                "get_export_raw_data" => GetExportRawData(request.FilePath, request.UsmapPath, request.ExportIndex),
                
                // GUI Backend - Property Editing
                "set_property_value" => SetPropertyValue(request.FilePath, request.UsmapPath, request.ExportIndex, request.PropertyPath, request.PropertyValue),
                "add_property" => AddProperty(request.FilePath, request.UsmapPath, request.ExportIndex, request.PropertyName, request.PropertyType, request.PropertyValue),
                "remove_property" => RemoveProperty(request.FilePath, request.UsmapPath, request.ExportIndex, request.PropertyPath),
                
                // GUI Backend - Save/Export
                "save_asset" => SaveAsset(request.FilePath, request.UsmapPath, request.OutputPath),
                "export_to_json" => ExportToJson(request.FilePath, request.UsmapPath),
                "import_from_json" => ImportFromJson(request.FilePath, request.UsmapPath, request.JsonData),
                
                // Debug
                "debug_asset_info" => DebugAssetInfo(request.FilePath),
                
                _ => new UAssetResponse { Success = false, Message = $"Unknown action: {request.Action}" }
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Error: {ex.Message}" };
        }
    }
    
    #endregion
    
    #region GUI Backend Methods
    
    /// <summary>
    /// Get a summary of the asset including header info, counts, and detected type
    /// </summary>
    private static UAssetResponse GetAssetSummary(string? filePath, string? usmapPath)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path is required" };
        
        var asset = LoadAsset(filePath, usmapPath);
        if (asset == null)
            return new UAssetResponse { Success = false, Message = "Failed to load asset" };
        
        var summary = new Dictionary<string, object?>
        {
            ["file_path"] = filePath,
            ["file_name"] = Path.GetFileName(filePath),
            ["detected_type"] = DetectAssetType(asset),
            ["name_count"] = asset.GetNameMapIndexList().Count,
            ["import_count"] = asset.Imports?.Count ?? 0,
            ["export_count"] = asset.Exports?.Count ?? 0,
            ["has_unversioned_properties"] = asset.HasUnversionedProperties,
            ["package_flags"] = asset.PackageFlags.ToString(),
            ["file_version_ue4"] = asset.ObjectVersion.ToString(),
            ["file_version_ue5"] = asset.ObjectVersionUE5.ToString(),
            ["uses_event_driven_loader"] = asset.UsesEventDrivenLoader,
            ["package_guid"] = asset.PackageGuid.ToString()
        };
        
        return new UAssetResponse { Success = true, Message = "Asset summary retrieved", Data = summary };
    }
    
    /// <summary>
    /// Get the name map (list of all FNames in the asset)
    /// </summary>
    private static UAssetResponse GetNameMap(string? filePath, string? usmapPath)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path is required" };
        
        var asset = LoadAsset(filePath, usmapPath);
        if (asset == null)
            return new UAssetResponse { Success = false, Message = "Failed to load asset" };
        
        var names = new List<Dictionary<string, object>>();
        var nameMap = asset.GetNameMapIndexList();
        for (int i = 0; i < nameMap.Count; i++)
        {
            names.Add(new Dictionary<string, object>
            {
                ["index"] = i,
                ["value"] = nameMap[i]
            });
        }
        
        return new UAssetResponse { Success = true, Message = $"Retrieved {names.Count} names", Data = names };
    }
    
    /// <summary>
    /// Get all imports with their details
    /// </summary>
    private static UAssetResponse GetImports(string? filePath, string? usmapPath)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path is required" };
        
        var asset = LoadAsset(filePath, usmapPath);
        if (asset == null)
            return new UAssetResponse { Success = false, Message = "Failed to load asset" };
        
        var imports = new List<Dictionary<string, object?>>();
        if (asset.Imports != null)
        {
            for (int i = 0; i < asset.Imports.Count; i++)
            {
                var imp = asset.Imports[i];
                imports.Add(new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["class_package"] = imp.ClassPackage?.ToString(),
                    ["class_name"] = imp.ClassName?.ToString(),
                    ["object_name"] = imp.ObjectName?.ToString(),
                    ["outer_index"] = imp.OuterIndex.Index,
                    ["is_optional"] = imp.bImportOptional
                });
            }
        }
        
        return new UAssetResponse { Success = true, Message = $"Retrieved {imports.Count} imports", Data = imports };
    }
    
    /// <summary>
    /// Get all exports with their details
    /// </summary>
    private static UAssetResponse GetExports(string? filePath, string? usmapPath)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path is required" };
        
        var asset = LoadAsset(filePath, usmapPath);
        if (asset == null)
            return new UAssetResponse { Success = false, Message = "Failed to load asset" };
        
        var exports = new List<Dictionary<string, object?>>();
        for (int i = 0; i < asset.Exports.Count; i++)
        {
            var exp = asset.Exports[i];
            string className = GetExportClassName(asset, exp);
            
            exports.Add(new Dictionary<string, object?>
            {
                ["index"] = i,
                ["object_name"] = exp.ObjectName?.ToString(),
                ["class_name"] = className,
                ["class_index"] = exp.ClassIndex.Index,
                ["super_index"] = exp.SuperIndex.Index,
                ["outer_index"] = exp.OuterIndex.Index,
                ["serial_size"] = exp.SerialSize,
                ["serial_offset"] = exp.SerialOffset,
                ["object_flags"] = exp.ObjectFlags.ToString(),
                ["export_type"] = exp.GetType().Name,
                ["property_count"] = (exp is NormalExport ne) ? ne.Data?.Count ?? 0 : 0
            });
        }
        
        return new UAssetResponse { Success = true, Message = $"Retrieved {exports.Count} exports", Data = exports };
    }
    
    /// <summary>
    /// Get properties of a specific export
    /// </summary>
    private static UAssetResponse GetExportProperties(string? filePath, string? usmapPath, int exportIndex)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path is required" };
        if (exportIndex < 0)
            return new UAssetResponse { Success = false, Message = "Export index is required" };
        
        var asset = LoadAsset(filePath, usmapPath);
        if (asset == null)
            return new UAssetResponse { Success = false, Message = "Failed to load asset" };
        
        if (exportIndex >= asset.Exports.Count)
            return new UAssetResponse { Success = false, Message = $"Export index {exportIndex} out of range (max: {asset.Exports.Count - 1})" };
        
        var export = asset.Exports[exportIndex];
        var properties = new List<Dictionary<string, object?>>();
        
        if (export is NormalExport normalExport && normalExport.Data != null)
        {
            foreach (var prop in normalExport.Data)
            {
                properties.Add(SerializeProperty(prop, 0));
            }
        }
        
        return new UAssetResponse { Success = true, Message = $"Retrieved {properties.Count} properties", Data = properties };
    }
    
    /// <summary>
    /// Serialize a property to a dictionary for JSON output
    /// </summary>
    private static Dictionary<string, object?> SerializeProperty(PropertyData prop, int depth)
    {
        var result = new Dictionary<string, object?>
        {
            ["name"] = prop.Name?.ToString(),
            ["type"] = prop.PropertyType.ToString(),
            ["array_index"] = prop.ArrayIndex
        };
        
        // Add value based on property type
        if (prop is IntPropertyData intProp)
            result["value"] = intProp.Value;
        else if (prop is FloatPropertyData floatProp)
            result["value"] = floatProp.Value;
        else if (prop is BoolPropertyData boolProp)
            result["value"] = boolProp.Value;
        else if (prop is StrPropertyData strProp)
            result["value"] = strProp.Value?.ToString();
        else if (prop is NamePropertyData nameProp)
            result["value"] = nameProp.Value?.ToString();
        else if (prop is ObjectPropertyData objProp)
            result["value"] = objProp.Value.Index;
        else if (prop is SoftObjectPropertyData softObjProp)
            result["value"] = softObjProp.Value.AssetPath.ToString();
        else if (prop is EnumPropertyData enumProp)
            result["value"] = enumProp.Value?.ToString();
        else if (prop is BytePropertyData byteProp)
            result["value"] = byteProp.ByteType == BytePropertyType.Byte ? byteProp.Value : byteProp.EnumValue?.ToString();
        else if (prop is ArrayPropertyData arrayProp)
        {
            var items = new List<object?>();
            if (arrayProp.Value != null && depth < 3)
            {
                foreach (var item in arrayProp.Value)
                {
                    items.Add(SerializeProperty(item, depth + 1));
                }
            }
            result["value"] = items;
            result["array_type"] = arrayProp.ArrayType?.ToString();
        }
        else if (prop is StructPropertyData structProp)
        {
            var structItems = new List<object?>();
            if (structProp.Value != null && depth < 3)
            {
                foreach (var item in structProp.Value)
                {
                    structItems.Add(SerializeProperty(item, depth + 1));
                }
            }
            result["value"] = structItems;
            result["struct_type"] = structProp.StructType?.ToString();
        }
        else if (prop is MapPropertyData mapProp)
        {
            result["value"] = $"[Map with {mapProp.Value?.Count ?? 0} entries]";
            result["key_type"] = mapProp.KeyType?.ToString();
            result["value_type"] = mapProp.ValueType?.ToString();
        }
        else
        {
            result["value"] = prop.ToString();
        }
        
        return result;
    }
    
    /// <summary>
    /// Get raw binary data of an export (hex encoded)
    /// </summary>
    private static UAssetResponse GetExportRawData(string? filePath, string? usmapPath, int exportIndex)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path is required" };
        if (exportIndex < 0)
            return new UAssetResponse { Success = false, Message = "Export index is required" };
        
        var asset = LoadAsset(filePath, usmapPath);
        if (asset == null)
            return new UAssetResponse { Success = false, Message = "Failed to load asset" };
        
        if (exportIndex >= asset.Exports.Count)
            return new UAssetResponse { Success = false, Message = $"Export index {exportIndex} out of range" };
        
        var export = asset.Exports[exportIndex];
        
        // Get the raw extras data if available
        byte[]? rawData = null;
        if (export is RawExport rawExport)
            rawData = rawExport.Data;
        else if (export is NormalExport normalExport)
            rawData = normalExport.Extras;
        
        var data = new Dictionary<string, object?>
        {
            ["export_index"] = exportIndex,
            ["serial_size"] = export.SerialSize,
            ["serial_offset"] = export.SerialOffset,
            ["has_raw_data"] = rawData != null && rawData.Length > 0,
            ["raw_data_size"] = rawData?.Length ?? 0,
            ["raw_data_hex"] = rawData != null ? Convert.ToBase64String(rawData) : null
        };
        
        return new UAssetResponse { Success = true, Message = "Raw data retrieved", Data = data };
    }
    
    /// <summary>
    /// Set a property value in an export
    /// </summary>
    private static UAssetResponse SetPropertyValue(string? filePath, string? usmapPath, int exportIndex, string? propertyPath, JsonElement? propertyValue)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path is required" };
        if (exportIndex < 0)
            return new UAssetResponse { Success = false, Message = "Export index is required" };
        if (string.IsNullOrEmpty(propertyPath))
            return new UAssetResponse { Success = false, Message = "Property path is required" };
        if (propertyValue == null)
            return new UAssetResponse { Success = false, Message = "Property value is required" };
        
        var asset = LoadAsset(filePath, usmapPath);
        if (asset == null)
            return new UAssetResponse { Success = false, Message = "Failed to load asset" };
        
        if (exportIndex >= asset.Exports.Count)
            return new UAssetResponse { Success = false, Message = $"Export index {exportIndex} out of range" };
        
        var export = asset.Exports[exportIndex];
        if (export is not NormalExport normalExport || normalExport.Data == null)
            return new UAssetResponse { Success = false, Message = "Export has no editable properties" };
        
        // Find the property by path (supports nested paths like "Property.SubProperty")
        var pathParts = propertyPath.Split('.');
        PropertyData? targetProp = null;
        IList<PropertyData>? parentList = normalExport.Data;
        
        for (int i = 0; i < pathParts.Length; i++)
        {
            string partName = pathParts[i];
            targetProp = parentList?.FirstOrDefault(p => p.Name?.ToString() == partName);
            
            if (targetProp == null)
                return new UAssetResponse { Success = false, Message = $"Property not found: {partName}" };
            
            if (i < pathParts.Length - 1)
            {
                // Navigate into struct
                if (targetProp is StructPropertyData structProp)
                    parentList = structProp.Value;
                else
                    return new UAssetResponse { Success = false, Message = $"Cannot navigate into non-struct property: {partName}" };
            }
        }
        
        if (targetProp == null)
            return new UAssetResponse { Success = false, Message = "Property not found" };
        
        // Set the value based on property type
        try
        {
            if (targetProp is IntPropertyData intProp)
                intProp.Value = propertyValue.Value.GetInt32();
            else if (targetProp is FloatPropertyData floatProp)
                floatProp.Value = propertyValue.Value.GetSingle();
            else if (targetProp is BoolPropertyData boolProp)
                boolProp.Value = propertyValue.Value.GetBoolean();
            else if (targetProp is StrPropertyData strProp)
                strProp.Value = FString.FromString(propertyValue.Value.GetString() ?? "");
            else if (targetProp is NamePropertyData nameProp)
                nameProp.Value = FName.FromString(asset, propertyValue.Value.GetString() ?? "");
            else
                return new UAssetResponse { Success = false, Message = $"Unsupported property type for editing: {targetProp.PropertyType}" };
            
            // Save the asset
            asset.Write(filePath);
            
            return new UAssetResponse { Success = true, Message = $"Property '{propertyPath}' updated successfully" };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Failed to set property value: {ex.Message}" };
        }
    }
    
    /// <summary>
    /// Add a new property to an export
    /// </summary>
    private static UAssetResponse AddProperty(string? filePath, string? usmapPath, int exportIndex, string? propertyName, string? propertyType, JsonElement? propertyValue)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path is required" };
        if (exportIndex < 0)
            return new UAssetResponse { Success = false, Message = "Export index is required" };
        if (string.IsNullOrEmpty(propertyName))
            return new UAssetResponse { Success = false, Message = "Property name is required" };
        if (string.IsNullOrEmpty(propertyType))
            return new UAssetResponse { Success = false, Message = "Property type is required" };
        
        var asset = LoadAsset(filePath, usmapPath);
        if (asset == null)
            return new UAssetResponse { Success = false, Message = "Failed to load asset" };
        
        if (exportIndex >= asset.Exports.Count)
            return new UAssetResponse { Success = false, Message = $"Export index {exportIndex} out of range" };
        
        var export = asset.Exports[exportIndex];
        if (export is not NormalExport normalExport)
            return new UAssetResponse { Success = false, Message = "Export does not support properties" };
        
        normalExport.Data ??= new List<PropertyData>();
        
        // Create the property based on type
        PropertyData? newProp = propertyType.ToLower() switch
        {
            "int" or "intproperty" => new IntPropertyData(FName.FromString(asset, propertyName)) { Value = propertyValue?.GetInt32() ?? 0 },
            "float" or "floatproperty" => new FloatPropertyData(FName.FromString(asset, propertyName)) { Value = propertyValue?.GetSingle() ?? 0f },
            "bool" or "boolproperty" => new BoolPropertyData(FName.FromString(asset, propertyName)) { Value = propertyValue?.GetBoolean() ?? false },
            "str" or "strproperty" => new StrPropertyData(FName.FromString(asset, propertyName)) { Value = FString.FromString(propertyValue?.GetString() ?? "") },
            "name" or "nameproperty" => new NamePropertyData(FName.FromString(asset, propertyName)) { Value = FName.FromString(asset, propertyValue?.GetString() ?? "") },
            _ => null
        };
        
        if (newProp == null)
            return new UAssetResponse { Success = false, Message = $"Unsupported property type: {propertyType}" };
        
        normalExport.Data.Add(newProp);
        asset.Write(filePath);
        
        return new UAssetResponse { Success = true, Message = $"Property '{propertyName}' added successfully" };
    }
    
    /// <summary>
    /// Remove a property from an export
    /// </summary>
    private static UAssetResponse RemoveProperty(string? filePath, string? usmapPath, int exportIndex, string? propertyPath)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path is required" };
        if (exportIndex < 0)
            return new UAssetResponse { Success = false, Message = "Export index is required" };
        if (string.IsNullOrEmpty(propertyPath))
            return new UAssetResponse { Success = false, Message = "Property path is required" };
        
        var asset = LoadAsset(filePath, usmapPath);
        if (asset == null)
            return new UAssetResponse { Success = false, Message = "Failed to load asset" };
        
        if (exportIndex >= asset.Exports.Count)
            return new UAssetResponse { Success = false, Message = $"Export index {exportIndex} out of range" };
        
        var export = asset.Exports[exportIndex];
        if (export is not NormalExport normalExport || normalExport.Data == null)
            return new UAssetResponse { Success = false, Message = "Export has no properties" };
        
        var prop = normalExport.Data.FirstOrDefault(p => p.Name?.ToString() == propertyPath);
        if (prop == null)
            return new UAssetResponse { Success = false, Message = $"Property not found: {propertyPath}" };
        
        normalExport.Data.Remove(prop);
        asset.Write(filePath);
        
        return new UAssetResponse { Success = true, Message = $"Property '{propertyPath}' removed successfully" };
    }
    
    /// <summary>
    /// Save the asset to a new path
    /// </summary>
    private static UAssetResponse SaveAsset(string? filePath, string? usmapPath, string? outputPath)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path is required" };
        
        var asset = LoadAsset(filePath, usmapPath);
        if (asset == null)
            return new UAssetResponse { Success = false, Message = "Failed to load asset" };
        
        string savePath = outputPath ?? filePath;
        asset.Write(savePath);
        
        return new UAssetResponse { Success = true, Message = $"Asset saved to {savePath}" };
    }
    
    /// <summary>
    /// Export the asset to JSON format
    /// </summary>
    private static UAssetResponse ExportToJson(string? filePath, string? usmapPath)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path is required" };
        
        var asset = LoadAsset(filePath, usmapPath);
        if (asset == null)
            return new UAssetResponse { Success = false, Message = "Failed to load asset" };
        
        string jsonPath = Path.ChangeExtension(filePath, ".json");
        string json = asset.SerializeJson();
        File.WriteAllText(jsonPath, json);
        
        return new UAssetResponse { Success = true, Message = $"Asset exported to {jsonPath}", Data = new { path = jsonPath } };
    }
    
    /// <summary>
    /// Import asset data from JSON
    /// </summary>
    private static UAssetResponse ImportFromJson(string? filePath, string? usmapPath, string? jsonData)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path is required" };
        if (string.IsNullOrEmpty(jsonData))
            return new UAssetResponse { Success = false, Message = "JSON data is required" };
        
        try
        {
            // Load mappings if provided
            Usmap? mappings = null;
            if (!string.IsNullOrEmpty(usmapPath) && File.Exists(usmapPath))
                mappings = new Usmap(usmapPath);
            
            // Deserialize from JSON
            var asset = UAsset.DeserializeJson(jsonData);
            if (asset == null)
                return new UAssetResponse { Success = false, Message = "Failed to deserialize JSON" };
            
            asset.Mappings = mappings;
            asset.Write(filePath);
            
            return new UAssetResponse { Success = true, Message = $"Asset imported from JSON and saved to {filePath}" };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Failed to import from JSON: {ex.Message}" };
        }
    }
    
    #endregion

    #region Unified Asset Detection
    
    /// <summary>
    /// Core asset type detection - single unified method for all asset types.
    /// Returns: "static_mesh", "skeletal_mesh", "texture", "material_instance", "blueprint", "other"
    /// </summary>
    private static string DetectAssetType(UAsset asset)
    {
        foreach (var export in asset.Exports)
        {
            string className = GetExportClassName(asset, export);
            
            // Check class name against known types
            if (className.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase))
                return "static_mesh";
            if (className.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase))
                return "skeletal_mesh";
            if (className.Equals("Texture2D", StringComparison.OrdinalIgnoreCase))
                return "texture";
            if (className.Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("MaterialInstance", StringComparison.OrdinalIgnoreCase))
                return "material_instance";
            if (className.Contains("Blueprint", StringComparison.OrdinalIgnoreCase))
                return "blueprint";
            
            // Check export type name (fallback)
            string exportTypeName = export.GetType().Name;
            if (exportTypeName.Contains("StaticMesh", StringComparison.OrdinalIgnoreCase))
                return "static_mesh";
            if (exportTypeName.Contains("SkeletalMesh", StringComparison.OrdinalIgnoreCase))
                return "skeletal_mesh";
            if (exportTypeName.Contains("Texture2D", StringComparison.OrdinalIgnoreCase))
                return "texture";
        }
        
        // No filename heuristics - rely only on actual asset class detection
        return "other";
    }
    
    /// <summary>
    /// Get the class name for an export (from import reference)
    /// </summary>
    private static string GetExportClassName(UAsset asset, Export export)
    {
        if (export.ClassIndex.IsImport())
        {
            var import = export.ClassIndex.ToImport(asset);
            if (import != null)
            {
                return import.ObjectName?.Value?.Value ?? "Unknown";
            }
        }
        return "Unknown";
    }
    
    /// <summary>
    /// Check if asset matches a specific type
    /// </summary>
    private static bool IsAssetType(UAsset asset, string targetType)
    {
        string detectedType = DetectAssetType(asset);
        return detectedType.Equals(targetType, StringComparison.OrdinalIgnoreCase);
    }
    
    /// <summary>
    /// Detect single asset and check if it matches target type
    /// </summary>
    private static UAssetResponse DetectSingleAsset(string? filePath, string? usmapPath, string targetType)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path required" };
        if (!File.Exists(filePath))
            return new UAssetResponse { Success = false, Message = $"File not found: {filePath}" };

        try
        {
            // Use provided usmap_path, fall back to environment variable
            string? effectiveUsmapPath = usmapPath ?? Environment.GetEnvironmentVariable("USMAP_PATH");
            var asset = LoadAsset(filePath, effectiveUsmapPath);
            
            // For textures, also check if it needs MipGen fix
            if (targetType == "texture")
            {
                bool isTexture = IsAssetType(asset, "texture");
                bool needsFix = isTexture && IsTextureNeedingMipGenFix(asset);
                return new UAssetResponse
                {
                    Success = true,
                    Message = needsFix ? "Texture needs MipGen fix" : (isTexture ? "Texture already has NoMipmaps" : "Not a texture"),
                    Data = needsFix
                };
            }
            
            bool isMatch = IsAssetType(asset, targetType);
            return new UAssetResponse
            {
                Success = true,
                Message = isMatch ? $"File is {targetType}" : $"File is not {targetType}",
                Data = isMatch
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Error: {ex.Message}" };
        }
    }
    
    /// <summary>
    /// Batch detect - check multiple files for a specific asset type
    /// </summary>
    private static UAssetResponse BatchDetectAssetType(List<string>? filePaths, string? usmapPath, string targetType)
    {
        if (filePaths == null || filePaths.Count == 0)
            return new UAssetResponse { Success = false, Message = "file_paths required" };

        try
        {
            // Use provided usmap_path, fall back to environment variable
            string? effectiveUsmapPath = usmapPath ?? Environment.GetEnvironmentVariable("USMAP_PATH");
            Usmap? mappings = LoadMappings(effectiveUsmapPath);

            bool foundMatch = filePaths.AsParallel().Any(filePath =>
            {
                if (!File.Exists(filePath)) return false;
                try
                {
                    var asset = LoadAssetWithMappings(filePath, mappings);
                    
                    // For textures, check if it needs MipGen fix
                    if (targetType == "texture")
                    {
                        return IsAssetType(asset, "texture") && IsTextureNeedingMipGenFix(asset);
                    }
                    
                    return IsAssetType(asset, targetType);
                }
                catch
                {
                    return false;
                }
            });

            return new UAssetResponse
            {
                Success = true,
                Message = foundMatch ? $"Found {targetType} in batch" : $"No {targetType} found",
                Data = foundMatch
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Batch detection error: {ex.Message}" };
        }
    }
    
    #endregion

    #region Texture Operations
    
    private static bool IsTextureNeedingMipGenFix(UAsset asset)
    {
        foreach (var export in asset.Exports)
        {
            if (GetExportClassName(asset, export) == "Texture2D" && export is NormalExport normalExport)
            {
                foreach (var property in normalExport.Data)
                {
                    if (property.Name?.Value?.Value == "MipGenSettings")
                    {
                        if (property is EnumPropertyData enumProp)
                        {
                            string value = enumProp.Value?.Value?.Value ?? "";
                            return !value.Equals("TMGS_NoMipmaps", StringComparison.OrdinalIgnoreCase);
                        }
                        else if (property is BytePropertyData byteProp)
                        {
                            return byteProp.Value != 13; // 13 = TMGS_NoMipmaps
                        }
                    }
                }
                // MipGenSettings not found = using default (FromTextureGroup) = needs fix
                return true;
            }
        }
        return false;
    }
    
    /// <summary>
    /// Check if a texture has inline data (no .ubulk needed).
    /// Returns true if texture data is stored inline in the .uexp file.
    /// </summary>
    private static bool CheckTextureHasInlineData(UAsset asset)
    {
        foreach (var export in asset.Exports)
        {
            if (GetExportClassName(asset, export) == "Texture2D" && export is TextureExport textureExport)
            {
                // Check if PlatformData exists and has mips
                if (textureExport.PlatformData?.Mips != null && textureExport.PlatformData.Mips.Count > 0)
                {
                    var mip = textureExport.PlatformData.Mips[0];
                    
                    // Check if the mip has inline data (ForceInlinePayload flag or DataResourceIndex >= 0)
                    if (mip.BulkData?.Header != null)
                    {
                        var header = mip.BulkData.Header;
                        
                        // ForceInlinePayload = 0x40, SingleUse = 0x08
                        // Inline textures typically have flags 0x48 (ForceInlinePayload | SingleUse)
                        bool hasInlineFlag = ((int)header.BulkDataFlags & 0x40) != 0;
                        
                        // Also check if DataResourceIndex is valid (UE5.3+ inline data)
                        bool hasDataResource = header.DataResourceIndex >= 0;
                        
                        // If either condition is true, data is inline
                        if (hasInlineFlag || hasDataResource)
                        {
                            return true;
                        }
                    }
                    
                    // Also check if mip has actual pixel data stored
                    if (mip.BulkData?.Data != null && mip.BulkData.Data.Length > 0)
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }
    
    private static UAssetResponse HasInlineTextureData(string? filePath, string? usmapPath)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path required" };
        if (!File.Exists(filePath))
            return new UAssetResponse { Success = false, Message = $"File not found: {filePath}" };

        try
        {
            var asset = LoadAsset(filePath, usmapPath);
            
            if (!IsAssetType(asset, "texture"))
                return new UAssetResponse { Success = true, Message = "Not a texture", Data = false };
            
            bool hasInline = CheckTextureHasInlineData(asset);
            return new UAssetResponse 
            { 
                Success = true, 
                Message = hasInline ? "Texture has inline data" : "Texture uses external bulk data",
                Data = hasInline 
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Error: {ex.Message}" };
        }
    }
    
    private static UAssetResponse BatchHasInlineTextureData(List<string>? filePaths, string? usmapPath)
    {
        if (filePaths == null || filePaths.Count == 0)
            return new UAssetResponse { Success = false, Message = "file_paths required" };

        try
        {
            Usmap? mappings = LoadMappings(usmapPath);
            
            // Return list of files that have inline texture data
            var inlineFiles = filePaths.AsParallel()
                .Where(filePath =>
                {
                    if (!File.Exists(filePath)) return false;
                    try
                    {
                        var asset = LoadAssetWithMappings(filePath, mappings);
                        return IsAssetType(asset, "texture") && CheckTextureHasInlineData(asset);
                    }
                    catch
                    {
                        return false;
                    }
                })
                .ToList();

            return new UAssetResponse
            {
                Success = true,
                Message = $"Found {inlineFiles.Count} textures with inline data",
                Data = inlineFiles
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Batch detection error: {ex.Message}" };
        }
    }
    
    /// <summary>
    /// Strip mipmaps using native UAssetAPI TextureExport.
    /// This is a pure C# implementation based on CUE4Parse's texture parsing.
    /// </summary>
    private static UAssetResponse StripMipmapsNative(string? filePath, string? usmapPath)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path required" };
        if (!File.Exists(filePath))
            return new UAssetResponse { Success = false, Message = $"File not found: {filePath}" };

        try
        {
            Console.Error.WriteLine($"[UAssetTool] Native mipmap stripping: {filePath}");
            
            // Use provided usmap_path, fall back to environment variable
            string? effectiveUsmapPath = usmapPath ?? Environment.GetEnvironmentVariable("USMAP_PATH");
            Console.Error.WriteLine($"[UAssetTool] Using USMAP: {effectiveUsmapPath ?? "null"}");
            var asset = LoadAsset(filePath, effectiveUsmapPath);
            
            // Debug: Log export types
            Console.Error.WriteLine($"[UAssetTool] Asset has {asset.Exports.Count} exports");
            foreach (var exp in asset.Exports)
            {
                var className = GetExportClassName(asset, exp);
                Console.Error.WriteLine($"[UAssetTool] Export type: {exp.GetType().Name}, Class: {className}");
            }
            
            // Find TextureExport
            TextureExport? textureExport = null;
            foreach (var export in asset.Exports)
            {
                if (export is TextureExport tex)
                {
                    textureExport = tex;
                    break;
                }
            }
            
            if (textureExport == null)
            {
                return new UAssetResponse { Success = false, Message = "No TextureExport found in asset" };
            }
            
            if (textureExport.PlatformData == null)
            {
                return new UAssetResponse { Success = false, Message = "TextureExport has no PlatformData (texture data not parsed)" };
            }
            
            int originalMipCount = textureExport.MipCount;
            Console.Error.WriteLine($"[UAssetTool] Original mip count: {originalMipCount}");
            
            if (originalMipCount <= 1)
            {
                return new UAssetResponse { Success = true, Message = "Texture already has 1 or fewer mipmaps" };
            }
            
            // Strip mipmaps using UAssetAPI
            bool stripped = textureExport.StripMipmaps();
            if (!stripped)
            {
                return new UAssetResponse { Success = false, Message = "Failed to strip mipmaps" };
            }
            
            Console.Error.WriteLine($"[UAssetTool] Stripped to {textureExport.MipCount} mipmap(s)");
            
            // Update DataResources for inline mip data
            if (asset.DataResources != null && textureExport.PlatformData?.Mips?.Count > 0)
            {
                var mip = textureExport.PlatformData.Mips[0];
                int dataSize = mip.BulkData?.Data?.Length ?? 0;
                
                // Calculate the SerialOffset where pixel data will be in the .uexp
                // Structure: Properties + LightingGuid(16) + StripFlags(4) + bCooked(4) + bSerializeMipData(4)
                //          + PixelFormatFName(8) + ExtraBytes(0-4) + SkipOffset(8)
                //          + Placeholder(16) + SizeX(4) + SizeY(4) + PackedData(4) + PixelFormat FString(4+len)
                //          + FirstMipToSerialize(4) + MipCount(4) + MipHeader(4+12) + bIsVirtual(4)
                // 
                // For Marvel Rivals textures with 1 mip:
                // Properties header: 10 bytes (unversioned header)
                // LightingGuid: 16 bytes
                // StripFlags: 4 bytes  
                // bCooked: 4 bytes
                // bSerializeMipData: 4 bytes
                // PixelFormatFName: 8 bytes
                // ExtraBytes: 4 bytes (Marvel Rivals specific)
                // SkipOffset: 8 bytes
                // Placeholder: 16 bytes
                // SizeX: 4 bytes
                // SizeY: 4 bytes
                // PackedData: 4 bytes
                // PixelFormat FString: 4 + 8 = 12 bytes (for "PF_DXT1\0")
                // FirstMipToSerialize: 4 bytes
                // MipCount: 4 bytes
                // Mip0 header: 4 (data_resource_id) + 4 (SizeX) + 4 (SizeY) + 4 (SizeZ) = 16 bytes
                // bIsVirtual: 4 bytes
                // Total: 10 + 16 + 4 + 4 + 4 + 8 + 4 + 8 + 16 + 4 + 4 + 4 + 12 + 4 + 4 + 16 + 4 = 126 bytes
                
                // Calculate based on pixel format string length
                string pixelFormat = textureExport.PlatformData.PixelFormat ?? "PF_DXT1";
                int pixelFormatLen = pixelFormat.Length + 1; // +1 for null terminator
                
                // Base offset calculation
                int serialOffset = 10  // Properties header (unversioned)
                    + 16  // LightingGuid
                    + 4   // StripFlags
                    + 4   // bCooked
                    + 4   // bSerializeMipData
                    + 8   // PixelFormatFName
                    + (textureExport.ExtraBytes?.Length ?? 0)  // ExtraBytes (Marvel Rivals specific)
                    + 8   // SkipOffset
                    + 16  // Placeholder
                    + 4   // SizeX
                    + 4   // SizeY
                    + 4   // PackedData
                    + 4 + pixelFormatLen  // PixelFormat FString (length + chars)
                    + 4   // FirstMipToSerialize
                    + 4   // MipCount
                    + 16  // Mip0 header (data_resource_id + SizeX + SizeY + SizeZ)
                    + 4;  // bIsVirtual
                
                // Create a new DataResource entry for the inline mip
                var inlineResource = new UAssetAPI.UnrealTypes.FObjectDataResource(
                    (UAssetAPI.UnrealTypes.EObjectDataResourceFlags)0,
                    serialOffset,  // SerialOffset - where pixel data starts in .uexp
                    -1, // DuplicateSerialOffset
                    dataSize, // SerialSize
                    dataSize, // RawSize
                    new UAssetAPI.UnrealTypes.FPackageIndex(1), // OuterIndex
                    0x48 // LegacyBulkDataFlags - ForceInlinePayload | SingleUse
                );
                
                // Clear and add only 1 entry
                asset.DataResources.Clear();
                asset.DataResources.Add(inlineResource);
                
                // Set the mip's DataResourceIndex to 0 (first entry)
                if (mip.BulkData?.Header != null)
                    mip.BulkData.Header.DataResourceIndex = 0;
            }
            
            // Save the modified asset
            asset.Write(filePath);
            
            // Delete .ubulk file if it exists (data is now inline)
            string ubulkPath = Path.ChangeExtension(filePath, ".ubulk");
            if (File.Exists(ubulkPath))
            {
                Console.Error.WriteLine($"[UAssetTool] Deleting .ubulk: {ubulkPath}");
                File.Delete(ubulkPath);
            }
            
            return new UAssetResponse 
            { 
                Success = true, 
                Message = $"Stripped mipmaps: {originalMipCount} -> {textureExport.MipCount}",
                Data = JsonSerializer.SerializeToElement(new { original_mips = originalMipCount, new_mips = textureExport.MipCount })
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[UAssetTool] Native strip error: {ex.Message}");
            Console.Error.WriteLine($"[UAssetTool] Stack: {ex.StackTrace}");
            return new UAssetResponse { Success = false, Message = $"Error: {ex.Message}" };
        }
    }
    
    /// <summary>
    /// Batch strip mipmaps from multiple textures using native UAssetAPI TextureExport.
    /// This processes all files in a single call for better performance.
    /// </summary>
    private static UAssetResponse BatchStripMipmapsNative(List<string>? filePaths, string? usmapPath)
    {
        if (filePaths == null || filePaths.Count == 0)
            return new UAssetResponse { Success = false, Message = "file_paths required" };

        try
        {
            Console.Error.WriteLine($"[UAssetTool] Batch stripping mipmaps for {filePaths.Count} files");
            
            // Use provided usmap_path, fall back to environment variable
            string? effectiveUsmapPath = usmapPath ?? Environment.GetEnvironmentVariable("USMAP_PATH");
            Console.Error.WriteLine($"[UAssetTool] Using USMAP: {effectiveUsmapPath ?? "null"}");
            Usmap? mappings = LoadMappings(effectiveUsmapPath);
            
            var results = new List<object>();
            int successCount = 0;
            int skipCount = 0;
            int errorCount = 0;
            
            foreach (var filePath in filePaths)
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                {
                    results.Add(new { path = filePath, success = false, message = "File not found" });
                    errorCount++;
                    continue;
                }
                
                try
                {
                    var asset = LoadAssetWithMappings(filePath, mappings);
                    
                    // Find TextureExport
                    TextureExport? textureExport = null;
                    foreach (var export in asset.Exports)
                    {
                        if (export is TextureExport tex)
                        {
                            textureExport = tex;
                            break;
                        }
                    }
                    
                    if (textureExport == null)
                    {
                        results.Add(new { path = filePath, success = false, message = "No TextureExport found" });
                        errorCount++;
                        continue;
                    }
                    
                    if (textureExport.PlatformData == null)
                    {
                        results.Add(new { path = filePath, success = false, message = "No PlatformData (texture not parsed)" });
                        errorCount++;
                        continue;
                    }
                    
                    int originalMipCount = textureExport.MipCount;
                    
                    if (originalMipCount <= 1)
                    {
                        results.Add(new { path = filePath, success = true, message = "Already has 1 mipmap", skipped = true });
                        skipCount++;
                        continue;
                    }
                    
                    // Target data_resource_id = 5 for Marvel Rivals textures
                    int targetDataResourceId = 5;
                    
                    // Strip mipmaps
                    bool stripped = textureExport.StripMipmaps();
                    if (!stripped)
                    {
                        results.Add(new { path = filePath, success = false, message = "Failed to strip mipmaps" });
                        errorCount++;
                        continue;
                    }
                    
                    // Update DataResources
                    if (asset.DataResources != null && textureExport.PlatformData?.Mips?.Count > 0)
                    {
                        var mip = textureExport.PlatformData.Mips[0];
                        int dataSize = mip.BulkData?.Data?.Length ?? 0;
                        
                        var inlineResource = new UAssetAPI.UnrealTypes.FObjectDataResource(
                            (UAssetAPI.UnrealTypes.EObjectDataResourceFlags)0,
                            0,
                            -1,
                            dataSize,
                            dataSize,
                            new UAssetAPI.UnrealTypes.FPackageIndex(1),
                            0x48
                        );
                        
                        asset.DataResources.Clear();
                        asset.DataResources.Add(inlineResource);
                        if (mip.BulkData?.Header != null)
                            mip.BulkData.Header.DataResourceIndex = targetDataResourceId;
                    }
                    
                    // Save the modified asset (first pass)
                    asset.Write(filePath);
                    
                    // Second pass: Find inline data offset and update DataResource
                    string uexpPath = Path.ChangeExtension(filePath, ".uexp");
                    if (File.Exists(uexpPath) && asset.DataResources != null && asset.DataResources.Count > 0)
                    {
                        var mip = textureExport.PlatformData?.Mips?[0];
                        if (mip?.BulkData?.Data != null && mip.BulkData.Data.Length >= 4)
                        {
                            int drIndex = mip.BulkData.Header.DataResourceIndex;
                            if (drIndex < 0 || drIndex >= asset.DataResources.Count)
                                drIndex = asset.DataResources.Count - 1;
                            
                            byte[] uexpData = File.ReadAllBytes(uexpPath);
                            byte[] searchPattern = new byte[4];
                            Array.Copy(mip.BulkData.Data, 0, searchPattern, 0, 4);
                            
                            long inlineOffset = -1;
                            for (int i = 0; i < uexpData.Length - 4; i++)
                            {
                                if (uexpData[i] == searchPattern[0] && 
                                    uexpData[i+1] == searchPattern[1] &&
                                    uexpData[i+2] == searchPattern[2] &&
                                    uexpData[i+3] == searchPattern[3])
                                {
                                    inlineOffset = i;
                                    break;
                                }
                            }
                            
                            if (inlineOffset >= 0)
                            {
                                var dr = asset.DataResources[drIndex];
                                asset.DataResources[drIndex] = new UAssetAPI.UnrealTypes.FObjectDataResource(
                                    dr.Flags,
                                    inlineOffset,
                                    dr.DuplicateSerialOffset,
                                    dr.SerialSize,
                                    dr.RawSize,
                                    dr.OuterIndex,
                                    dr.LegacyBulkDataFlags,
                                    dr.CookedIndex
                                );
                                asset.Write(filePath);
                            }
                        }
                    }
                    
                    // Patch data_resource_id in .uexp
                    if (File.Exists(uexpPath))
                    {
                        byte[] uexpBytes = File.ReadAllBytes(uexpPath);
                        int targetValue = targetDataResourceId;
                        
                        if (targetValue > 0 && targetValue < 100)
                        {
                            int firstPos = -1;
                            int secondPos = -1;
                            
                            for (int i = 100; i < Math.Min(uexpBytes.Length - 8, 300); i++)
                            {
                                int val1 = BitConverter.ToInt32(uexpBytes, i);
                                int val2 = BitConverter.ToInt32(uexpBytes, i + 4);
                                
                                if (val1 == 1 && val2 == 0 && firstPos == -1)
                                    firstPos = i + 4;
                                else if (val1 == 1 && val2 == targetValue && secondPos == -1 && firstPos >= 0)
                                    secondPos = i + 4;
                            }
                            
                            if (firstPos >= 0 && secondPos >= 0 && (secondPos - firstPos) == 64)
                            {
                                byte[] targetBytes = BitConverter.GetBytes(targetValue);
                                byte[] zeroBytes = BitConverter.GetBytes(0);
                                
                                Array.Copy(targetBytes, 0, uexpBytes, firstPos, 4);
                                Array.Copy(zeroBytes, 0, uexpBytes, secondPos, 4);
                                
                                File.WriteAllBytes(uexpPath, uexpBytes);
                            }
                        }
                    }
                    
                    // Delete .ubulk file
                    string ubulkPath = Path.ChangeExtension(filePath, ".ubulk");
                    if (File.Exists(ubulkPath))
                    {
                        File.Delete(ubulkPath);
                    }
                    
                    results.Add(new { 
                        path = filePath, 
                        success = true, 
                        message = $"Stripped {originalMipCount} -> {textureExport.MipCount}",
                        original_mips = originalMipCount,
                        new_mips = textureExport.MipCount
                    });
                    successCount++;
                    
                    Console.Error.WriteLine($"[UAssetTool] Stripped: {Path.GetFileName(filePath)} ({originalMipCount} -> {textureExport.MipCount})");
                }
                catch (Exception ex)
                {
                    results.Add(new { path = filePath, success = false, message = ex.Message });
                    errorCount++;
                    Console.Error.WriteLine($"[UAssetTool] Error processing {Path.GetFileName(filePath)}: {ex.Message}");
                }
            }
            
            Console.Error.WriteLine($"[UAssetTool] Batch complete: {successCount} stripped, {skipCount} skipped, {errorCount} errors");
            
            return new UAssetResponse
            {
                Success = true,
                Message = $"Batch processed {filePaths.Count} files: {successCount} stripped, {skipCount} skipped, {errorCount} errors",
                Data = new { 
                    total = filePaths.Count,
                    success_count = successCount,
                    skip_count = skipCount,
                    error_count = errorCount,
                    results = results
                }
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[UAssetTool] Batch strip error: {ex.Message}");
            return new UAssetResponse { Success = false, Message = $"Batch error: {ex.Message}" };
        }
    }
    
    private static UAssetResponse GetTextureInfo(string? filePath, string? usmapPath)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path required" };
        if (!File.Exists(filePath))
            return new UAssetResponse { Success = false, Message = $"File not found: {filePath}" };

        try
        {
            // Use provided usmap_path, fall back to environment variable
            string? effectiveUsmapPath = usmapPath ?? Environment.GetEnvironmentVariable("USMAP_PATH");
            Console.Error.WriteLine($"[UAssetTool] USMAP path: {effectiveUsmapPath ?? "null"}");
            
            if (!string.IsNullOrEmpty(effectiveUsmapPath) && File.Exists(effectiveUsmapPath))
            {
                Console.Error.WriteLine($"[UAssetTool] USMAP file found");
            }
            else
            {
                Console.Error.WriteLine($"[UAssetTool] USMAP file not found or path is null");
            }
            
            var asset = LoadAsset(filePath, effectiveUsmapPath);
            asset.UseSeparateBulkDataFiles = true;
            
            Console.Error.WriteLine($"[UAssetTool] Mappings loaded: {asset.Mappings != null}");
            
            var info = ExtractTextureInfo(asset);
            return new UAssetResponse { Success = true, Message = "Texture info retrieved", Data = info };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[UAssetTool] Error in GetTextureInfo: {ex.Message}");
            return new UAssetResponse { Success = false, Message = $"Error: {ex.Message}" };
        }
    }
    
    private static Dictionary<string, object> ExtractTextureInfo(UAsset asset)
    {
        // Use snake_case for consistency with other responses
        var info = new Dictionary<string, object>
        {
            ["is_texture"] = false,
            ["format"] = "Unknown",
            ["pixel_format"] = "Unknown",
            ["width"] = 0,
            ["height"] = 0,
            ["mip_count"] = 0,
            ["mip_gen_settings"] = "Unknown",
            ["compression_settings"] = "Unknown",
            ["has_inline_data"] = false,
            ["size_bytes"] = 0L
        };
        
        foreach (var export in asset.Exports)
        {
            string className = GetExportClassName(asset, export);
            if (className == "Texture2D" || className == "TextureCube" || className == "VolumeTexture")
            {
                info["is_texture"] = true;
                
                // Try to get detailed info from TextureExport
                if (export is TextureExport textureExport && textureExport.PlatformData != null)
                {
                    var platformData = textureExport.PlatformData;
                    info["pixel_format"] = platformData.PixelFormat ?? "Unknown";
                    info["format"] = $"PF_{platformData.PixelFormat ?? "Unknown"}";
                    info["width"] = platformData.SizeX;
                    info["height"] = platformData.SizeY;
                    info["mip_count"] = platformData.Mips?.Count ?? 0;
                    info["has_inline_data"] = !textureExport.HasExternalBulkData;
                    info["size_bytes"] = platformData.GetTotalMipDataSize();
                }
                
                // Extract properties from NormalExport
                if (export is NormalExport normalExport && normalExport.Data != null)
                {
                    var properties = new List<Dictionary<string, string>>();
                    foreach (var prop in normalExport.Data)
                    {
                        var propInfo = new Dictionary<string, string>
                        {
                            ["name"] = prop.Name?.Value?.Value ?? "Unknown",
                            ["type"] = prop.GetType().Name
                        };
                        
                        if (prop is EnumPropertyData enumProp)
                            propInfo["value"] = enumProp.Value?.Value?.Value ?? "null";
                        else if (prop is BytePropertyData byteProp)
                            propInfo["value"] = byteProp.Value.ToString();
                        else if (prop is IntPropertyData intProp)
                            propInfo["value"] = intProp.Value.ToString();
                        else if (prop is BoolPropertyData boolProp)
                            propInfo["value"] = boolProp.Value.ToString();
                        else
                            propInfo["value"] = "(complex)";
                        
                        properties.Add(propInfo);
                        
                        // Extract specific texture settings
                        string propName = prop.Name?.Value?.Value ?? "";
                        if (propName == "MipGenSettings")
                        {
                            if (prop is EnumPropertyData ep)
                                info["mip_gen_settings"] = ep.Value?.Value?.Value ?? "Unknown";
                            else if (prop is BytePropertyData bp)
                                info["mip_gen_settings"] = $"ByteValue_{bp.Value}";
                        }
                        else if (propName == "CompressionSettings")
                        {
                            if (prop is EnumPropertyData ep)
                                info["compression_settings"] = ep.Value?.Value?.Value ?? "Unknown";
                        }
                    }
                    info["properties"] = properties;
                }
                break;
            }
        }
        
        return info;
    }
    
    #endregion

    #region Mesh Operations
    
    private static UAssetResponse PatchMesh(string? filePath, string? uexpPath)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path required" };
        if (string.IsNullOrEmpty(uexpPath))
            return new UAssetResponse { Success = false, Message = "UEXP path required" };
        if (!File.Exists(filePath))
            return new UAssetResponse { Success = false, Message = $"File not found: {filePath}" };
        if (!File.Exists(uexpPath))
            return new UAssetResponse { Success = false, Message = $"UEXP not found: {uexpPath}" };

        try
        {
            File.Copy(filePath, filePath + ".backup", true);
            File.Copy(uexpPath, uexpPath + ".backup", true);
            
            // TODO: Implement actual mesh patching
            return new UAssetResponse { Success = true, Message = "Mesh patch placeholder (backups created)" };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Error: {ex.Message}" };
        }
    }
    
    private static UAssetResponse GetMeshInfo(string? filePath, string? usmapPath)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path required" };
        if (!File.Exists(filePath))
            return new UAssetResponse { Success = false, Message = $"File not found: {filePath}" };

        try
        {
            // Use provided usmap_path, fall back to environment variable
            string? effectiveUsmapPath = usmapPath ?? Environment.GetEnvironmentVariable("USMAP_PATH");
            Console.Error.WriteLine($"[UAssetTool] GetMeshInfo USMAP path: {effectiveUsmapPath ?? "null"}");
            
            var asset = LoadAsset(filePath, effectiveUsmapPath);
            asset.UseSeparateBulkDataFiles = true;
            
            // Use snake_case for consistency with other responses
            var info = new Dictionary<string, object>
            {
                ["mesh_type"] = "Unknown",
                ["vertex_count"] = 0,
                ["triangle_count"] = 0,
                ["lod_count"] = 0,
                ["material_slots"] = 0,
                ["bone_count"] = 0,
                ["has_morph_targets"] = false,
                ["has_vertex_colors"] = false
            };
            
            foreach (var export in asset.Exports)
            {
                string className = GetExportClassName(asset, export);
                
                if (className == "SkeletalMesh")
                {
                    info["mesh_type"] = "SkeletalMesh";
                    // Extract skeletal mesh properties if available
                    if (export is NormalExport normalExport && normalExport.Data != null)
                    {
                        foreach (var prop in normalExport.Data)
                        {
                            string propName = prop.Name?.Value?.Value ?? "";
                            if (propName == "Materials" && prop is ArrayPropertyData arrayProp)
                            {
                                info["material_slots"] = arrayProp.Value?.Length ?? 0;
                            }
                        }
                    }
                    break;
                }
                else if (className == "StaticMesh")
                {
                    info["mesh_type"] = "StaticMesh";
                    // Extract static mesh properties if available
                    if (export is NormalExport normalExport && normalExport.Data != null)
                    {
                        foreach (var prop in normalExport.Data)
                        {
                            string propName = prop.Name?.Value?.Value ?? "";
                            if (propName == "StaticMaterials" && prop is ArrayPropertyData arrayProp)
                            {
                                info["material_slots"] = arrayProp.Value?.Length ?? 0;
                            }
                        }
                    }
                    break;
                }
            }
            
            return new UAssetResponse { Success = true, Message = "Mesh info retrieved", Data = info };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[UAssetTool] Error in GetMeshInfo: {ex.Message}");
            return new UAssetResponse { Success = false, Message = $"Error: {ex.Message}" };
        }
    }
    
    private static UAssetResponse FixSerializeSizeJson(string? filePath, string? usmapPath)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path required" };
        if (!File.Exists(filePath))
            return new UAssetResponse { Success = false, Message = $"File not found: {filePath}" };

        var result = FixSerializeSize(filePath, usmapPath);
        return new UAssetResponse
        {
            Success = (bool)(result.GetType().GetProperty("success")?.GetValue(result) ?? false),
            Message = (string)(result.GetType().GetProperty("message")?.GetValue(result) ?? ""),
            Data = result
        };
    }
    
    private static object FixSerializeSize(string uassetPath, string? usmapPath)
    {
        if (string.IsNullOrEmpty(usmapPath) || !File.Exists(usmapPath))
        {
            return new { success = false, message = "USmap file required for SerializeSize fix", fixed_count = 0 };
        }

        var asset = LoadAsset(uassetPath, usmapPath);
        asset.UseSeparateBulkDataFiles = true;
        
        string uexpPath = uassetPath.Replace(".uasset", ".uexp");
        if (!File.Exists(uexpPath))
        {
            return new { success = false, message = "No .uexp file found", fixed_count = 0 };
        }

        long uexpSize = new FileInfo(uexpPath).Length;
        long headerSize = asset.Exports.Min(e => e.SerialOffset);
        var sortedExports = asset.Exports.OrderBy(e => e.SerialOffset).ToList();
        
        // Check for .ubulk file - if present, we need to add its size + overhead to the last export
        // This is because the game reads bulk data inline with the export in Zen/IoStore format
        string ubulkPath = Path.ChangeExtension(uassetPath, ".ubulk");
        long bulkDataAdjustment = 0;
        if (File.Exists(ubulkPath))
        {
            long ubulkSize = new FileInfo(ubulkPath).Length;
            // The game reads bulk data inline with serialization overhead
            // Overhead = FBulkData headers + alignment padding
            // TODO: Calculate this properly based on number of bulk data entries
            // For now, 432 bytes works for tested StaticMesh assets
            const long BULK_DATA_OVERHEAD = 432;
            bulkDataAdjustment = ubulkSize + BULK_DATA_OVERHEAD;
            Console.Error.WriteLine($"[FixSerializeSize] Found .ubulk ({ubulkSize} bytes), adding {bulkDataAdjustment} (overhead={BULK_DATA_OVERHEAD}) to last export");
        }
        
        var fixes = new List<object>();
        int fixedCount = 0;

        for (int i = 0; i < sortedExports.Count; i++)
        {
            var export = sortedExports[i];
            long startInUexp = export.SerialOffset - headerSize;
            // For the last export, exclude the 4-byte PACKAGE_FILE_TAG (0x9E2A83C1) at the end of .uexp
            // The tag is not part of the export data and should not be included in SerialSize
            long endInUexp = (i < sortedExports.Count - 1) 
                ? sortedExports[i + 1].SerialOffset - headerSize 
                : uexpSize - 4;  // Subtract 4 bytes for PACKAGE_FILE_TAG
            
            long actualSize = endInUexp - startInUexp;
            
            // For the last export, add bulk data adjustment if .ubulk exists
            if (i == sortedExports.Count - 1 && bulkDataAdjustment > 0)
            {
                actualSize += bulkDataAdjustment;
            }
            
            long headerSize_current = export.SerialSize;
            
            if (actualSize != headerSize_current)
            {
                // Use actual size directly - no padding added
                // This ensures SerialSize matches the real export data size
                export.SerialSize = actualSize;
                fixes.Add(new
                {
                    export_name = export.ObjectName?.Value?.Value ?? $"Export_{i}",
                    old_size = headerSize_current,
                    new_size = actualSize,
                    difference = actualSize - headerSize_current
                });
                fixedCount++;
            }
        }

        // Write the fixed asset back to disk if any fixes were made
        if (fixedCount > 0)
        {
            asset.Write(uassetPath);
            Console.Error.WriteLine($"[FixSerializeSize] Wrote {fixedCount} SerialSize fixes to {uassetPath}");
        }

        return new
        {
            success = true,
            message = fixedCount > 0 ? $"Fixed {fixedCount} SerialSize mismatches" : "No fixes needed",
            fixed_count = fixedCount,
            fixes = fixes
        };
    }
    
    #endregion

    #region Debug Operations
    
    private static UAssetResponse DebugAssetInfo(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path required" };
        if (!File.Exists(filePath))
            return new UAssetResponse { Success = false, Message = $"File not found: {filePath}" };

        try
        {
            string? usmapPath = Environment.GetEnvironmentVariable("USMAP_PATH");
            var asset = LoadAsset(filePath, usmapPath);
            
            var info = new Dictionary<string, object>();
            
            var exports = new List<Dictionary<string, string>>();
            foreach (var export in asset.Exports)
            {
                exports.Add(new Dictionary<string, string>
                {
                    ["ExportType"] = export.GetType().Name,
                    ["ObjectName"] = export.ObjectName?.Value?.Value ?? "null",
                    ["ClassName"] = GetExportClassName(asset, export)
                });
            }
            info["Exports"] = exports;
            
            var imports = asset.Imports.Select(i => i.ObjectName?.Value?.Value ?? "null").ToList();
            info["Imports"] = imports;
            info["DetectedType"] = DetectAssetType(asset);
            
            return new UAssetResponse
            {
                Success = true,
                Message = $"Asset info for {Path.GetFileName(filePath)}",
                Data = info
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Error: {ex.Message}" };
        }
    }
    
    private static void DumpAssetInfo(UAsset asset, string filePath)
    {
        Console.WriteLine($"=== Asset Dump: {Path.GetFileName(filePath)} ===");
        Console.WriteLine($"Detected Type: {DetectAssetType(asset)}");
        Console.WriteLine($"Exports: {asset.Exports.Count}");
        Console.WriteLine($"Imports: {asset.Imports.Count}");
        Console.WriteLine();
        
        Console.WriteLine("=== Exports ===");
        for (int i = 0; i < asset.Exports.Count; i++)
        {
            var export = asset.Exports[i];
            Console.WriteLine($"  [{i}] {export.ObjectName?.Value?.Value} (Class: {GetExportClassName(asset, export)})");
            Console.WriteLine($"      SerialOffset: 0x{export.SerialOffset:X}, SerialSize: {export.SerialSize}");
        }
        
        Console.WriteLine();
        Console.WriteLine("=== Imports ===");
        for (int i = 0; i < asset.Imports.Count; i++)
        {
            var import = asset.Imports[i];
            Console.WriteLine($"  [{i}] {import.ObjectName?.Value?.Value}");
        }
    }
    
    #endregion

    #region Asset Loading Helpers
    
    private static UAsset LoadAsset(string filePath, string? usmapPath)
    {
        Console.Error.WriteLine($"[UAssetTool] Loading asset: {filePath}");
        Console.Error.WriteLine($"[UAssetTool] USMAP path: {usmapPath ?? "null"}");
        Usmap? mappings = LoadMappings(usmapPath);
        Console.Error.WriteLine($"[UAssetTool] Mappings loaded: {mappings != null}");
        if (mappings != null)
        {
            Console.Error.WriteLine($"[UAssetTool] Mappings has {mappings.Schemas?.Count ?? 0} schemas");
        }
        return LoadAssetWithMappings(filePath, mappings);
    }
    
    private static UAsset LoadAssetWithMappings(string filePath, Usmap? mappings)
    {
        var asset = new UAsset(filePath, EngineVersion.VER_UE5_3, mappings);
        asset.UseSeparateBulkDataFiles = true;
        Console.Error.WriteLine($"[UAssetTool] Asset loaded: HasUnversionedProperties={asset.HasUnversionedProperties}, Exports={asset.Exports?.Count ?? 0}");
        return asset;
    }
    
    private static Usmap? LoadMappings(string? usmapPath)
    {
        if (!string.IsNullOrEmpty(usmapPath) && File.Exists(usmapPath))
        {
            Console.Error.WriteLine($"[UAssetTool] Loading USMAP from: {usmapPath}");
            try
            {
                var usmap = new Usmap(usmapPath);
                Console.Error.WriteLine($"[UAssetTool] USMAP loaded successfully");
                return usmap;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[UAssetTool] Failed to load USMAP: {ex.Message}");
            }
        }
        else
        {
            Console.Error.WriteLine($"[UAssetTool] USMAP file not found or path is null");
        }
        return null;
    }
    
    private static void WriteJsonResponse(bool success, string message, object? data = null)
    {
        var response = new UAssetResponse { Success = success, Message = message, Data = data };
        Console.WriteLine(JsonSerializer.Serialize(response));
    }
    
    #endregion

    #region Zen Package Conversion
    
    private static UAssetResponse ConvertToZen(string? filePath, string? usmapPath)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path required" };
        if (!File.Exists(filePath))
            return new UAssetResponse { Success = false, Message = $"File not found: {filePath}" };

        try
        {
            byte[] zenData = ZenPackage.ZenConverter.ConvertLegacyToZen(filePath, usmapPath);
            
            string outputPath = Path.ChangeExtension(filePath, ".uzenasset");
            File.WriteAllBytes(outputPath, zenData);
            
            return new UAssetResponse 
            { 
                Success = true, 
                Message = $"Converted to Zen format: {outputPath}",
                Data = new { output_path = outputPath, size = zenData.Length }
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Conversion failed: {ex.Message}" };
        }
    }
    
    private static UAssetResponse ConvertFromZen(string? filePath, string? usmapPath)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "File path required" };
        if (!File.Exists(filePath))
            return new UAssetResponse { Success = false, Message = $"File not found: {filePath}" };

        try
        {
            // TODO: Implement Zen to Legacy conversion
            return new UAssetResponse 
            { 
                Success = false, 
                Message = "Zen to Legacy conversion not yet implemented" 
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Conversion failed: {ex.Message}" };
        }
    }
    
    #endregion
    
    /// <summary>
    /// Dump raw Zen package data from game IoStore for comparison
    /// </summary>
    private static int CliDumpZenFromGame(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: UAssetTool dump_zen_from_game <paks_path> <package_path> [output_file]");
            Console.Error.WriteLine("Example: UAssetTool dump_zen_from_game \"E:\\Game\\Paks\" \"/Game/Marvel/Characters/1057/1057001/Meshes/SK_1057_1057001\" original.bin");
            return 1;
        }

        string paksPath = args[1];
        string packagePath = args[2];
        string? outputFile = args.Length > 3 ? args[3] : null;

        if (!Directory.Exists(paksPath))
        {
            Console.Error.WriteLine($"Paks directory not found: {paksPath}");
            return 1;
        }

        try
        {
            // Find all .utoc files
            var utocFiles = Directory.GetFiles(paksPath, "*.utoc", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f)
                .ToList();

            Console.WriteLine($"Searching {utocFiles.Count} containers for package: {packagePath}");

            // Calculate package ID
            var packageId = IoStore.FPackageId.FromName(packagePath);
            Console.WriteLine($"Package ID: 0x{packageId.Value:X16}");

            // Parse AES key for Marvel Rivals
            byte[] aesKey = Convert.FromHexString("0C263D8C22DCB085894899C3A3796383E9BF9DE0CBFB08C9BF2DEF2E84F29D74");

            foreach (var utocFile in utocFiles)
            {
                try
                {
                    using var reader = new IoStore.IoStoreReader(utocFile, aesKey);
                    
                    // Look for ExportBundleData chunk for this package
                    var chunkId = IoStore.FIoChunkId.FromPackageId(packageId, 0, IoStore.EIoChunkType.ExportBundleData);
                    
                    if (reader.HasChunk(chunkId))
                    {
                        Console.WriteLine($"Found in: {Path.GetFileName(utocFile)}");
                        
                        byte[] zenData = reader.ReadChunk(chunkId);
                        Console.WriteLine($"Zen package size: {zenData.Length} bytes");
                        
                        // Dump header info
                        Console.WriteLine("\n=== Zen Package Header (first 256 bytes) ===");
                        DumpHex(zenData, 0, Math.Min(256, zenData.Length));
                        
                        // Also check for BulkData chunk
                        var bulkChunkId = IoStore.FIoChunkId.FromPackageId(packageId, 0, IoStore.EIoChunkType.BulkData);
                        if (reader.HasChunk(bulkChunkId))
                        {
                            byte[] bulkData = reader.ReadChunk(bulkChunkId);
                            Console.WriteLine($"\nBulkData chunk 0: {bulkData.Length} bytes");
                        }
                        
                        // Check for additional BulkData chunks
                        for (int i = 1; i < 10; i++)
                        {
                            var bulkChunkIdN = IoStore.FIoChunkId.FromPackageId(packageId, (ushort)i, IoStore.EIoChunkType.BulkData);
                            if (reader.HasChunk(bulkChunkIdN))
                            {
                                byte[] bulkData = reader.ReadChunk(bulkChunkIdN);
                                Console.WriteLine($"BulkData chunk {i}: {bulkData.Length} bytes");
                            }
                        }
                        
                        // Save to file if requested
                        if (!string.IsNullOrEmpty(outputFile))
                        {
                            File.WriteAllBytes(outputFile, zenData);
                            Console.WriteLine($"\nSaved to: {outputFile}");
                        }
                        
                        return 0;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  Warning: Error reading {Path.GetFileName(utocFile)}: {ex.Message}");
                }
            }

            Console.Error.WriteLine($"Package not found: {packagePath}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }
    
    private static void DumpHex(byte[] data, int offset, int length)
    {
        for (int i = 0; i < length; i += 16)
        {
            Console.Write($"{offset + i:X8}: ");
            
            // Hex bytes
            for (int j = 0; j < 16; j++)
            {
                if (i + j < length)
                    Console.Write($"{data[offset + i + j]:X2} ");
                else
                    Console.Write("   ");
            }
            
            Console.Write(" ");
            
            // ASCII
            for (int j = 0; j < 16 && i + j < length; j++)
            {
                byte b = data[offset + i + j];
                Console.Write(b >= 32 && b < 127 ? (char)b : '.');
            }
            
            Console.WriteLine();
        }
    }
    
    private static int CliExtractPak(string[] args)
    {
        // Usage: extract_pak <pak_path> <output_dir> [--aes <key>] [--filter <patterns...>]
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: UAssetTool extract_pak <pak_path> <output_dir> [options]");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Options:");
            Console.Error.WriteLine("  --aes <key>          AES decryption key (hex string, 64 chars)");
            Console.Error.WriteLine("  --filter <patterns>  Only extract files matching patterns (space-separated)");
            Console.Error.WriteLine("  --list               List files only, don't extract");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Examples:");
            Console.Error.WriteLine("  extract_pak mod.pak output --filter SK_1036 MI_Body");
            Console.Error.WriteLine("  extract_pak mod.pak output --filter Meshes Textures Materials");
            Console.Error.WriteLine("  extract_pak mod.pak output --list");
            return 1;
        }

        string pakPath = args[1];
        string outputDir = args[2];
        string? aesKey = null;
        List<string> filters = new();
        bool listOnly = false;

        // Parse options
        for (int i = 3; i < args.Length; i++)
        {
            if (args[i] == "--aes" && i + 1 < args.Length)
            {
                aesKey = args[++i];
            }
            else if (args[i] == "--filter")
            {
                // Collect all following args until next option (starts with --)
                while (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                {
                    filters.Add(args[++i]);
                }
            }
            else if (args[i] == "--list")
            {
                listOnly = true;
            }
        }

        if (!File.Exists(pakPath))
        {
            Console.Error.WriteLine($"PAK file not found: {pakPath}");
            return 1;
        }

        try
        {
            Console.Error.WriteLine($"[ExtractPak] Opening PAK: {pakPath}");
            
            using var pakReader = new IoStore.PakReader(pakPath, aesKey);
            
            Console.Error.WriteLine($"[ExtractPak] PAK Version: {pakReader.Version}");
            Console.Error.WriteLine($"[ExtractPak] Mount Point: {pakReader.MountPoint}");
            Console.Error.WriteLine($"[ExtractPak] Encrypted Index: {pakReader.EncryptedIndex}");
            Console.Error.WriteLine($"[ExtractPak] Total Files: {pakReader.Files.Count}");
            
            var filesToExtract = pakReader.Files.ToList();
            
            // Apply filters if specified (file must match ANY of the filters)
            if (filters.Count > 0)
            {
                filesToExtract = filesToExtract.Where(f => 
                    filters.Any(filter => f.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToList();
                Console.Error.WriteLine($"[ExtractPak] Files matching filters [{string.Join(", ", filters)}]: {filesToExtract.Count}");
            }
            
            if (listOnly)
            {
                Console.WriteLine($"Files in PAK ({filesToExtract.Count}):");
                foreach (var file in filesToExtract)
                {
                    var entry = pakReader.GetEntry(file);
                    Console.WriteLine($"  {file}");
                    if (entry != null)
                    {
                        Console.WriteLine($"    Size: {entry.UncompressedSize} bytes, Compressed: {entry.CompressedSize} bytes");
                    }
                }
                return 0;
            }
            
            // Create output directory
            Directory.CreateDirectory(outputDir);
            
            int extracted = 0;
            int failed = 0;
            
            foreach (var file in filesToExtract)
            {
                try
                {
                    byte[] data = pakReader.Get(file);
                    
                    // Determine output path
                    string relativePath = file;
                    if (relativePath.StartsWith("../"))
                    {
                        // Remove leading ../../../ etc
                        while (relativePath.StartsWith("../"))
                            relativePath = relativePath.Substring(3);
                    }
                    
                    string outputPath = Path.Combine(outputDir, relativePath);
                    string? outputDirPath = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrEmpty(outputDirPath))
                        Directory.CreateDirectory(outputDirPath);
                    
                    File.WriteAllBytes(outputPath, data);
                    extracted++;
                    
                    if (extracted % 10 == 0 || extracted == filesToExtract.Count)
                    {
                        Console.Error.WriteLine($"[ExtractPak] Extracted {extracted}/{filesToExtract.Count} files...");
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[ExtractPak] Failed to extract '{file}': {ex.Message}");
                    failed++;
                }
            }
            
            Console.WriteLine($"Extraction complete: {extracted} extracted, {failed} failed");
            Console.WriteLine($"Output directory: {outputDir}");
            return failed > 0 ? 1 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (Environment.GetEnvironmentVariable("DEBUG") == "1")
            {
                Console.Error.WriteLine(ex.StackTrace);
            }
            return 1;
        }
    }
}

#region Request/Response Models

public class UAssetRequest
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "";
    
    [JsonPropertyName("file_path")]
    public string? FilePath { get; set; }
    
    [JsonPropertyName("file_paths")]
    public List<string>? FilePaths { get; set; }
    
    [JsonPropertyName("mip_gen")]
    public string? MipGen { get; set; }
    
    [JsonPropertyName("uexp_path")]
    public string? UexpPath { get; set; }
    
    [JsonPropertyName("usmap_path")]
    public string? UsmapPath { get; set; }
    
    // GUI Backend fields
    [JsonPropertyName("export_index")]
    public int ExportIndex { get; set; } = -1;
    
    [JsonPropertyName("property_path")]
    public string? PropertyPath { get; set; }
    
    [JsonPropertyName("property_name")]
    public string? PropertyName { get; set; }
    
    [JsonPropertyName("property_type")]
    public string? PropertyType { get; set; }
    
    [JsonPropertyName("property_value")]
    public JsonElement? PropertyValue { get; set; }
    
    [JsonPropertyName("output_path")]
    public string? OutputPath { get; set; }
    
    [JsonPropertyName("json_data")]
    public string? JsonData { get; set; }
}

public class UAssetResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
    
    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
    
    [JsonPropertyName("data")]
    public object? Data { get; set; }
}

#endregion
