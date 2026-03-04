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
public partial class Program
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
                "inspect_zen" => CliInspectZen(args),
                "create_pak" => CliCreatePak(args),
                "create_companion_pak" => CliCreateCompanionPak(args),
                "create_iostore_bundle" => CliCreateIoStoreBundle(args),
                "create_mod_iostore" => CliCreateModIoStore(args),
                "extract_iostore" => CliExtractIoStore(args),
                "extract_iostore_legacy" => CliExtractIoStoreLegacy(args),
                "is_iostore_compressed" => CliIsIoStoreCompressed(args),
                "is_iostore_encrypted" => CliIsIoStoreEncrypted(args),
                "extract_script_objects" => CliExtractScriptObjects(args),
                "recompress_iostore" => CliRecompressIoStore(args),
                "cityhash" => CliCityHash(args),
                "from_json" => CliFromJson(args),
                "to_json" => CliToJson(args),
                "dump_zen_from_game" => CliDumpZenFromGame(args),
                "clone_mod_iostore" => CliCloneModIoStore(args),
                "extract_pak" => CliExtractPak(args),
                "niagara_analyze" => CliNiagaraAnalyze(args),
                "niagara_details" => CliNiagaraDetails(args),
                "niagara_details_stream" => CliNiagaraDetailsStream(args),
                "niagara_edit" => CliNiagaraEdit(args),
                "niagara_edit_batch" => CliNiagaraEditBatch(args),
                "skeletal_mesh_info" => CliSkeletalMeshInfo(args),
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
        Console.WriteLine();
        Console.WriteLine("  Asset Inspection:");
        Console.WriteLine("    detect <uasset_path> [usmap_path]       - Detect asset type");
        Console.WriteLine("    batch_detect <directory> [usmap_path]   - Detect all assets in directory");
        Console.WriteLine("    dump <uasset_path> <usmap_path>         - Dump detailed asset info");
        Console.WriteLine("    skeletal_mesh_info <uasset> <usmap>     - Get SkeletalMesh material/bone info");
        Console.WriteLine();
        Console.WriteLine("  Asset Editing:");
        Console.WriteLine("    fix <uasset_path> [usmap_path]          - Fix SerializeSize for meshes");
        Console.WriteLine("    to_json <path> [usmap] [output_dir]     - Convert uasset to JSON (file or directory)");
        Console.WriteLine("    from_json <json> <output_uasset> [usmap] - Convert JSON back to uasset");
        Console.WriteLine();
        Console.WriteLine("  Mod Creation (Legacy -> IoStore):");
        Console.WriteLine("    create_mod_iostore <output> <inputs...>  - Convert legacy assets and create IoStore bundle");
        Console.WriteLine("    to_zen <uasset> [--no-material-tags] - Convert legacy to Zen format");
        Console.WriteLine("    create_pak <output.pak> <files...>       - Create encrypted PAK file");
        Console.WriteLine("    create_companion_pak <output.pak> <files...> - Create companion PAK for IoStore");
        Console.WriteLine("    create_iostore_bundle <output> <files...> - Create complete IoStore bundle");
        Console.WriteLine();
        Console.WriteLine("  Extraction:");
        Console.WriteLine("    extract_iostore_legacy <paks> <output> [options] - Extract IoStore to legacy format");
        Console.WriteLine("    extract_pak <pak_path> <output_dir> [options]    - Extract legacy PAK file");
        Console.WriteLine("    extract_script_objects <paks> <output>           - Extract ScriptObjects.bin");
        Console.WriteLine();
        Console.WriteLine("  IoStore Utilities:");
        Console.WriteLine("    inspect_zen <zen_asset_path>             - Inspect Zen package structure");
        Console.WriteLine("    is_iostore_compressed <utoc_path>        - Check if IoStore is compressed");
        Console.WriteLine("    recompress_iostore <utoc_path>           - Recompress IoStore with Oodle");
        Console.WriteLine("    cityhash <path_string>                   - Calculate CityHash64 for a path");
        Console.WriteLine("    clone_mod_iostore <utoc> <output>        - Clone/repackage a mod IoStore");
        Console.WriteLine();
        Console.WriteLine("  Other:");
        Console.WriteLine();
        Console.WriteLine("Interactive mode: Run without arguments to use JSON stdin/stdout");
        return 0;
    }
    
    private static int CliToZen(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: UAssetTool to_zen <uasset_path> [--no-material-tags]");
            return 1;
        }

        string uassetPath = args[1];
        
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--no-material-tags")
                ZenPackage.ZenConverter.SetMaterialTagsEnabled(false);
        }

        if (!File.Exists(uassetPath))
        {
            Console.Error.WriteLine($"File not found: {uassetPath}");
            return 1;
        }

        try
        {
            Console.Error.WriteLine($"[CliToZen] Converting {uassetPath} to Zen format...");
            
            byte[] zenData = ZenPackage.ZenConverter.ConvertLegacyToZen(uassetPath);
            
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
            Console.Error.WriteLine("Usage: UAssetTool create_mod_iostore <output_base> <input> [input2] ...");
            Console.Error.WriteLine("  Converts legacy assets to Zen format and creates IoStore bundle.");
            Console.Error.WriteLine("  Input can be .uasset files, directories, or .pak files.");
            Console.Error.WriteLine("  This is the complete pipeline for Marvel Rivals mod creation.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Options:");
            Console.Error.WriteLine("  --mount-point <path>  - Mount point (default: ../../../)");
            Console.Error.WriteLine("  --game-path <prefix>  - Game path prefix (default: Marvel/Content/)");
            Console.Error.WriteLine("  --compress            - Enable Oodle compression (default: enabled)");
            Console.Error.WriteLine("  --no-compress         - Disable compression");
            Console.Error.WriteLine("  --obfuscate           - Protect mod from extraction tools like FModel");
            Console.Error.WriteLine("  --pak-aes <hex>       - AES key for decrypting input .pak files");
            Console.Error.WriteLine("  --no-material-tags    - Disable MaterialTag injection (enabled by default)");
            return 1;
        }

        string outputBase = args[1];
        string mountPoint = "../../../";
        string gamePathPrefix = "Marvel/Content/";
        bool enableCompression = true;
        bool enableEncryption = false;
        string? aesKey = null;
        string? pakAesKey = null;
        // Marvel Rivals AES key for obfuscation
        const string MARVEL_RIVALS_AES_KEY = "0C263D8C22DCB085894899C3A3796383E9BF9DE0CBFB08C9BF2DEF2E84F29D74";
        var uassetFiles = new List<string>();
        var tempDirsToCleanup = new List<string>();

        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--no-material-tags")
            {
                ZenPackage.ZenConverter.SetMaterialTagsEnabled(false);
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
            else if (args[i] == "--obfuscate")
            {
                enableEncryption = true;
                aesKey = MARVEL_RIVALS_AES_KEY;
            }
            else if (args[i] == "--pak-aes" && i + 1 < args.Length)
            {
                pakAesKey = args[++i];
            }
            else if (args[i].EndsWith(".pak", StringComparison.OrdinalIgnoreCase) && File.Exists(args[i]))
            {
                // Extract legacy assets from .pak file to temp directory
                string pakInputPath = Path.GetFullPath(args[i]);
                string tempDir = Path.Combine(Path.GetTempPath(), "UAssetTool_pak_" + Path.GetFileNameWithoutExtension(pakInputPath) + "_" + Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(tempDir);
                tempDirsToCleanup.Add(tempDir);

                Console.Error.WriteLine($"[CreateModIoStore] Extracting legacy PAK: {pakInputPath}");
                try
                {
                    using var pakReader = new IoStore.PakReader(pakInputPath, pakAesKey);
                    int extracted = 0;
                    foreach (var file in pakReader.Files)
                    {
                        // Only extract .uasset, .uexp, and .ubulk files
                        string ext = Path.GetExtension(file).ToLowerInvariant();
                        if (ext != ".uasset" && ext != ".uexp" && ext != ".ubulk")
                            continue;

                        byte[] data = pakReader.Get(file);
                        string relativePath = file.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                        string outPath = Path.Combine(tempDir, relativePath);
                        string? dir = Path.GetDirectoryName(outPath);
                        if (!string.IsNullOrEmpty(dir))
                            Directory.CreateDirectory(dir);
                        File.WriteAllBytes(outPath, data);
                        extracted++;
                    }
                    Console.Error.WriteLine($"[CreateModIoStore]   Extracted {extracted} files from PAK");

                    // Find all .uasset files in the extracted directory
                    var dirFiles = Directory.GetFiles(tempDir, "*.uasset", SearchOption.AllDirectories);
                    foreach (var f in dirFiles)
                    {
                        uassetFiles.Add(Path.GetFullPath(f));
                    }
                    Console.Error.WriteLine($"[CreateModIoStore]   Found {dirFiles.Length} .uasset files");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error extracting PAK: {ex.Message}");
                }
            }
            else if (args[i].EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) && File.Exists(args[i]))
            {
                uassetFiles.Add(Path.GetFullPath(args[i]));
            }
            else if (Directory.Exists(args[i]))
            {
                // Support directory input - recursively find all .uasset files
                var dirFiles = Directory.GetFiles(args[i], "*.uasset", SearchOption.AllDirectories);
                foreach (var f in dirFiles)
                {
                    uassetFiles.Add(Path.GetFullPath(f));
                }
                Console.Error.WriteLine($"Found {dirFiles.Length} .uasset files in directory: {args[i]}");
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
            Console.Error.WriteLine($"[CreateModIoStore]   Protection: {(enableEncryption ? "Obfuscated (FModel-proof)" : "None")}");

            // Phase 1: Parallel conversion to Zen format (CPU-intensive)
            // This is the bottleneck - UAsset parsing, NameMap building, export reordering, hashing
            int threadCount = Math.Max(1, (Environment.ProcessorCount * 3) / 4); // 75% of cores
            Console.Error.WriteLine($"[CreateModIoStore]   Threads: {threadCount}");

            var conversionResults = new System.Collections.Concurrent.ConcurrentBag<(string assetName, string uassetPath, byte[]? zenData, string packagePath, ZenPackage.FZenPackage? zenPackage, byte[]? ubulkData, string? error)>();
            int processedCount = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            Parallel.ForEach(uassetFiles, new ParallelOptions { MaxDegreeOfParallelism = threadCount }, uassetPath =>
            {
                string assetName = Path.GetFileNameWithoutExtension(uassetPath);
                try
                {
                    byte[] zenData;
                    string packagePath;
                    ZenPackage.FZenPackage zenPackage;

                    (zenData, packagePath, zenPackage) = ZenPackage.ZenConverter.ConvertLegacyToZenFull(
                        uassetPath, containerVersion: ZenPackage.EIoContainerHeaderVersion.NoExportInfo);

                    byte[]? ubulkData = null;
                    string ubulkPath = Path.ChangeExtension(uassetPath, ".ubulk");
                    if (File.Exists(ubulkPath)) ubulkData = File.ReadAllBytes(ubulkPath);

                    conversionResults.Add((assetName, uassetPath, zenData, packagePath, zenPackage, ubulkData, null));

                    int count = Interlocked.Increment(ref processedCount);
                    if (count % 50 == 0 || count == uassetFiles.Count)
                    {
                        Console.Error.WriteLine($"[CreateModIoStore] Converted {count}/{uassetFiles.Count}...");
                    }
                }
                catch (Exception ex)
                {
                    conversionResults.Add((assetName, uassetPath, null, "", null, null, ex.Message));
                    Interlocked.Increment(ref processedCount);
                }
            });

            Console.Error.WriteLine($"[CreateModIoStore] Parallel conversion done in {sw.Elapsed.TotalSeconds:F1}s. Writing IoStore...");

            // Phase 2: Sequential write to IoStore (fast I/O, not parallelizable)
            using var ioStoreWriter = new IoStore.IoStoreWriter(
                utocPath,
                IoStore.EIoStoreTocVersion.PerfectHashWithOverflow,
                IoStore.EIoContainerHeaderVersion.NoExportInfo,
                mountPoint,
                enableCompression,
                enableEncryption,
                aesKey);

            var filePaths = new List<string>();
            int convertedCount = 0;
            var errors = new List<string>();

            foreach (var result in conversionResults)
            {
                if (result.error != null || result.zenData == null)
                {
                    if (result.error != null)
                    {
                        errors.Add($"{result.assetName}: {result.error}");
                        Console.Error.WriteLine($"  ERROR: {result.assetName}: {result.error}");
                    }
                    continue;
                }

                try
                {
                    var (assetName, uassetPath, zenData, packagePath, zenPackage, ubulkData, _) = result;

                    // Create package ID using the /Game/... format
                    string gamePackagePath;
                    if (packagePath.StartsWith("Marvel/Content/"))
                    {
                        gamePackagePath = "/Game/" + packagePath.Substring("Marvel/Content/".Length);
                    }
                    else
                    {
                        gamePackagePath = "/" + packagePath;
                    }

                    var packageId = IoStore.FPackageId.FromName(gamePackagePath);
                    var chunkId = IoStore.FIoChunkId.FromPackageId(packageId, 0, IoStore.EIoChunkType.ExportBundleData);

                    // Create store entry with imported packages from the Zen package
                    var storeEntry = new IoStore.StoreEntry
                    {
                        ExportCount = zenPackage.ExportMap.Count,
                        ExportBundleCount = 1,
                        LoadOrder = 0
                    };

                    foreach (ulong importedPkgId in zenPackage.ImportedPackages)
                    {
                        storeEntry.ImportedPackages.Add(new IoStore.FPackageId(importedPkgId));
                    }

                    // Write to IoStore
                    string fullPath = mountPoint + packagePath + ".uasset";
                    ioStoreWriter.WritePackageChunk(chunkId, fullPath, zenData, storeEntry);

                    filePaths.Add(packagePath + ".uasset");
                    filePaths.Add(packagePath + ".uexp");

                    // Handle .ubulk if exists (already loaded during parallel phase)
                    if (ubulkData != null)
                    {
                        var bulkChunkId = IoStore.FIoChunkId.FromPackageId(packageId, 0, IoStore.EIoChunkType.BulkData);
                        string bulkFullPath = mountPoint + packagePath + ".ubulk";
                        ioStoreWriter.WriteChunk(bulkChunkId, bulkFullPath, ubulkData);
                        filePaths.Add(packagePath + ".ubulk");
                    }

                    convertedCount++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{result.assetName}: {ex.Message}");
                    Console.Error.WriteLine($"  ERROR writing {result.assetName}: {ex.Message}");
                }
            }

            if (filePaths.Count == 0)
            {
                Console.Error.WriteLine("Error: No assets were successfully converted");
                if (errors.Count > 0)
                    Console.Error.WriteLine($"Errors: {string.Join("; ", errors)}");
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
        finally
        {
            // Clean up temp directories created from .pak extraction
            foreach (var tempDir in tempDirsToCleanup)
            {
                try
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, recursive: true);
                        Console.Error.WriteLine($"[CreateModIoStore] Cleaned up temp directory: {tempDir}");
                    }
                }
                catch { /* Best effort cleanup */ }
            }
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
    /// Check if an IoStore container is encrypted (obfuscated).
    /// </summary>
    private static int CliIsIoStoreEncrypted(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: UAssetTool is_iostore_encrypted <utoc_path>");
            return 1;
        }

        string utocPath = args[1];
        
        try
        {
            bool isEncrypted = IoStore.IoStoreReader.IsEncrypted(utocPath);
            Console.WriteLine(isEncrypted ? "true" : "false");
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
            Console.Error.WriteLine("  --aes <hex>              AES key for decryption (can specify multiple times)");
            Console.Error.WriteLine("  --filter <patterns...>   Only extract packages matching patterns (space-separated)");
            Console.Error.WriteLine("  --with-deps              Also extract imported/referenced packages");
            Console.Error.WriteLine("  --mod <path>             Path to modded .utoc file or directory containing .utoc files.");
            Console.Error.WriteLine("                           Extracts from mod containers, uses game paks for import resolution.");
            Console.Error.WriteLine("                           If path is a .utoc file, loads that single bundle.");
            Console.Error.WriteLine("                           If path is a directory, loads all .utoc files in it.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Examples:");
            Console.Error.WriteLine("  extract_iostore_legacy \"C:/Game/Paks\" output --filter SK_1014 SK_1057 SK_1036");
            Console.Error.WriteLine("  extract_iostore_legacy \"C:/Game/Paks\" output --filter Characters/1014 Characters/1057");
            Console.Error.WriteLine("  extract_iostore_legacy \"C:/Game/Paks\" output --mod \"C:/Mods/my_mod.utoc\" --filter SK_1014");
            Console.Error.WriteLine("  extract_iostore_legacy \"C:/Game/Paks\" output --mod \"C:/Mods/\" --with-deps");
            return 1;
        }

        string paksPath = args[1];
        string outputDir = args[2];
        string? scriptObjectsPath = null;
        string? globalUtocPath = null;
        List<string> additionalContainers = new();
        List<string> filterPatterns = new();
        List<string> modPaths = new(); // Mod utoc files or directories
        List<string> aesKeys = new();
        bool extractDependencies = false;

        for (int i = 3; i < args.Length; i++)
        {
            if ((args[i] == "--aes" || args[i] == "--aes-key") && i + 1 < args.Length)
                aesKeys.Add(args[++i]);
            else if (args[i] == "--script-objects" && i + 1 < args.Length)
                scriptObjectsPath = args[++i];
            else if (args[i] == "--global" && i + 1 < args.Length)
                globalUtocPath = args[++i];
            else if (args[i] == "--container" && i + 1 < args.Length)
                additionalContainers.Add(args[++i]);
            else if (args[i] == "--mod")
            {
                // Collect all following args until next option (starts with --)
                while (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                {
                    modPaths.Add(args[++i]);
                }
            }
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
            
            // Set AES keys - use provided keys or default Marvel Rivals key
            if (aesKeys.Count > 0)
            {
                foreach (var key in aesKeys)
                    context.AddAesKey(key);
            }
            else
            {
                context.SetAesKey("0C263D8C22DCB085894899C3A3796383E9BF9DE0CBFB08C9BF2DEF2E84F29D74");
            }
            
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
            
            // Track mod container indices for extraction source filtering
            HashSet<int> modContainerIndices = new();
            
            // Load mod containers with priority (they override game packages)
            if (modPaths.Count > 0)
            {
                Console.WriteLine($"\nLoading mod containers...");
                foreach (var modPath in modPaths)
                {
                    if (modPath.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase) && File.Exists(modPath))
                    {
                        // Single utoc file specified
                        int containerIdx = context.ContainerCount;
                        context.LoadContainerWithPriority(modPath);
                        modContainerIndices.Add(containerIdx);
                    }
                    else if (Directory.Exists(modPath))
                    {
                        // Directory - load all utoc files in it
                        var modUtocFiles = Directory.GetFiles(modPath, "*.utoc", SearchOption.TopDirectoryOnly);
                        if (modUtocFiles.Length == 0)
                        {
                            Console.Error.WriteLine($"Warning: No .utoc files found in mod directory: {modPath}");
                        }
                        foreach (var modUtoc in modUtocFiles)
                        {
                            try
                            {
                                int containerIdx = context.ContainerCount;
                                context.LoadContainerWithPriority(modUtoc);
                                modContainerIndices.Add(containerIdx);
                            }
                            catch (Exception ex)
                            {
                                Console.Error.WriteLine($"  Warning: Failed to load mod {Path.GetFileName(modUtoc)}: {ex.Message}");
                            }
                        }
                    }
                    else
                    {
                        Console.Error.WriteLine($"Warning: Mod path not found: {modPath}");
                    }
                }
                Console.WriteLine($"Loaded {modContainerIndices.Count} mod container(s)");
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
            bool extractFromModOnly = modContainerIndices.Count > 0; // When mods are specified, extract from mods
            
            if (extractFromModOnly && filterPatterns.Count == 0)
            {
                // No filter specified but mods are loaded - extract all packages from mod containers
                packageIds = new List<ulong>();
                foreach (var containerIdx in modContainerIndices)
                {
                    foreach (var pkgId in context.GetPackageIdsFromContainer(containerIdx))
                    {
                        if (!packageIds.Contains(pkgId))
                            packageIds.Add(pkgId);
                    }
                }
                skipFilterCheck = true;
                Console.WriteLine($"Extracting all {packageIds.Count} packages from mod container(s)");
            }
            else if (filterPatterns.Count > 0)
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
                    // Normalize path (handle /../ patterns like /Game/Marvel/../../../Marvel/Content/...)
                    string relPath = packageName;
                    
                    // First resolve any /../ patterns by using Path.GetFullPath with a fake root
                    if (relPath.Contains("/../"))
                    {
                        // Use a temp root to resolve relative segments, then extract the result
                        string tempRoot = Path.GetTempPath();
                        string tempPath = Path.Combine(tempRoot, relPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                        string resolved = Path.GetFullPath(tempPath);
                        relPath = resolved.Substring(tempRoot.Length).Replace(Path.DirectorySeparatorChar, '/');
                        if (!relPath.StartsWith("/"))
                            relPath = "/" + relPath;
                        // The resolved path is like /Marvel/Content/Marvel/Characters/...
                        // /Marvel/Content is the game root (= /Game), so extract everything after it
                        // Result should be /Game/Marvel/Characters/...
                        int contentIdx = relPath.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
                        if (contentIdx >= 0)
                            relPath = "/Game" + relPath.Substring(contentIdx + "/Content".Length);
                    }
                    
                    // Map /Game/ back to Marvel/Content/ to match the on-disk path structure
                    // /Game/Marvel/VFX/... -> Marvel/Content/Marvel/VFX/...
                    if (relPath.StartsWith("/Game/"))
                        relPath = "Marvel/Content/" + relPath.Substring(6);
                    else if (relPath.StartsWith("/"))
                        relPath = relPath.Substring(1); // Remove leading slash
                    
                    relPath = relPath.Replace('/', Path.DirectorySeparatorChar);
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
        
        // Set FilePath so FindAssetOnDiskFromPath can locate sibling assets for schema resolution
        asset.FilePath = Path.GetFullPath(outputPath);
        
        // Preload schemas from referenced assets (parent BPs etc.) - same as CliToJson
        PreloadReferencedAssetsForSchemas(asset);
        
        // Ensure output directory exists
        string? outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);
        
        asset.Write(outputPath);
        
        Console.WriteLine($"Asset imported from JSON and saved to {outputPath}");
        return 0;
    }
    
    private static int CliToJson(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: UAssetTool to_json <path> [usmap_path] [output_dir]");
            Console.Error.WriteLine("  <path>       - Path to a .uasset file or directory containing .uasset files");
            Console.Error.WriteLine("  [usmap_path] - Optional path to .usmap mappings file");
            Console.Error.WriteLine("  [output_dir] - Optional output directory (default: same as input)");
            return 1;
        }

        string inputPath = args[1];
        string? usmapPath = args.Length > 2 ? args[2] : null;
        string? outputDir = args.Length > 3 ? args[3] : null;

        int successCount = 0;
        int failCount = 0;

        if (Directory.Exists(inputPath))
        {
            // Batch mode: process all .uasset files in directory
            var files = Directory.GetFiles(inputPath, "*.uasset", SearchOption.AllDirectories);
            Console.WriteLine($"Found {files.Length} .uasset files in {inputPath}");

            foreach (var file in files)
            {
                try
                {
                    string jsonOutputPath;
                    if (!string.IsNullOrEmpty(outputDir))
                    {
                        // Preserve relative directory structure in output
                        string relativePath = Path.GetRelativePath(inputPath, file);
                        string relativeDir = Path.GetDirectoryName(relativePath) ?? "";
                        string outputSubDir = Path.Combine(outputDir, relativeDir);
                        Directory.CreateDirectory(outputSubDir);
                        jsonOutputPath = Path.Combine(outputSubDir, Path.GetFileNameWithoutExtension(file) + ".json");
                    }
                    else
                    {
                        jsonOutputPath = Path.ChangeExtension(file, ".json");
                    }

                    var asset = LoadAsset(file, usmapPath);
                    PreloadReferencedAssetsForSchemas(asset);
                    string json = asset.SerializeJson(true);
                    File.WriteAllText(jsonOutputPath, json, System.Text.Encoding.UTF8);
                    Console.WriteLine($"Converted: {file} -> {jsonOutputPath}");
                    successCount++;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Failed to convert {file}: {ex.Message}");
                    failCount++;
                }
            }

            Console.WriteLine($"\nBatch conversion complete: {successCount} succeeded, {failCount} failed");
        }
        else if (File.Exists(inputPath))
        {
            // Single file mode
            try
            {
                string jsonOutputPath;
                if (!string.IsNullOrEmpty(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                    jsonOutputPath = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(inputPath) + ".json");
                }
                else
                {
                    jsonOutputPath = Path.ChangeExtension(inputPath, ".json");
                }

                var asset = LoadAsset(inputPath, usmapPath);
                PreloadReferencedAssetsForSchemas(asset);
                string json = asset.SerializeJson(true);
                File.WriteAllText(jsonOutputPath, json, System.Text.Encoding.UTF8);
                Console.WriteLine($"Asset exported to JSON: {jsonOutputPath}");
                successCount = 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to convert: {ex.Message}");
                return 1;
            }
        }
        else
        {
            Console.Error.WriteLine($"Path not found: {inputPath}");
            return 1;
        }

        return failCount > 0 ? 1 : 0;
    }
    
    private static int CliNiagaraEdit(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: UAssetTool niagara_edit <path> <usmap_path> <r> <g> <b> [a] [options]");
            Console.Error.WriteLine("  Edits ShaderLUT color curves in NiagaraSystem assets.");
            Console.Error.WriteLine("  If <path> is a directory, batch-processes all NS_*.uasset files.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Single file options:");
            Console.Error.WriteLine("  --export <index>    Only edit a specific export by index");
            Console.Error.WriteLine("  --channels <rgba>   Which channels to modify (default: rgb)");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Batch directory options (alternating player/enemy colors):");
            Console.Error.WriteLine("  --player <r> <g> <b> [a]  Player color (odd ColorCurve exports)");
            Console.Error.WriteLine("  --enemy  <r> <g> <b> [a]  Enemy color (even ColorCurve exports)");
            Console.Error.WriteLine("  --output <dir>             Write modified files to output dir (preserves originals)");
            return 1;
        }

        // --- JSON mode: if args[1] starts with '{', parse as JSON request from Tauri frontend ---
        if (args[1].TrimStart().StartsWith("{"))
        {
            return CliNiagaraEditJson(args);
        }

        string assetPath = args[1];
        string? usmapPath = null;
        float r = 0, g = 0, b = 0, a = 1;
        int? targetExport = null;
        string channels = "rgb";
        bool hasColor = false;

        // Batch mode colors
        float pR = 0, pG = 5, pB = 0, pA = 1; // player green
        float eR = 0, eG = 0, eB = 5, eA = 1; // enemy dark blue
        bool hasPlayer = false, hasEnemy = false;
        string? outputDir = null;

        // Parse args
        var positional = new List<string>();
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--export" && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out int idx)) targetExport = idx;
            }
            else if (args[i] == "--channels" && i + 1 < args.Length)
            {
                channels = args[++i].ToLowerInvariant();
            }
            else if (args[i] == "--usmap" && i + 1 < args.Length)
            {
                usmapPath = args[++i];
            }
            else if (args[i] == "--output" && i + 1 < args.Length)
            {
                outputDir = args[++i];
            }
            else if (args[i] == "--player" && i + 3 < args.Length)
            {
                float.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out pR);
                float.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out pG);
                float.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out pB);
                hasPlayer = true;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--") && float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float pa))
                { pA = pa; i++; }
            }
            else if (args[i] == "--enemy" && i + 3 < args.Length)
            {
                float.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out eR);
                float.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out eG);
                float.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out eB);
                hasEnemy = true;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--") && float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float ea))
                { eA = ea; i++; }
            }
            else
            {
                positional.Add(args[i]);
            }
        }

        // Directory batch mode — full parse, one file at a time (correct + low RAM)
        // Same proven path as single-file edit, just in a loop with cleanup
        if (Directory.Exists(assetPath))
        {
            if (!hasPlayer && !hasEnemy)
            {
                Console.Error.WriteLine("Batch mode: using default Player=green(0,5,0) Enemy=blue(0,0,5)");
                hasPlayer = true; hasEnemy = true;
            }

            // Parse usmap from positional if present
            if (positional.Count >= 1 && usmapPath == null) usmapPath = positional[0];

            var nsFiles = Directory.GetFiles(assetPath, "NS_*.uasset", SearchOption.AllDirectories);
            Console.Error.WriteLine($"Found {nsFiles.Length} NS_*.uasset files");

            // Try all NS files — binary pre-filter is unreliable across different extraction tools
            var candidates = nsFiles.ToList();
            Console.Error.WriteLine($"  Processing all {candidates.Count} candidates");

            // Load usmap once — shared across all files (read-only after load)
            Usmap? mappings = LoadMappings(usmapPath);

            int totalModifiedFiles = 0;
            int totalPatchedLUTs = 0;
            int totalErrors = 0;
            int totalSkipped = 0;

            for (int fi = 0; fi < candidates.Count; fi++)
            {
                string file = candidates[fi];
                string name = Path.GetFileNameWithoutExtension(file);
                try
                {
                    // Full parse with usmap — exact same path as single-file edit
                    var batchAsset = LoadAssetWithMappings(file, mappings);

                    int colorCurveIdx = 0;
                    bool modified = false;

                    for (int ei = 0; ei < batchAsset.Exports.Count; ei++)
                    {
                        string cls = batchAsset.Exports[ei].GetExportClassType()?.Value?.Value ?? "";
                        if (cls != "NiagaraDataInterfaceColorCurve") continue;
                        if (batchAsset.Exports[ei] is not NormalExport ne || ne.Data == null) { colorCurveIdx++; continue; }

                        // Alternate: even index = player, odd = enemy
                        float cr, cg, cb, ca;
                        if (colorCurveIdx % 2 == 0) { cr = pR; cg = pG; cb = pB; ca = pA; }
                        else { cr = eR; cg = eG; cb = eB; ca = eA; }

                        foreach (var prop in ne.Data)
                        {
                            if (prop.Name?.Value?.Value != "ShaderLUT") continue;
                            if (prop is not ArrayPropertyData arrayProp || arrayProp.Value == null) continue;

                            for (int j = 0; j + 3 < arrayProp.Value.Length; j += 4)
                            {
                                if (arrayProp.Value[j] is FloatPropertyData rp) rp.Value = cr;
                                if (arrayProp.Value[j + 1] is FloatPropertyData gp) gp.Value = cg;
                                if (arrayProp.Value[j + 2] is FloatPropertyData bp) bp.Value = cb;
                                if (arrayProp.Value[j + 3] is FloatPropertyData ap) ap.Value = ca;
                            }
                            modified = true;
                            totalPatchedLUTs++;
                            break;
                        }

                        // Also patch RGBA float quads in Extras (binary curve data the game reads at runtime)
                        if (ne.Extras != null && ne.Extras.Length >= 16)
                        {
                            byte[] extras = ne.Extras;
                            int extrasPatched = 0;
                            for (int eb = 0; eb + 15 < extras.Length; eb += 4)
                            {
                                float fA = BitConverter.ToSingle(extras, eb + 12);
                                if (fA < 0.99f || fA > 1.01f) continue;
                                float fR = BitConverter.ToSingle(extras, eb);
                                float fG = BitConverter.ToSingle(extras, eb + 4);
                                float fB = BitConverter.ToSingle(extras, eb + 8);
                                if (fR < 0 || fR > 100 || fG < 0 || fG > 100 || fB < 0 || fB > 100) continue;
                                if (fR == 0 && fG == 0 && fB == 0) continue;
                                Array.Copy(BitConverter.GetBytes(cr), 0, extras, eb, 4);
                                Array.Copy(BitConverter.GetBytes(cg), 0, extras, eb + 4, 4);
                                Array.Copy(BitConverter.GetBytes(cb), 0, extras, eb + 8, 4);
                                Array.Copy(BitConverter.GetBytes(ca), 0, extras, eb + 12, 4);
                                extrasPatched++;
                                eb += 12;
                            }
                            if (extrasPatched > 0)
                                Console.Error.WriteLine($"    Export {ei}: patched {extrasPatched} RGBA quads in Extras");
                        }
                        colorCurveIdx++;
                    }

                    if (modified)
                    {
                        string writePath;
                        if (outputDir != null)
                        {
                            string relPath = Path.GetRelativePath(assetPath, file);
                            string outPath = Path.Combine(outputDir, relPath);
                            string? outSubDir = Path.GetDirectoryName(outPath);
                            if (outSubDir != null) Directory.CreateDirectory(outSubDir);
                            string srcUexp = Path.ChangeExtension(file, ".uexp");
                            string dstUexp = Path.ChangeExtension(outPath, ".uexp");
                            if (File.Exists(srcUexp)) File.Copy(srcUexp, dstUexp, true);
                            File.Copy(file, outPath, true);
                            batchAsset.Write(outPath);
                            writePath = outPath;
                        }
                        else
                        {
                            batchAsset.Write(batchAsset.FilePath);
                            writePath = batchAsset.FilePath;
                        }

                        // Second pass: binary patch the written .uexp to replace RGBA quads
                        // in Extras areas (curve data the game reads at runtime to regenerate ShaderLUT)
                        string uexpPath = Path.ChangeExtension(writePath, ".uexp");
                        if (File.Exists(uexpPath))
                        {
                            byte[] uexpBytes = File.ReadAllBytes(uexpPath);
                            int totalExtrasPatched = 0;
                            for (int bp = 0; bp + 15 < uexpBytes.Length; bp += 4)
                            {
                                float fA = BitConverter.ToSingle(uexpBytes, bp + 12);
                                if (fA < 0.99f || fA > 1.01f) continue;
                                float fR = BitConverter.ToSingle(uexpBytes, bp);
                                float fG = BitConverter.ToSingle(uexpBytes, bp + 4);
                                float fB = BitConverter.ToSingle(uexpBytes, bp + 8);
                                if (fR < 0 || fR > 100 || fG < 0 || fG > 100 || fB < 0 || fB > 100) continue;
                                if (fR == 0 && fG == 0 && fB == 0) continue;
                                // Already patched by Write() (ShaderLUT area) — skip if already our target color
                                bool alreadyPatched = false;
                                // Check against both player and enemy colors
                                if ((fR == pR && fG == pG && fB == pB) || (fR == eR && fG == eG && fB == eB))
                                    alreadyPatched = true;
                                if (alreadyPatched) { bp += 12; continue; }
                                // Determine which color to use based on position in the file
                                // We can't easily tell which export this belongs to, so use a simple heuristic:
                                // first half of RGBA quads = player, second half = enemy (matches alternating pattern)
                                // Actually just replace ALL with the same alternating logic isn't possible here.
                                // Instead, just replace with the nearest color based on the original value pattern.
                                // For now: replace all non-patched RGBA quads where R==G==B (grey/white) with player color
                                // and where R!=G or G!=B with enemy color (heuristic, may need refinement)
                                float useR, useG, useB, useA;
                                if (Math.Abs(fR - fG) < 0.01f && Math.Abs(fG - fB) < 0.01f)
                                {
                                    // Grey/white — use player color
                                    useR = pR; useG = pG; useB = pB; useA = pA;
                                }
                                else
                                {
                                    // Non-grey — use enemy color
                                    useR = eR; useG = eG; useB = eB; useA = eA;
                                }
                                Array.Copy(BitConverter.GetBytes(useR), 0, uexpBytes, bp, 4);
                                Array.Copy(BitConverter.GetBytes(useG), 0, uexpBytes, bp + 4, 4);
                                Array.Copy(BitConverter.GetBytes(useB), 0, uexpBytes, bp + 8, 4);
                                Array.Copy(BitConverter.GetBytes(useA), 0, uexpBytes, bp + 12, 4);
                                totalExtrasPatched++;
                                bp += 12;
                            }
                            if (totalExtrasPatched > 0)
                            {
                                File.WriteAllBytes(uexpPath, uexpBytes);
                                Console.Error.WriteLine($"    {name}: binary-patched {totalExtrasPatched} RGBA quads in .uexp");
                            }
                        }
                        totalModifiedFiles++;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  ERROR {name}: {ex.Message}");
                    totalErrors++;
                }

                // Aggressive cleanup every 10 files to keep RAM under control
                if ((fi + 1) % 10 == 0)
                    GC.Collect(2, GCCollectionMode.Forced, true, true);

                if ((fi + 1) % 50 == 0)
                    Console.Error.WriteLine($"  Processed {fi + 1}/{candidates.Count} ({totalModifiedFiles} modified, {totalErrors} errors)...");
            }

            Console.Error.WriteLine($"  Done: {candidates.Count} files, {totalModifiedFiles} modified, {totalPatchedLUTs} LUTs patched, {totalSkipped} skipped, {totalErrors} errors");
            var batchResult = new { success = true, totalFiles = candidates.Count, totalModifiedFiles, totalPatchedLUTs, totalSkipped, totalErrors };
            Console.WriteLine(JsonSerializer.Serialize(batchResult, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        // Single file mode
        // positional: [usmap] r g b [a]
        if (positional.Count >= 4)
        {
            usmapPath = positional[0];
            float.TryParse(positional[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out r);
            float.TryParse(positional[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out g);
            float.TryParse(positional[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out b);
            hasColor = true;
            if (positional.Count >= 5)
                float.TryParse(positional[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out a);
        }
        else if (positional.Count >= 3)
        {
            float.TryParse(positional[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out r);
            float.TryParse(positional[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out g);
            float.TryParse(positional[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out b);
            hasColor = true;
            if (positional.Count >= 4)
                float.TryParse(positional[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out a);
        }

        if (!hasColor)
        {
            Console.Error.WriteLine("Error: Must provide R G B color values");
            return 1;
        }

        bool modR = channels.Contains('r');
        bool modG = channels.Contains('g');
        bool modB = channels.Contains('b');
        bool modA = channels.Contains('a');

        var asset = LoadAsset(assetPath, usmapPath);

        int modifiedColors = 0;
        int modifiedExports = 0;

        for (int ei = 0; ei < asset.Exports.Count; ei++)
        {
            if (targetExport.HasValue && targetExport.Value != ei) continue;

            var export = asset.Exports[ei];
            string className = export.GetExportClassType()?.Value?.Value ?? "";
            if (!className.Contains("ColorCurve")) continue;

            if (export is not NormalExport normalExport || normalExport.Data == null) continue;

            // Find ShaderLUT array property
            foreach (var prop in normalExport.Data)
            {
                if (prop.Name?.Value?.Value != "ShaderLUT") continue;
                if (prop is not ArrayPropertyData arrayProp || arrayProp.Value == null) continue;

                // Modify RGBA groups (every 4 floats)
                for (int j = 0; j + 3 < arrayProp.Value.Length; j += 4)
                {
                    if (arrayProp.Value[j] is FloatPropertyData rp && modR) rp.Value = r;
                    if (arrayProp.Value[j + 1] is FloatPropertyData gp && modG) gp.Value = g;
                    if (arrayProp.Value[j + 2] is FloatPropertyData bp && modB) bp.Value = b;
                    if (arrayProp.Value[j + 3] is FloatPropertyData ap && modA) ap.Value = a;
                    modifiedColors++;
                }

                modifiedExports++;
                break;
            }

            // Also patch RGBA float quads in Extras (binary curve data the game reads at runtime)
            if (normalExport.Extras != null && normalExport.Extras.Length >= 16)
            {
                byte[] extras = normalExport.Extras;
                int extrasPatched = 0;
                for (int eb = 0; eb + 15 < extras.Length; eb += 4)
                {
                    float fA = BitConverter.ToSingle(extras, eb + 12);
                    if (fA < 0.99f || fA > 1.01f) continue;
                    float fR = BitConverter.ToSingle(extras, eb);
                    float fG = BitConverter.ToSingle(extras, eb + 4);
                    float fB = BitConverter.ToSingle(extras, eb + 8);
                    if (fR < 0 || fR > 100 || fG < 0 || fG > 100 || fB < 0 || fB > 100) continue;
                    if (fR == 0 && fG == 0 && fB == 0) continue;
                    if (modR) Array.Copy(BitConverter.GetBytes(r), 0, extras, eb, 4);
                    if (modG) Array.Copy(BitConverter.GetBytes(g), 0, extras, eb + 4, 4);
                    if (modB) Array.Copy(BitConverter.GetBytes(b), 0, extras, eb + 8, 4);
                    if (modA) Array.Copy(BitConverter.GetBytes(a), 0, extras, eb + 12, 4);
                    extrasPatched++;
                    eb += 12;
                }
                if (extrasPatched > 0)
                    Console.Error.WriteLine($"  Export {ei}: patched {extrasPatched} RGBA quads in Extras");
            }
        }

        if (modifiedColors > 0)
        {
            asset.Write(asset.FilePath);
        }

        var result = new { success = true, path = assetPath, modifiedExports, modifiedColors };
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    /// <summary>
    /// JSON-mode niagara_edit: parses a JSON request object from the Tauri frontend.
    /// Supports batch colors (per-index RGBA) and overwrite mode (flat fill).
    /// </summary>
    private static int CliNiagaraEditJson(string[] args)
    {
        string json = args[1];
        // Support @filepath: read JSON from file instead of CLI arg (avoids OS error 206)
        if (json.StartsWith("@")) { json = File.ReadAllText(json.Substring(1)); }
        string? usmapPath = (args.Length >= 3 && !args[2].StartsWith("{") && !args[2].StartsWith("@")) ? args[2] : null;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (Exception ex) { Console.Error.WriteLine($"Error: Invalid JSON: {ex.Message}"); return 1; }

        var root = doc.RootElement;

        string assetPath = root.GetProperty("assetPath").GetString() ?? "";
        if (string.IsNullOrEmpty(assetPath)) { Console.Error.WriteLine("Error: assetPath is required"); return 1; }

        int? exportIndex = root.TryGetProperty("exportIndex", out var eiProp) && eiProp.ValueKind == JsonValueKind.Number ? eiProp.GetInt32() : null;
        string? exportNameFilter = root.TryGetProperty("exportNameFilter", out var enfProp) && enfProp.ValueKind == JsonValueKind.String ? enfProp.GetString() : null;

        bool modR = root.TryGetProperty("modifyR", out var mr) && mr.ValueKind == JsonValueKind.True;
        bool modG = root.TryGetProperty("modifyG", out var mg) && mg.ValueKind == JsonValueKind.True;
        bool modB = root.TryGetProperty("modifyB", out var mb) && mb.ValueKind == JsonValueKind.True;
        bool modA = root.TryGetProperty("modifyA", out var ma) && ma.ValueKind == JsonValueKind.True;

        // Batch colors array (per-index RGBA)
        List<(int index, float r, float g, float b, float a)>? batchColors = null;
        if (root.TryGetProperty("colors", out var colorsProp) && colorsProp.ValueKind == JsonValueKind.Array)
        {
            batchColors = new();
            foreach (var c in colorsProp.EnumerateArray())
            {
                int idx = c.GetProperty("index").GetInt32();
                float cr = (float)c.GetProperty("r").GetDouble();
                float cg = (float)c.GetProperty("g").GetDouble();
                float cb = (float)c.GetProperty("b").GetDouble();
                float ca = (float)c.GetProperty("a").GetDouble();
                batchColors.Add((idx, cr, cg, cb, ca));
            }
        }

        // Overwrite mode (flat fill with range)
        float fillR = 0, fillG = 0, fillB = 0, fillA = 1;
        bool hasOverwrite = false;
        int colorIndexStart = 0, colorIndexEnd = int.MaxValue;
        if (root.TryGetProperty("r", out var rProp) && rProp.ValueKind == JsonValueKind.Number && batchColors == null)
        {
            fillR = (float)rProp.GetDouble();
            fillG = root.TryGetProperty("g", out var gProp) ? (float)gProp.GetDouble() : 0;
            fillB = root.TryGetProperty("b", out var bProp) ? (float)bProp.GetDouble() : 0;
            fillA = root.TryGetProperty("a", out var aProp) ? (float)aProp.GetDouble() : 1;
            hasOverwrite = true;
        }
        if (root.TryGetProperty("colorIndexStart", out var cisProp)) colorIndexStart = cisProp.GetInt32();
        if (root.TryGetProperty("colorIndexEnd", out var cieProp)) colorIndexEnd = cieProp.GetInt32();

        if (batchColors == null && !hasOverwrite)
        {
            Console.Error.WriteLine("Error: Must provide either 'colors' array or 'r','g','b' values");
            return 1;
        }

        // Free memory before loading large assets to reduce OOM risk
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        UAsset asset;
        try { asset = LoadAsset(assetPath, usmapPath); }
        catch (OutOfMemoryException)
        {
            Console.Error.WriteLine($"Error: Out of memory loading asset: {assetPath}");
            return 1;
        }

        int modifiedColors = 0;
        int modifiedExports = 0;

        for (int ei = 0; ei < asset.Exports.Count; ei++)
        {
            if (exportIndex.HasValue && exportIndex.Value != ei) continue;

            var export = asset.Exports[ei];
            string exportName = export.ObjectName?.Value?.Value ?? "";
            string className = export.GetExportClassType()?.Value?.Value ?? "";

            // Filter by export name if specified
            if (exportNameFilter != null && !exportName.Equals(exportNameFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!className.Contains("ColorCurve") && !className.Contains("NiagaraDataInterface"))
                continue;

            if (export is not NormalExport normalExport || normalExport.Data == null) continue;

            foreach (var prop in normalExport.Data)
            {
                if (prop.Name?.Value?.Value != "ShaderLUT") continue;
                if (prop is not ArrayPropertyData arrayProp || arrayProp.Value == null) continue;

                int colorCount = arrayProp.Value.Length / 4;

                if (batchColors != null)
                {
                    // Batch mode: apply each color to its specific index
                    foreach (var (idx, cr, cg, cb, ca) in batchColors)
                    {
                        int j = idx * 4;
                        if (j + 3 >= arrayProp.Value.Length) continue;
                        if (modR && arrayProp.Value[j] is FloatPropertyData rp) rp.Value = cr;
                        if (modG && arrayProp.Value[j + 1] is FloatPropertyData gp) gp.Value = cg;
                        if (modB && arrayProp.Value[j + 2] is FloatPropertyData bp) bp.Value = cb;
                        if (modA && arrayProp.Value[j + 3] is FloatPropertyData ap) ap.Value = ca;
                        modifiedColors++;
                    }
                }
                else
                {
                    // Overwrite mode: fill range with a single color
                    for (int ci = colorIndexStart; ci < Math.Min(colorIndexEnd, colorCount); ci++)
                    {
                        int j = ci * 4;
                        if (j + 3 >= arrayProp.Value.Length) continue;
                        if (modR && arrayProp.Value[j] is FloatPropertyData rp) rp.Value = fillR;
                        if (modG && arrayProp.Value[j + 1] is FloatPropertyData gp) gp.Value = fillG;
                        if (modB && arrayProp.Value[j + 2] is FloatPropertyData bp) bp.Value = fillB;
                        if (modA && arrayProp.Value[j + 3] is FloatPropertyData ap) ap.Value = fillA;
                        modifiedColors++;
                    }
                }

                modifiedExports++;
                break;
            }

            // Also patch RGBA float quads in Extras (binary curve data the game reads at runtime)
            if (normalExport.Extras != null && normalExport.Extras.Length >= 16)
            {
                byte[] extras = normalExport.Extras;
                int extrasPatched = 0;

                if (batchColors != null && batchColors.Count > 0)
                {
                    // For batch mode, use the first color as the representative for Extras
                    var (_, er, eg, eb, ea) = batchColors[0];
                    for (int ebi = 0; ebi + 15 < extras.Length; ebi += 4)
                    {
                        float fA = BitConverter.ToSingle(extras, ebi + 12);
                        if (fA < 0.99f || fA > 1.01f) continue;
                        float fR = BitConverter.ToSingle(extras, ebi);
                        float fG = BitConverter.ToSingle(extras, ebi + 4);
                        float fB = BitConverter.ToSingle(extras, ebi + 8);
                        if (fR < 0 || fR > 100 || fG < 0 || fG > 100 || fB < 0 || fB > 100) continue;
                        if (fR == 0 && fG == 0 && fB == 0) continue;
                        if (modR) Array.Copy(BitConverter.GetBytes(er), 0, extras, ebi, 4);
                        if (modG) Array.Copy(BitConverter.GetBytes(eg), 0, extras, ebi + 4, 4);
                        if (modB) Array.Copy(BitConverter.GetBytes(eb), 0, extras, ebi + 8, 4);
                        if (modA) Array.Copy(BitConverter.GetBytes(ea), 0, extras, ebi + 12, 4);
                        extrasPatched++;
                        ebi += 12;
                    }
                }
                else if (hasOverwrite)
                {
                    for (int ebi = 0; ebi + 15 < extras.Length; ebi += 4)
                    {
                        float fA = BitConverter.ToSingle(extras, ebi + 12);
                        if (fA < 0.99f || fA > 1.01f) continue;
                        float fR = BitConverter.ToSingle(extras, ebi);
                        float fG = BitConverter.ToSingle(extras, ebi + 4);
                        float fB = BitConverter.ToSingle(extras, ebi + 8);
                        if (fR < 0 || fR > 100 || fG < 0 || fG > 100 || fB < 0 || fB > 100) continue;
                        if (fR == 0 && fG == 0 && fB == 0) continue;
                        if (modR) Array.Copy(BitConverter.GetBytes(fillR), 0, extras, ebi, 4);
                        if (modG) Array.Copy(BitConverter.GetBytes(fillG), 0, extras, ebi + 4, 4);
                        if (modB) Array.Copy(BitConverter.GetBytes(fillB), 0, extras, ebi + 8, 4);
                        if (modA) Array.Copy(BitConverter.GetBytes(fillA), 0, extras, ebi + 12, 4);
                        extrasPatched++;
                        ebi += 12;
                    }
                }

                if (extrasPatched > 0)
                    Console.Error.WriteLine($"  Export {ei}: patched {extrasPatched} RGBA quads in Extras");
            }
        }

        if (modifiedColors > 0)
        {
            asset.Write(asset.FilePath);
        }

        var result = new { success = true, path = assetPath, modifiedExports, modifiedColors };
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    /// <summary>
    /// Batch multi-export niagara_edit: loads the asset ONCE, applies edits to multiple exports,
    /// then writes ONCE. JSON format:
    /// { "assetPath": "...", "edits": [ { "exportIndex": N, "colors": [...], "modifyR": true, ... }, ... ] }
    /// This is safe for parallel cross-file saving since each file is only written once.
    /// </summary>
    private static int CliNiagaraEditBatch(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("Usage: UAssetTool niagara_edit_batch <json|@filepath> [usmap_path]"); return 1; }

        string json = args[1];
        // Support @filepath: read JSON from file instead of CLI arg (avoids OS error 206)
        if (json.StartsWith("@")) { json = File.ReadAllText(json.Substring(1)); }
        string? usmapPath = (args.Length >= 3 && !args[2].StartsWith("{") && !args[2].StartsWith("@")) ? args[2] : null;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (Exception ex) { Console.Error.WriteLine($"Error: Invalid JSON: {ex.Message}"); return 1; }

        var root = doc.RootElement;

        string assetPath = root.GetProperty("assetPath").GetString() ?? "";
        if (string.IsNullOrEmpty(assetPath)) { Console.Error.WriteLine("Error: assetPath is required"); return 1; }

        if (!root.TryGetProperty("edits", out var editsProp) || editsProp.ValueKind != JsonValueKind.Array)
        {
            Console.Error.WriteLine("Error: 'edits' array is required");
            return 1;
        }

        // Free memory before loading large assets to reduce OOM risk
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        UAsset asset;
        try { asset = LoadAsset(assetPath, usmapPath); }
        catch (OutOfMemoryException)
        {
            Console.Error.WriteLine($"Error: Out of memory loading asset: {assetPath}");
            return 1;
        }

        int totalModifiedColors = 0;
        int totalModifiedExports = 0;
        int editCount = 0;

        foreach (var edit in editsProp.EnumerateArray())
        {
            editCount++;
            int? exportIndex = edit.TryGetProperty("exportIndex", out var eiProp) && eiProp.ValueKind == JsonValueKind.Number ? eiProp.GetInt32() : null;
            string? exportNameFilter = edit.TryGetProperty("exportNameFilter", out var enfProp) && enfProp.ValueKind == JsonValueKind.String ? enfProp.GetString() : null;

            bool modR = edit.TryGetProperty("modifyR", out var mr) && mr.ValueKind == JsonValueKind.True;
            bool modG = edit.TryGetProperty("modifyG", out var mg) && mg.ValueKind == JsonValueKind.True;
            bool modB = edit.TryGetProperty("modifyB", out var mb) && mb.ValueKind == JsonValueKind.True;
            bool modA = edit.TryGetProperty("modifyA", out var ma) && ma.ValueKind == JsonValueKind.True;

            // Batch colors array (per-index RGBA)
            List<(int index, float r, float g, float b, float a)>? batchColors = null;
            if (edit.TryGetProperty("colors", out var colorsProp) && colorsProp.ValueKind == JsonValueKind.Array)
            {
                batchColors = new();
                foreach (var c in colorsProp.EnumerateArray())
                {
                    int idx = c.GetProperty("index").GetInt32();
                    float cr = (float)c.GetProperty("r").GetDouble();
                    float cg = (float)c.GetProperty("g").GetDouble();
                    float cb = (float)c.GetProperty("b").GetDouble();
                    float ca = (float)c.GetProperty("a").GetDouble();
                    batchColors.Add((idx, cr, cg, cb, ca));
                }
            }

            // Overwrite mode (flat fill with range)
            float fillR = 0, fillG = 0, fillB = 0, fillA = 1;
            bool hasOverwrite = false;
            int colorIndexStart = 0, colorIndexEnd = int.MaxValue;
            if (edit.TryGetProperty("r", out var rProp) && rProp.ValueKind == JsonValueKind.Number && batchColors == null)
            {
                fillR = (float)rProp.GetDouble();
                fillG = edit.TryGetProperty("g", out var gProp) ? (float)gProp.GetDouble() : 0;
                fillB = edit.TryGetProperty("b", out var bProp) ? (float)bProp.GetDouble() : 0;
                fillA = edit.TryGetProperty("a", out var aProp) ? (float)aProp.GetDouble() : 1;
                hasOverwrite = true;
            }
            if (edit.TryGetProperty("colorIndexStart", out var cisProp)) colorIndexStart = cisProp.GetInt32();
            if (edit.TryGetProperty("colorIndexEnd", out var cieProp)) colorIndexEnd = cieProp.GetInt32();

            if (batchColors == null && !hasOverwrite)
            {
                Console.Error.WriteLine($"Warning: edit #{editCount} has no 'colors' or 'r','g','b' — skipping");
                continue;
            }

            // Apply this edit to matching exports
            for (int ei = 0; ei < asset.Exports.Count; ei++)
            {
                if (exportIndex.HasValue && exportIndex.Value != ei) continue;

                var export = asset.Exports[ei];
                string exportName = export.ObjectName?.Value?.Value ?? "";
                string className = export.GetExportClassType()?.Value?.Value ?? "";

                if (exportNameFilter != null && !exportName.Equals(exportNameFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!className.Contains("ColorCurve") && !className.Contains("NiagaraDataInterface"))
                    continue;

                if (export is not NormalExport normalExport || normalExport.Data == null) continue;

                foreach (var prop in normalExport.Data)
                {
                    if (prop.Name?.Value?.Value != "ShaderLUT") continue;
                    if (prop is not ArrayPropertyData arrayProp || arrayProp.Value == null) continue;

                    int colorCount = arrayProp.Value.Length / 4;

                    if (batchColors != null)
                    {
                        foreach (var (idx, cr, cg, cb, ca) in batchColors)
                        {
                            int j = idx * 4;
                            if (j + 3 >= arrayProp.Value.Length) continue;
                            if (modR && arrayProp.Value[j] is FloatPropertyData rp) rp.Value = cr;
                            if (modG && arrayProp.Value[j + 1] is FloatPropertyData gp) gp.Value = cg;
                            if (modB && arrayProp.Value[j + 2] is FloatPropertyData bp) bp.Value = cb;
                            if (modA && arrayProp.Value[j + 3] is FloatPropertyData ap) ap.Value = ca;
                            totalModifiedColors++;
                        }
                    }
                    else
                    {
                        for (int ci = colorIndexStart; ci < Math.Min(colorIndexEnd, colorCount); ci++)
                        {
                            int j = ci * 4;
                            if (j + 3 >= arrayProp.Value.Length) continue;
                            if (modR && arrayProp.Value[j] is FloatPropertyData rp) rp.Value = fillR;
                            if (modG && arrayProp.Value[j + 1] is FloatPropertyData gp) gp.Value = fillG;
                            if (modB && arrayProp.Value[j + 2] is FloatPropertyData bp) bp.Value = fillB;
                            if (modA && arrayProp.Value[j + 3] is FloatPropertyData ap) ap.Value = fillA;
                            totalModifiedColors++;
                        }
                    }

                    totalModifiedExports++;
                    break;
                }

                // Patch Extras binary data
                if (normalExport.Extras != null && normalExport.Extras.Length >= 16)
                {
                    byte[] extras = normalExport.Extras;
                    int extrasPatched = 0;

                    if (batchColors != null && batchColors.Count > 0)
                    {
                        var (_, er, eg, eb, ea) = batchColors[0];
                        for (int ebi = 0; ebi + 15 < extras.Length; ebi += 4)
                        {
                            float fA = BitConverter.ToSingle(extras, ebi + 12);
                            if (fA < 0.99f || fA > 1.01f) continue;
                            float fR = BitConverter.ToSingle(extras, ebi);
                            float fG = BitConverter.ToSingle(extras, ebi + 4);
                            float fB = BitConverter.ToSingle(extras, ebi + 8);
                            if (fR < 0 || fR > 100 || fG < 0 || fG > 100 || fB < 0 || fB > 100) continue;
                            if (fR == 0 && fG == 0 && fB == 0) continue;
                            if (modR) Array.Copy(BitConverter.GetBytes(er), 0, extras, ebi, 4);
                            if (modG) Array.Copy(BitConverter.GetBytes(eg), 0, extras, ebi + 4, 4);
                            if (modB) Array.Copy(BitConverter.GetBytes(eb), 0, extras, ebi + 8, 4);
                            if (modA) Array.Copy(BitConverter.GetBytes(ea), 0, extras, ebi + 12, 4);
                            extrasPatched++;
                            ebi += 12;
                        }
                    }
                    else if (hasOverwrite)
                    {
                        for (int ebi = 0; ebi + 15 < extras.Length; ebi += 4)
                        {
                            float fA = BitConverter.ToSingle(extras, ebi + 12);
                            if (fA < 0.99f || fA > 1.01f) continue;
                            float fR = BitConverter.ToSingle(extras, ebi);
                            float fG = BitConverter.ToSingle(extras, ebi + 4);
                            float fB = BitConverter.ToSingle(extras, ebi + 8);
                            if (fR < 0 || fR > 100 || fG < 0 || fG > 100 || fB < 0 || fB > 100) continue;
                            if (fR == 0 && fG == 0 && fB == 0) continue;
                            if (modR) Array.Copy(BitConverter.GetBytes(fillR), 0, extras, ebi, 4);
                            if (modG) Array.Copy(BitConverter.GetBytes(fillG), 0, extras, ebi + 4, 4);
                            if (modB) Array.Copy(BitConverter.GetBytes(fillB), 0, extras, ebi + 8, 4);
                            if (modA) Array.Copy(BitConverter.GetBytes(fillA), 0, extras, ebi + 12, 4);
                            extrasPatched++;
                            ebi += 12;
                        }
                    }

                    if (extrasPatched > 0)
                        Console.Error.WriteLine($"  Export {ei}: patched {extrasPatched} RGBA quads in Extras");
                }
            }
        }

        // Single write for ALL edits
        if (totalModifiedColors > 0)
        {
            asset.Write(asset.FilePath);
        }

        var result = new { success = true, path = assetPath, editCount, modifiedExports = totalModifiedExports, modifiedColors = totalModifiedColors };
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static int CliNiagaraAnalyze(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: UAssetTool niagara_analyze <path> [usmap_path] [--export <index>] [--summary]");
            Console.Error.WriteLine("  Dumps full property structure of a NiagaraSystem asset.");
            Console.Error.WriteLine("  If <path> is a directory, processes all NS_*.uasset files.");
            Console.Error.WriteLine("  --summary  Compact one-line-per-file report (for directories).");
            return 1;
        }

        string assetPath = args[1];
        string? usmapPath = null;
        int? targetExport = null;
        bool summaryMode = false;

        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--export" && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out int idx)) targetExport = idx;
            }
            else if (args[i] == "--summary")
                summaryMode = true;
            else if (usmapPath == null && !args[i].StartsWith("--"))
                usmapPath = args[i];
        }

        // Directory batch mode — lightweight binary scan, no full asset parsing
        if (Directory.Exists(assetPath))
        {
            var files = Directory.GetFiles(assetPath, "NS_*.uasset", SearchOption.AllDirectories);
            Console.Error.WriteLine($"Found {files.Length} NS_*.uasset files");

            var summaryRows = new List<object>();
            // Precompute search bytes for class names we care about
            byte[] colorCurveBytes = System.Text.Encoding.ASCII.GetBytes("NiagaraDataInterfaceColorCurve");
            byte[] scalarCurveBytes = System.Text.Encoding.ASCII.GetBytes("NiagaraDataInterfaceCurve");
            byte[] vectorCurveBytes = System.Text.Encoding.ASCII.GetBytes("NiagaraDataInterfaceVectorCurve");
            byte[] vector2dCurveBytes = System.Text.Encoding.ASCII.GetBytes("NiagaraDataInterfaceVector2DCurve");
            byte[] shaderLutBytes = System.Text.Encoding.ASCII.GetBytes("ShaderLUT");

            for (int fi = 0; fi < files.Length; fi++)
            {
                string file = files[fi];
                string name = Path.GetFileNameWithoutExtension(file);
                try
                {
                    // Read .uasset header bytes to find class names in name/import tables
                    byte[] headerData = File.ReadAllBytes(file);
                    int colorCurves = CountOccurrences(headerData, colorCurveBytes);
                    // Subtract ColorCurve matches from generic Curve matches to avoid double-counting
                    int rawCurves = CountOccurrences(headerData, scalarCurveBytes);
                    int scalarCurves = rawCurves - colorCurves;
                    int vectorCurves = CountOccurrences(headerData, vectorCurveBytes);
                    int vector2dCurves = CountOccurrences(headerData, vector2dCurveBytes);

                    // ShaderLUT is in name map (header), not .uexp (unversioned uses schema indices)
                    int shaderLUTs = CountOccurrences(headerData, shaderLutBytes);

                    summaryRows.Add(new { name, colorCurves, scalarCurves, vectorCurves, vector2dCurves, shaderLUTs, sizeKB = headerData.Length / 1024, error = (string?)null });
                    headerData = null!; // help GC
                }
                catch (Exception ex)
                {
                    summaryRows.Add(new { name, colorCurves = 0, scalarCurves = 0, vectorCurves = 0, vector2dCurves = 0, shaderLUTs = 0, sizeKB = 0, error = ex.Message });
                }

                if ((fi + 1) % 100 == 0)
                {
                    GC.Collect(0, GCCollectionMode.Default, false);
                    Console.Error.WriteLine($"  Processed {fi + 1}/{files.Length}...");
                }
            }

            Console.Error.WriteLine($"  Done: {files.Length}/{files.Length}");
            Console.WriteLine(JsonSerializer.Serialize(new { fileCount = files.Length, files = summaryRows },
                new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        var asset = LoadAsset(assetPath, usmapPath);

        var exports = new List<object>();
        for (int i = 0; i < asset.Exports.Count; i++)
        {
            if (targetExport.HasValue && targetExport.Value != i) continue;

            var export = asset.Exports[i];
            string className = export.GetExportClassType()?.Value?.Value ?? "Unknown";
            string exportName = export.ObjectName?.Value?.Value ?? $"Export_{i}";
            string csharpType = export.GetType().Name;
            long serialSize = export.SerialSize;

            object? propTree = null;
            byte[]? extras = null;

            if (export is NormalExport normalExport)
            {
                if (normalExport.Data != null && normalExport.Data.Count > 0)
                    propTree = DumpPropertyList(normalExport.Data);
                if (normalExport.Extras != null && normalExport.Extras.Length > 0)
                    extras = normalExport.Extras;
            }

            // Resolve outer (parent) export name
            int outerIdx = export.OuterIndex.Index;
            string? outerName = null;
            if (outerIdx > 0 && outerIdx <= asset.Exports.Count)
                outerName = asset.Exports[outerIdx - 1].ObjectName?.Value?.Value;
            else if (outerIdx < 0)
            {
                int impIdx = -outerIdx - 1;
                if (impIdx < asset.Imports.Count)
                    outerName = asset.Imports[impIdx].ObjectName?.Value?.Value;
            }

            var entry = new Dictionary<string, object?>
            {
                ["index"] = i,
                ["name"] = exportName,
                ["class"] = className,
                ["csharpType"] = csharpType,
                ["serialSize"] = serialSize,
                ["outerIndex"] = outerIdx,
                ["outerName"] = outerName,
                ["properties"] = propTree,
            };
            if (extras != null)
                entry["extrasSize"] = extras.Length;

            exports.Add(entry);
        }

        var output = new { exportCount = asset.Exports.Count, exports };
        Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static object DumpPropertyList(List<PropertyData> properties)
    {
        var result = new List<object>();
        foreach (var prop in properties)
        {
            result.Add(DumpProperty(prop));
        }
        return result;
    }

    private static object DumpProperty(PropertyData prop)
    {
        string name = prop.Name?.Value?.Value ?? "?";
        string typeName = prop.GetType().Name.Replace("PropertyData", "");

        var entry = new Dictionary<string, object?> { ["name"] = name, ["type"] = typeName };

        switch (prop)
        {
            case StructPropertyData structProp:
                entry["structType"] = structProp.StructType?.Value?.Value ?? "?";
                if (structProp.Value != null && structProp.Value.Count > 0)
                    entry["children"] = DumpPropertyList(structProp.Value);
                break;

            case ArrayPropertyData arrayProp:
                entry["arrayType"] = arrayProp.ArrayType?.Value?.Value ?? "?";
                entry["count"] = arrayProp.Value?.Length ?? 0;
                if (arrayProp.Value != null)
                {
                    // For large arrays, show first few + last few
                    int count = arrayProp.Value.Length;
                    var items = new List<object>();
                    int showMax = 6;
                    for (int j = 0; j < Math.Min(showMax, count); j++)
                        items.Add(DumpProperty(arrayProp.Value[j]));
                    if (count > showMax)
                        items.Add($"... ({count - showMax} more)");
                    entry["items"] = items;
                }
                break;

            case FloatPropertyData fpd:
                entry["value"] = fpd.Value;
                break;
            case IntPropertyData ipd:
                entry["value"] = ipd.Value;
                break;
            case BoolPropertyData bpd:
                entry["value"] = bpd.Value;
                break;
            case NamePropertyData npd:
                entry["value"] = npd.Value?.Value?.Value;
                break;
            case StrPropertyData spd:
                entry["value"] = spd.Value?.Value;
                break;
            case EnumPropertyData epd:
                entry["value"] = epd.Value?.Value?.Value;
                break;
            case ObjectPropertyData opd:
                entry["value"] = opd.Value?.Index;
                break;
            case SoftObjectPropertyData sopd:
                entry["value"] = sopd.Value.AssetPath.AssetName?.Value?.Value;
                break;
            case BytePropertyData byteProp:
                entry["value"] = byteProp.Value;
                break;
            case UInt32PropertyData u32pd:
                entry["value"] = u32pd.Value;
                break;
            case Int64PropertyData i64pd:
                entry["value"] = i64pd.Value;
                break;
            default:
                // For unknown types, just record the type name
                break;
        }

        return entry;
    }

    /// <summary>
    /// niagara_details: Rich read-only inspection of NiagaraSystem assets.
    /// Returns JSON with color curves, outer chain, name map FNames for enemy/color params.
    /// Called by the Tauri NS Editor app via: UAssetTool niagara_details <asset> [usmap] [--full]
    /// </summary>
    private static int CliNiagaraDetails(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: UAssetTool niagara_details <asset_path> [usmap_path] [--full]");
            Console.Error.WriteLine("  Returns JSON with color curves, parent chains, and enemy/player FName metadata.");
            return 1;
        }

        string assetPath = args[1];
        string? usmapPath = null;
        bool fullMode = false;

        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--full") fullMode = true;
            else if (args[i] == "--usmap" && i + 1 < args.Length) usmapPath = args[++i];
            else if (usmapPath == null && !args[i].StartsWith("--")) usmapPath = args[i];
        }

        // Free memory before loading large assets to reduce OOM risk
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        UAsset asset;
        try { asset = LoadAsset(assetPath, usmapPath); }
        catch (OutOfMemoryException)
        {
            Console.Error.WriteLine($"Error: Out of memory loading asset: {assetPath}");
            Console.WriteLine(JsonSerializer.Serialize(new { success = false, error = "OutOfMemory", assetPath }));
            return 1;
        }

        // --- 1. Build export lookup and outer chain resolver ---
        string ResolveOuterChain(int exportIndex)
        {
            var chain = new List<string>();
            var visited = new HashSet<int>();
            int idx = exportIndex;
            while (idx >= 0 && idx < asset.Exports.Count && !visited.Contains(idx))
            {
                visited.Add(idx);
                var exp = asset.Exports[idx];
                string eName = exp.ObjectName?.Value?.Value ?? $"Export_{idx}";
                string eClass = exp.GetExportClassType()?.Value?.Value ?? "Unknown";
                chain.Add($"{eName} ({eClass})");
                int outerRaw = exp.OuterIndex.Index;
                if (outerRaw > 0) idx = outerRaw - 1;
                else break;
            }
            chain.Reverse();
            return string.Join(" > ", chain);
        }

        string? ResolveOuterName(Export exp)
        {
            int outerIdx = exp.OuterIndex.Index;
            if (outerIdx > 0 && outerIdx <= asset.Exports.Count)
                return asset.Exports[outerIdx - 1].ObjectName?.Value?.Value;
            if (outerIdx < 0)
            {
                int impIdx = -outerIdx - 1;
                if (impIdx < asset.Imports.Count)
                    return asset.Imports[impIdx].ObjectName?.Value?.Value;
            }
            return null;
        }

        // --- 2. Extract name map and find interesting FNames ---
        var nameMap = asset.GetNameMapIndexList();
        var emitterNames = new HashSet<string>();
        var enemyColorFNames = new List<Dictionary<string, object?>>();
        var colorParamFNames = new List<Dictionary<string, object?>>();
        var isEnemyFNames = new List<Dictionary<string, object?>>();

        // Also collect emitter names from NiagaraEmitter exports
        for (int i = 0; i < asset.Exports.Count; i++)
        {
            string cls = asset.Exports[i].GetExportClassType()?.Value?.Value ?? "";
            if (cls == "NiagaraEmitter")
                emitterNames.Add(asset.Exports[i].ObjectName?.Value?.Value ?? "");
        }

        for (int ni = 0; ni < nameMap.Count; ni++)
        {
            string fname = nameMap[ni]?.Value ?? "";
            if (string.IsNullOrEmpty(fname)) continue;

            // Classify interesting FNames
            string fnameLower = fname.ToLowerInvariant();
            if (fnameLower.Contains("enemycolor") || fnameLower.Contains("enemyvalue"))
            {
                // Parse emitter prefix: "NS_Glow_001.EnemyColor0" -> emitter="NS_Glow_001", param="EnemyColor0"
                string? emitter = null;
                string param = fname;
                int dotPos = fname.IndexOf('.');
                if (dotPos > 0) { emitter = fname.Substring(0, dotPos); param = fname.Substring(dotPos + 1); }
                enemyColorFNames.Add(new Dictionary<string, object?> { ["nameIndex"] = ni, ["fname"] = fname, ["emitter"] = emitter, ["param"] = param });
            }
            else if (fnameLower.Contains("colorfromcurve") || fnameLower.Contains("colorcurve") || fnameLower.Contains("linercolor") || fnameLower.Contains("linearcolor") || fnameLower.Contains("initial.color") || fnameLower.Contains("scalecolor"))
            {
                string? emitter = null;
                string param = fname;
                int dotPos = fname.IndexOf('.');
                if (dotPos > 0) { emitter = fname.Substring(0, dotPos); param = fname.Substring(dotPos + 1); }
                colorParamFNames.Add(new Dictionary<string, object?> { ["nameIndex"] = ni, ["fname"] = fname, ["emitter"] = emitter, ["param"] = param });
            }
            else if (fnameLower == "isenemy" || fnameLower.EndsWith(".isenemy"))
            {
                isEnemyFNames.Add(new Dictionary<string, object?> { ["nameIndex"] = ni, ["fname"] = fname });
            }
        }

        // --- 3. Classify color curves: use emitter enemy param presence ---
        // Build set of emitters that have EnemyColor params
        var emittersWithEnemyParams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ef in enemyColorFNames)
        {
            if (ef["emitter"] is string em && !string.IsNullOrEmpty(em))
                emittersWithEnemyParams.Add(em);
        }

        // --- 4. Build color curve details ---
        var colorCurves = new List<Dictionary<string, object?>>();
        var arrayColors = new List<Dictionary<string, object?>>();
        int totalColorCount = 0;
        int totalArrayColorValues = 0;

        for (int ei = 0; ei < asset.Exports.Count; ei++)
        {
            var export = asset.Exports[ei];
            string className = export.GetExportClassType()?.Value?.Value ?? "";
            if (className != "NiagaraDataInterfaceColorCurve") continue;
            if (export is not NormalExport ne || ne.Data == null) continue;

            string exportName = export.ObjectName?.Value?.Value ?? $"Export_{ei}";
            string parentChain = ResolveOuterChain(ei);
            string? parentName = ResolveOuterName(export);

            // Walk up to find the emitter name
            string? emitterName = null;
            int walkIdx = ei;
            while (walkIdx >= 0 && walkIdx < asset.Exports.Count)
            {
                var walkExp = asset.Exports[walkIdx];
                string walkClass = walkExp.GetExportClassType()?.Value?.Value ?? "";
                if (walkClass == "NiagaraEmitter")
                {
                    emitterName = walkExp.ObjectName?.Value?.Value;
                    break;
                }
                int outerRaw = walkExp.OuterIndex.Index;
                if (outerRaw > 0) walkIdx = outerRaw - 1;
                else break;
            }

            // Classify: does this emitter have enemy params?
            bool emitterHasEnemy = emitterName != null && emittersWithEnemyParams.Contains(emitterName);

            // Collect the actual enemy param FNames for this emitter
            var curveEnemyParams = emitterName != null
                ? enemyColorFNames
                    .Where(f => f["emitter"] is string em && em.Equals(emitterName, StringComparison.OrdinalIgnoreCase))
                    .Select(f => f["fname"]?.ToString() ?? "")
                    .ToList()
                : new List<string>();

            // Find ShaderLUT
            foreach (var prop in ne.Data)
            {
                if (prop.Name?.Value?.Value != "ShaderLUT") continue;
                if (prop is not ArrayPropertyData arrayProp || arrayProp.Value == null) continue;

                int colorCount = arrayProp.Value.Length / 4;
                var sampleColors = new List<Dictionary<string, object>>();

                if (fullMode)
                {
                    for (int j = 0; j + 3 < arrayProp.Value.Length; j += 4)
                    {
                        float cr = (arrayProp.Value[j] is FloatPropertyData rp) ? rp.Value : 0;
                        float cg = (arrayProp.Value[j + 1] is FloatPropertyData gp) ? gp.Value : 0;
                        float cb = (arrayProp.Value[j + 2] is FloatPropertyData bp) ? bp.Value : 0;
                        float ca = (arrayProp.Value[j + 3] is FloatPropertyData ap) ? ap.Value : 0;
                        sampleColors.Add(new Dictionary<string, object> { ["index"] = j / 4, ["r"] = cr, ["g"] = cg, ["b"] = cb, ["a"] = ca });
                    }
                }
                else
                {
                    // Just first, middle, last samples for compact output
                    int[] indices = colorCount <= 3 ? Enumerable.Range(0, colorCount).ToArray() : new[] { 0, colorCount / 2, colorCount - 1 };
                    foreach (int si in indices)
                    {
                        int j = si * 4;
                        if (j + 3 >= arrayProp.Value.Length) continue;
                        float cr = (arrayProp.Value[j] is FloatPropertyData rp) ? rp.Value : 0;
                        float cg = (arrayProp.Value[j + 1] is FloatPropertyData gp) ? gp.Value : 0;
                        float cb = (arrayProp.Value[j + 2] is FloatPropertyData bp) ? bp.Value : 0;
                        float ca = (arrayProp.Value[j + 3] is FloatPropertyData ap) ? ap.Value : 0;
                        sampleColors.Add(new Dictionary<string, object> { ["index"] = si, ["r"] = cr, ["g"] = cg, ["b"] = cb, ["a"] = ca });
                    }
                }

                // Classification heuristics
                var classification = ClassifyColorCurve(sampleColors);

                colorCurves.Add(new Dictionary<string, object?>
                {
                    ["exportIndex"] = ei,
                    ["exportName"] = exportName,
                    ["colorCount"] = colorCount,
                    ["sampleColors"] = sampleColors,
                    ["parentName"] = parentName,
                    ["parentChain"] = parentChain,
                    ["emitterName"] = emitterName,
                    ["emitterHasEnemyParams"] = emitterHasEnemy,
                    ["enemyParams"] = curveEnemyParams,
                    ["classification"] = classification,
                });
                totalColorCount += colorCount;
                break;
            }
        }

        // --- 5. Also find ArrayPropertyData with LinearColor-like RGBA patterns (arrayColors) ---
        for (int ei = 0; ei < asset.Exports.Count; ei++)
        {
            var export = asset.Exports[ei];
            string className = export.GetExportClassType()?.Value?.Value ?? "";
            // Skip ColorCurves (already handled) and non-parseable exports
            if (className == "NiagaraDataInterfaceColorCurve") continue;
            if (export is not NormalExport ne || ne.Data == null) continue;

            string exportName = export.ObjectName?.Value?.Value ?? $"Export_{ei}";

            foreach (var prop in ne.Data)
            {
                if (prop is not ArrayPropertyData arrayProp || arrayProp.Value == null) continue;
                // Look for arrays of StructPropertyData with R,G,B,A float fields (LinearColor arrays)
                if (arrayProp.Value.Length < 4) continue;
                // Check if it looks like RGBA float data
                bool isColorArray = true;
                int colorCount = 0;
                var sampleColors = new List<Dictionary<string, object>>();

                if (arrayProp.ArrayType?.Value?.Value == "FloatProperty" && arrayProp.Value.Length >= 4 && arrayProp.Value.Length % 4 == 0)
                {
                    // Could be another ShaderLUT-like array - check if it has plausible RGBA patterns
                    colorCount = arrayProp.Value.Length / 4;
                    for (int j = 0; j + 3 < arrayProp.Value.Length && sampleColors.Count < (fullMode ? colorCount : 3); j += 4)
                    {
                        float cr = (arrayProp.Value[j] is FloatPropertyData rp) ? rp.Value : 0;
                        float cg = (arrayProp.Value[j + 1] is FloatPropertyData gp) ? gp.Value : 0;
                        float cb = (arrayProp.Value[j + 2] is FloatPropertyData bp) ? bp.Value : 0;
                        float ca = (arrayProp.Value[j + 3] is FloatPropertyData ap) ? ap.Value : 0;
                        if (ca < 0 || ca > 2) { isColorArray = false; break; }
                        sampleColors.Add(new Dictionary<string, object> { ["index"] = j / 4, ["r"] = cr, ["g"] = cg, ["b"] = cb, ["a"] = ca });
                    }
                }
                else isColorArray = false;

                if (!isColorArray || sampleColors.Count == 0) continue;

                string propName = prop.Name?.Value?.Value ?? "Unknown";
                string? parentName = ResolveOuterName(export);
                string parentChain = ResolveOuterChain(ei);

                // Resolve emitter for this export
                string? acEmitterName = null;
                int acWalkIdx = ei;
                while (acWalkIdx >= 0 && acWalkIdx < asset.Exports.Count)
                {
                    var acWalkExp = asset.Exports[acWalkIdx];
                    string acCls = acWalkExp.GetExportClassType()?.Value?.Value ?? "";
                    if (acCls == "NiagaraEmitter")
                    {
                        acEmitterName = acWalkExp.ObjectName?.Value?.Value;
                        break;
                    }
                    int acOuterRaw = acWalkExp.OuterIndex.Index;
                    if (acOuterRaw > 0) acWalkIdx = acOuterRaw - 1;
                    else break;
                }
                bool acEmitterHasEnemy = acEmitterName != null && emittersWithEnemyParams.Contains(acEmitterName);
                var acEnemyParams = acEmitterName != null
                    ? enemyColorFNames
                        .Where(f => f["emitter"] is string em && em.Equals(acEmitterName, StringComparison.OrdinalIgnoreCase))
                        .Select(f => f["fname"]?.ToString() ?? "")
                        .ToList()
                    : new List<string>();

                var classification = ClassifyColorCurve(sampleColors);

                arrayColors.Add(new Dictionary<string, object?>
                {
                    ["exportIndex"] = ei,
                    ["exportName"] = $"{exportName}.{propName}",
                    ["colorCount"] = colorCount,
                    ["sampleColors"] = sampleColors,
                    ["parentName"] = parentName,
                    ["parentChain"] = parentChain,
                    ["emitterName"] = acEmitterName,
                    ["emitterHasEnemyParams"] = acEmitterHasEnemy,
                    ["enemyParams"] = acEnemyParams,
                    ["classification"] = classification,
                });
                totalArrayColorValues += colorCount;
            }
        }

        // --- 6. Build emitter summary ---
        var emitterSummary = new List<Dictionary<string, object?>>();
        for (int ei = 0; ei < asset.Exports.Count; ei++)
        {
            var export = asset.Exports[ei];
            string cls = export.GetExportClassType()?.Value?.Value ?? "";
            if (cls != "NiagaraEmitter") continue;

            string eName = export.ObjectName?.Value?.Value ?? "";
            bool hasEnemy = emittersWithEnemyParams.Contains(eName);

            // Collect enemy param names for this emitter
            var enemyParams = enemyColorFNames
                .Where(f => f["emitter"] is string em && em.Equals(eName, StringComparison.OrdinalIgnoreCase))
                .Select(f => f["fname"]?.ToString() ?? "")
                .ToList();

            // Collect color param names for this emitter
            var colorParams = colorParamFNames
                .Where(f => f["emitter"] is string em && em.Equals(eName, StringComparison.OrdinalIgnoreCase))
                .Select(f => f["fname"]?.ToString() ?? "")
                .ToList();

            emitterSummary.Add(new Dictionary<string, object?>
            {
                ["exportIndex"] = ei,
                ["name"] = eName,
                ["hasEnemyParams"] = hasEnemy,
                ["enemyParams"] = enemyParams,
                ["colorParams"] = colorParams,
            });
        }

        // --- 7. Output JSON ---
        var result = new Dictionary<string, object?>
        {
            ["success"] = true,
            ["totalExports"] = asset.Exports.Count,
            ["colorCurveCount"] = colorCurves.Count,
            ["totalColorCount"] = totalColorCount,
            ["colorCurves"] = colorCurves,
        };

        if (arrayColors.Count > 0)
        {
            result["arrayColorCount"] = arrayColors.Count;
            result["totalArrayColorValues"] = totalArrayColorValues;
            result["arrayColors"] = arrayColors;
        }

        result["emitters"] = emitterSummary;
        result["enemyColorFNames"] = enemyColorFNames;
        result["colorParamFNames"] = colorParamFNames;
        result["isEnemyFNames"] = isEnemyFNames;
        result["hasIsEnemyParam"] = isEnemyFNames.Count > 0;

        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    /// <summary>
    /// Streaming niagara_details: loads usmap ONCE, then reads asset paths from stdin
    /// and outputs JSON per asset. Massively reduces memory and startup overhead.
    /// Protocol:
    ///   stdin:  one asset path per line. Empty line or "EXIT" to quit.
    ///   stdout: one JSON object per asset (compact, single line), followed by "---END---" delimiter.
    ///   stderr: diagnostic logging (same as niagara_details).
    /// </summary>
    private static int CliNiagaraDetailsStream(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: UAssetTool niagara_details_stream <usmap_path> [--full]");
            Console.Error.WriteLine("  Reads asset paths from stdin (one per line), outputs JSON per asset.");
            Console.Error.WriteLine("  Send empty line or EXIT to quit.");
            return 1;
        }

        string? usmapPath = null;
        bool fullMode = false;

        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--full") fullMode = true;
            else if (usmapPath == null && !args[i].StartsWith("--")) usmapPath = args[i];
        }

        // Load usmap ONCE — this is the expensive part (~200MB, 25k schemas)
        Console.Error.WriteLine($"[stream] Loading usmap: {usmapPath ?? "null"}");
        Usmap? mappings = LoadMappings(usmapPath);
        Console.Error.WriteLine($"[stream] Usmap loaded: {mappings != null}, schemas: {mappings?.Schemas?.Count ?? 0}");
        Console.Error.WriteLine("[stream] READY");
        // Signal readiness on stdout so the caller knows usmap is loaded
        Console.Out.WriteLine("READY");
        Console.Out.Flush();

        int processed = 0;
        using var reader = new StreamReader(Console.OpenStandardInput(), System.Text.Encoding.UTF8);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (string.IsNullOrEmpty(line) || line.Equals("EXIT", StringComparison.OrdinalIgnoreCase))
                break;

            string assetPath = line;
            Console.Error.WriteLine($"[stream] Processing: {Path.GetFileName(assetPath)}");

            try
            {
                // Free previous asset memory
                if (processed > 0 && processed % 5 == 0)
                {
                    GC.Collect(2, GCCollectionMode.Forced, true, true);
                    GC.WaitForPendingFinalizers();
                }

                var result = ProcessSingleNiagaraDetails(assetPath, mappings, fullMode);
                // Output compact JSON (no indentation) + delimiter
                string json = JsonSerializer.Serialize(result);
                Console.Out.WriteLine(json);
                Console.Out.WriteLine("---END---");
                Console.Out.Flush();
            }
            catch (OutOfMemoryException)
            {
                Console.Error.WriteLine($"[stream] OOM: {Path.GetFileName(assetPath)}");
                var errorResult = new Dictionary<string, object?> { ["success"] = false, ["error"] = "OutOfMemory", ["assetPath"] = assetPath };
                Console.Out.WriteLine(JsonSerializer.Serialize(errorResult));
                Console.Out.WriteLine("---END---");
                Console.Out.Flush();
                // Force GC after OOM
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[stream] Error: {Path.GetFileName(assetPath)}: {ex.Message}");
                var errorResult = new Dictionary<string, object?> { ["success"] = false, ["error"] = ex.Message, ["assetPath"] = assetPath };
                Console.Out.WriteLine(JsonSerializer.Serialize(errorResult));
                Console.Out.WriteLine("---END---");
                Console.Out.Flush();
            }
            processed++;
        }

        Console.Error.WriteLine($"[stream] Done: processed {processed} assets");
        return 0;
    }

    /// <summary>
    /// Core niagara_details logic extracted for reuse by both CliNiagaraDetails and CliNiagaraDetailsStream.
    /// Returns a result dictionary ready for JSON serialization.
    /// </summary>
    private static Dictionary<string, object?> ProcessSingleNiagaraDetails(string assetPath, Usmap? mappings, bool fullMode)
    {
        UAsset asset = LoadAssetWithMappings(assetPath, mappings);

        // --- 1. Build export lookup and outer chain resolver ---
        string ResolveOuterChain(int exportIndex)
        {
            var chain = new List<string>();
            var visited = new HashSet<int>();
            int idx = exportIndex;
            while (idx >= 0 && idx < asset.Exports.Count && !visited.Contains(idx))
            {
                visited.Add(idx);
                var exp = asset.Exports[idx];
                string eName = exp.ObjectName?.Value?.Value ?? $"Export_{idx}";
                string eClass = exp.GetExportClassType()?.Value?.Value ?? "Unknown";
                chain.Add($"{eName} ({eClass})");
                int outerRaw = exp.OuterIndex.Index;
                if (outerRaw > 0) idx = outerRaw - 1;
                else break;
            }
            chain.Reverse();
            return string.Join(" > ", chain);
        }

        string? ResolveOuterName(Export exp)
        {
            int outerIdx = exp.OuterIndex.Index;
            if (outerIdx > 0 && outerIdx <= asset.Exports.Count)
                return asset.Exports[outerIdx - 1].ObjectName?.Value?.Value;
            if (outerIdx < 0)
            {
                int impIdx = -outerIdx - 1;
                if (impIdx < asset.Imports.Count)
                    return asset.Imports[impIdx].ObjectName?.Value?.Value;
            }
            return null;
        }

        // --- 2. Extract name map and find interesting FNames ---
        var nameMap = asset.GetNameMapIndexList();
        var emitterNames = new HashSet<string>();
        var enemyColorFNames = new List<Dictionary<string, object?>>();
        var colorParamFNames = new List<Dictionary<string, object?>>();
        var isEnemyFNames = new List<Dictionary<string, object?>>();

        for (int i = 0; i < asset.Exports.Count; i++)
        {
            string cls = asset.Exports[i].GetExportClassType()?.Value?.Value ?? "";
            if (cls == "NiagaraEmitter")
                emitterNames.Add(asset.Exports[i].ObjectName?.Value?.Value ?? "");
        }

        for (int ni = 0; ni < nameMap.Count; ni++)
        {
            string fname = nameMap[ni]?.Value ?? "";
            if (string.IsNullOrEmpty(fname)) continue;
            string fnameLower = fname.ToLowerInvariant();
            if (fnameLower.Contains("enemycolor") || fnameLower.Contains("enemyvalue"))
            {
                string? emitter = null; string param = fname;
                int dotPos = fname.IndexOf('.');
                if (dotPos > 0) { emitter = fname.Substring(0, dotPos); param = fname.Substring(dotPos + 1); }
                enemyColorFNames.Add(new Dictionary<string, object?> { ["nameIndex"] = ni, ["fname"] = fname, ["emitter"] = emitter, ["param"] = param });
            }
            else if (fnameLower.Contains("colorfromcurve") || fnameLower.Contains("colorcurve") || fnameLower.Contains("linercolor") || fnameLower.Contains("linearcolor") || fnameLower.Contains("initial.color") || fnameLower.Contains("scalecolor"))
            {
                string? emitter = null; string param = fname;
                int dotPos = fname.IndexOf('.');
                if (dotPos > 0) { emitter = fname.Substring(0, dotPos); param = fname.Substring(dotPos + 1); }
                colorParamFNames.Add(new Dictionary<string, object?> { ["nameIndex"] = ni, ["fname"] = fname, ["emitter"] = emitter, ["param"] = param });
            }
            else if (fnameLower == "isenemy" || fnameLower.EndsWith(".isenemy"))
            {
                isEnemyFNames.Add(new Dictionary<string, object?> { ["nameIndex"] = ni, ["fname"] = fname });
            }
        }

        // --- 3. Build set of emitters with enemy params ---
        var emittersWithEnemyParams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ef in enemyColorFNames)
        {
            if (ef["emitter"] is string em && !string.IsNullOrEmpty(em))
                emittersWithEnemyParams.Add(em);
        }

        // --- 4. Build color curve details ---
        var colorCurves = new List<Dictionary<string, object?>>();
        var arrayColors = new List<Dictionary<string, object?>>();
        int totalColorCount = 0;
        int totalArrayColorValues = 0;

        for (int ei = 0; ei < asset.Exports.Count; ei++)
        {
            var export = asset.Exports[ei];
            string className = export.GetExportClassType()?.Value?.Value ?? "";
            if (className != "NiagaraDataInterfaceColorCurve") continue;
            if (export is not NormalExport ne || ne.Data == null) continue;

            string exportName = export.ObjectName?.Value?.Value ?? $"Export_{ei}";
            string parentChain = ResolveOuterChain(ei);
            string? parentName = ResolveOuterName(export);

            string? emitterName = null;
            int walkIdx = ei;
            while (walkIdx >= 0 && walkIdx < asset.Exports.Count)
            {
                var walkExp = asset.Exports[walkIdx];
                string walkClass = walkExp.GetExportClassType()?.Value?.Value ?? "";
                if (walkClass == "NiagaraEmitter") { emitterName = walkExp.ObjectName?.Value?.Value; break; }
                int outerRaw = walkExp.OuterIndex.Index;
                if (outerRaw > 0) walkIdx = outerRaw - 1; else break;
            }

            bool emitterHasEnemy = emitterName != null && emittersWithEnemyParams.Contains(emitterName);
            var curveEnemyParams = emitterName != null
                ? enemyColorFNames.Where(f => f["emitter"] is string em && em.Equals(emitterName, StringComparison.OrdinalIgnoreCase)).Select(f => f["fname"]?.ToString() ?? "").ToList()
                : new List<string>();

            foreach (var prop in ne.Data)
            {
                if (prop.Name?.Value?.Value != "ShaderLUT") continue;
                if (prop is not ArrayPropertyData arrayProp || arrayProp.Value == null) continue;

                int colorCount = arrayProp.Value.Length / 4;
                var sampleColors = new List<Dictionary<string, object>>();

                if (fullMode)
                {
                    for (int j = 0; j + 3 < arrayProp.Value.Length; j += 4)
                    {
                        float cr = (arrayProp.Value[j] is FloatPropertyData rp) ? rp.Value : 0;
                        float cg = (arrayProp.Value[j + 1] is FloatPropertyData gp) ? gp.Value : 0;
                        float cb = (arrayProp.Value[j + 2] is FloatPropertyData bp) ? bp.Value : 0;
                        float ca = (arrayProp.Value[j + 3] is FloatPropertyData ap) ? ap.Value : 0;
                        sampleColors.Add(new Dictionary<string, object> { ["index"] = j / 4, ["r"] = cr, ["g"] = cg, ["b"] = cb, ["a"] = ca });
                    }
                }
                else
                {
                    int[] indices = colorCount <= 3 ? Enumerable.Range(0, colorCount).ToArray() : new[] { 0, colorCount / 2, colorCount - 1 };
                    foreach (int si in indices)
                    {
                        int j = si * 4;
                        if (j + 3 >= arrayProp.Value.Length) continue;
                        float cr = (arrayProp.Value[j] is FloatPropertyData rp) ? rp.Value : 0;
                        float cg = (arrayProp.Value[j + 1] is FloatPropertyData gp) ? gp.Value : 0;
                        float cb = (arrayProp.Value[j + 2] is FloatPropertyData bp) ? bp.Value : 0;
                        float ca = (arrayProp.Value[j + 3] is FloatPropertyData ap) ? ap.Value : 0;
                        sampleColors.Add(new Dictionary<string, object> { ["index"] = si, ["r"] = cr, ["g"] = cg, ["b"] = cb, ["a"] = ca });
                    }
                }

                var classification = ClassifyColorCurve(sampleColors);
                colorCurves.Add(new Dictionary<string, object?>
                {
                    ["exportIndex"] = ei, ["exportName"] = exportName, ["colorCount"] = colorCount,
                    ["sampleColors"] = sampleColors, ["parentName"] = parentName, ["parentChain"] = parentChain,
                    ["emitterName"] = emitterName, ["emitterHasEnemyParams"] = emitterHasEnemy,
                    ["enemyParams"] = curveEnemyParams, ["classification"] = classification,
                });
                totalColorCount += colorCount;
                break;
            }
        }

        // --- 5. ArrayColor patterns ---
        for (int ei = 0; ei < asset.Exports.Count; ei++)
        {
            var export = asset.Exports[ei];
            string className = export.GetExportClassType()?.Value?.Value ?? "";
            if (className == "NiagaraDataInterfaceColorCurve") continue;
            if (export is not NormalExport ne || ne.Data == null) continue;

            string exportName = export.ObjectName?.Value?.Value ?? $"Export_{ei}";
            foreach (var prop in ne.Data)
            {
                if (prop is not ArrayPropertyData arrayProp || arrayProp.Value == null) continue;
                if (arrayProp.Value.Length < 4) continue;
                bool isColorArray = true;
                int colorCount = 0;
                var sampleColors = new List<Dictionary<string, object>>();

                if (arrayProp.ArrayType?.Value?.Value == "FloatProperty" && arrayProp.Value.Length >= 4 && arrayProp.Value.Length % 4 == 0)
                {
                    colorCount = arrayProp.Value.Length / 4;
                    for (int j = 0; j + 3 < arrayProp.Value.Length && sampleColors.Count < (fullMode ? colorCount : 3); j += 4)
                    {
                        float cr = (arrayProp.Value[j] is FloatPropertyData rp) ? rp.Value : 0;
                        float cg = (arrayProp.Value[j + 1] is FloatPropertyData gp) ? gp.Value : 0;
                        float cb = (arrayProp.Value[j + 2] is FloatPropertyData bp) ? bp.Value : 0;
                        float ca = (arrayProp.Value[j + 3] is FloatPropertyData ap) ? ap.Value : 0;
                        if (ca < 0 || ca > 2) { isColorArray = false; break; }
                        sampleColors.Add(new Dictionary<string, object> { ["index"] = j / 4, ["r"] = cr, ["g"] = cg, ["b"] = cb, ["a"] = ca });
                    }
                }
                else isColorArray = false;

                if (!isColorArray || sampleColors.Count == 0) continue;

                string propName = prop.Name?.Value?.Value ?? "Unknown";
                string? pName = ResolveOuterName(export);
                string pChain = ResolveOuterChain(ei);

                string? acEmitterName = null;
                int acWalkIdx = ei;
                while (acWalkIdx >= 0 && acWalkIdx < asset.Exports.Count)
                {
                    var acWalkExp = asset.Exports[acWalkIdx];
                    string acCls = acWalkExp.GetExportClassType()?.Value?.Value ?? "";
                    if (acCls == "NiagaraEmitter") { acEmitterName = acWalkExp.ObjectName?.Value?.Value; break; }
                    int acOuterRaw = acWalkExp.OuterIndex.Index;
                    if (acOuterRaw > 0) acWalkIdx = acOuterRaw - 1; else break;
                }
                bool acEmitterHasEnemy = acEmitterName != null && emittersWithEnemyParams.Contains(acEmitterName);
                var acEnemyParams = acEmitterName != null
                    ? enemyColorFNames.Where(f => f["emitter"] is string em && em.Equals(acEmitterName, StringComparison.OrdinalIgnoreCase)).Select(f => f["fname"]?.ToString() ?? "").ToList()
                    : new List<string>();

                var classification = ClassifyColorCurve(sampleColors);
                arrayColors.Add(new Dictionary<string, object?>
                {
                    ["exportIndex"] = ei, ["exportName"] = $"{exportName}.{propName}", ["colorCount"] = colorCount,
                    ["sampleColors"] = sampleColors, ["parentName"] = pName, ["parentChain"] = pChain,
                    ["emitterName"] = acEmitterName, ["emitterHasEnemyParams"] = acEmitterHasEnemy,
                    ["enemyParams"] = acEnemyParams, ["classification"] = classification,
                });
                totalArrayColorValues += colorCount;
            }
        }

        // --- 6. Emitter summary ---
        var emitterSummary = new List<Dictionary<string, object?>>();
        for (int ei = 0; ei < asset.Exports.Count; ei++)
        {
            var export = asset.Exports[ei];
            string cls = export.GetExportClassType()?.Value?.Value ?? "";
            if (cls != "NiagaraEmitter") continue;
            string eName = export.ObjectName?.Value?.Value ?? "";
            bool hasEnemy = emittersWithEnemyParams.Contains(eName);
            var enemyParams = enemyColorFNames.Where(f => f["emitter"] is string em && em.Equals(eName, StringComparison.OrdinalIgnoreCase)).Select(f => f["fname"]?.ToString() ?? "").ToList();
            var colorParams = colorParamFNames.Where(f => f["emitter"] is string em && em.Equals(eName, StringComparison.OrdinalIgnoreCase)).Select(f => f["fname"]?.ToString() ?? "").ToList();
            emitterSummary.Add(new Dictionary<string, object?> { ["exportIndex"] = ei, ["name"] = eName, ["hasEnemyParams"] = hasEnemy, ["enemyParams"] = enemyParams, ["colorParams"] = colorParams });
        }

        // --- 7. Build result ---
        var result = new Dictionary<string, object?>
        {
            ["success"] = true,
            ["assetPath"] = assetPath,
            ["totalExports"] = asset.Exports.Count,
            ["colorCurveCount"] = colorCurves.Count,
            ["totalColorCount"] = totalColorCount,
            ["colorCurves"] = colorCurves,
        };
        if (arrayColors.Count > 0)
        {
            result["arrayColorCount"] = arrayColors.Count;
            result["totalArrayColorValues"] = totalArrayColorValues;
            result["arrayColors"] = arrayColors;
        }
        result["emitters"] = emitterSummary;
        result["enemyColorFNames"] = enemyColorFNames;
        result["colorParamFNames"] = colorParamFNames;
        result["isEnemyFNames"] = isEnemyFNames;
        result["hasIsEnemyParam"] = isEnemyFNames.Count > 0;

        return result;
    }

    /// <summary>
    /// Heuristic classification of a color curve based on its sample values.
    /// </summary>
    private static Dictionary<string, object> ClassifyColorCurve(List<Dictionary<string, object>> samples)
    {
        if (samples.Count == 0)
            return new Dictionary<string, object> { ["type"] = "unknown", ["confidence"] = 0.0, ["reason"] = "no samples", ["suggestEdit"] = false, ["isGrayscale"] = false, ["isHdr"] = false, ["hasAlphaVariation"] = false, ["isConstant"] = false, ["maxValue"] = 0.0, ["minValue"] = 0.0 };

        float maxR = 0, maxG = 0, maxB = 0, minR = float.MaxValue, minG = float.MaxValue, minB = float.MaxValue;
        float sumR = 0, sumG = 0, sumB = 0;
        bool hasAlphaVariation = false;
        float firstA = Convert.ToSingle(samples[0]["a"]);

        foreach (var s in samples)
        {
            float sr = Convert.ToSingle(s["r"]), sg = Convert.ToSingle(s["g"]), sb = Convert.ToSingle(s["b"]), sa = Convert.ToSingle(s["a"]);
            maxR = Math.Max(maxR, sr); maxG = Math.Max(maxG, sg); maxB = Math.Max(maxB, sb);
            minR = Math.Min(minR, sr); minG = Math.Min(minG, sg); minB = Math.Min(minB, sb);
            sumR += sr; sumG += sg; sumB += sb;
            if (Math.Abs(sa - firstA) > 0.01f) hasAlphaVariation = true;
        }

        float avgR = sumR / samples.Count, avgG = sumG / samples.Count, avgB = sumB / samples.Count;
        float maxVal = Math.Max(maxR, Math.Max(maxG, maxB));
        float minVal = Math.Min(minR, Math.Min(minG, minB));
        bool isHdr = maxVal > 1.05f;
        bool isGrayscale = Math.Abs(avgR - avgG) < 0.05f && Math.Abs(avgG - avgB) < 0.05f;
        bool isConstant = (maxR - minR) < 0.01f && (maxG - minG) < 0.01f && (maxB - minB) < 0.01f;

        string type = "color";
        string reason = "has color variation";
        double confidence = 0.8;
        bool suggestEdit = true;

        if (isGrayscale && !isHdr)
        {
            type = "opacity";
            reason = "grayscale, non-HDR — likely opacity/alpha curve";
            confidence = 0.6;
            suggestEdit = false;
        }
        else if (isHdr)
        {
            type = "emission";
            reason = isGrayscale ? "grayscale HDR — emission intensity" : "HDR color — emission/glow";
            confidence = 0.9;
            suggestEdit = true;
        }
        else if (isConstant && maxVal < 0.01f)
        {
            type = "unknown";
            reason = "all zeros";
            confidence = 0.3;
            suggestEdit = false;
        }

        return new Dictionary<string, object>
        {
            ["type"] = type,
            ["confidence"] = confidence,
            ["reason"] = reason,
            ["suggestEdit"] = suggestEdit,
            ["isGrayscale"] = isGrayscale,
            ["isHdr"] = isHdr,
            ["hasAlphaVariation"] = hasAlphaVariation,
            ["isConstant"] = isConstant,
            ["maxValue"] = (double)maxVal,
            ["minValue"] = (double)minVal,
        };
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
                    Console.Out.Flush(); // Ensure response is sent immediately
                }
                catch (JsonException)
                {
                    WriteJsonResponse(false, "Invalid JSON format");
                    Console.Out.Flush();
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
                "batch_strip_mipmaps_native" => BatchStripMipmapsNative(request.FilePaths, request.UsmapPath, request.Parallel),
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
                
                // PAK operations
                "list_pak_files" => ListPakFiles(request.FilePath, request.AesKey),
                "extract_pak_file" => ExtractPakFile(request.FilePath, request.InternalPath, request.OutputPath, request.AesKey),
                "extract_pak_all" => ExtractPakAll(request.FilePath, request.OutputPath, request.AesKey),
                "create_pak" => CreatePakJson(request.OutputPath, request.FilePaths, request.MountPoint, request.PathHashSeed, request.AesKey),
                "create_companion_pak" => CreateCompanionPakJson(request.OutputPath, request.FilePaths, request.MountPoint, request.PathHashSeed, request.AesKey),
                
                // IoStore operations
                "list_iostore_files" => ListIoStoreFiles(request.FilePath, request.AesKey),
                "create_iostore" => CreateIoStoreJson(request.OutputPath, request.InputDir, request.Compress, request.AesKey),
                "is_iostore_compressed" => IsIoStoreCompressed(request.FilePath),
                "is_iostore_encrypted" => IsIoStoreEncrypted(request.FilePath),
                "recompress_iostore" => RecompressIoStore(request.FilePath),
                "extract_iostore" => ExtractIoStoreJson(request.FilePath, request.OutputPath, request.AesKey),
                "extract_script_objects" => ExtractScriptObjectsJson(request.FilePath, request.OutputPath),
                "create_mod_iostore" => CreateModIoStoreJson(request.OutputPath, request.InputDir, request.MountPoint, request.Compress, request.AesKey, request.Parallel, request.Obfuscate),
                
                // Additional CLI-equivalent operations for parity
                "dump" => DumpAssetJson(request.FilePath, request.UsmapPath),
                "skeletal_mesh_info" => GetSkeletalMeshInfoJson(request.FilePath, request.UsmapPath),
                "to_json" => ToJsonJson(request.FilePath, request.UsmapPath, request.OutputPath),
                "from_json" => FromJsonJson(request.FilePath, request.OutputPath, request.UsmapPath),
                "cityhash" => CityHashJson(request.FilePath),
                "clone_mod_iostore" => CloneModIoStoreJson(request.FilePath, request.OutputPath),
                "inspect_zen" => InspectZenJson(request.FilePath),
                
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
            ["type"] = prop.PropertyType?.ToString(),
            ["array_index"] = prop.ArrayIndex
        };
        
        try
        {
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
                result["value"] = objProp.Value?.Index;
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
                try { result["value"] = prop.ToString(); }
                catch { result["value"] = $"[{prop.GetType().Name}]"; }
            }
        }
        catch (Exception)
        {
            result["value"] = $"[Error serializing {prop.GetType().Name}]";
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
            asset.FilePath = Path.GetFullPath(filePath);
            PreloadReferencedAssetsForSchemas(asset);
            
            string? outputDir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(outputDir))
                Directory.CreateDirectory(outputDir);
            
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
    /// When parallel=true, uses Parallel.ForEach for concurrent processing.
    /// </summary>
    private static UAssetResponse BatchStripMipmapsNative(List<string>? filePaths, string? usmapPath, bool parallel = false)
    {
        if (filePaths == null || filePaths.Count == 0)
            return new UAssetResponse { Success = false, Message = "file_paths required" };

        try
        {
            Console.Error.WriteLine($"[UAssetTool] Batch stripping mipmaps for {filePaths.Count} files (parallel={parallel})");
            
            // Use provided usmap_path, fall back to environment variable
            string? effectiveUsmapPath = usmapPath ?? Environment.GetEnvironmentVariable("USMAP_PATH");
            Console.Error.WriteLine($"[UAssetTool] Using USMAP: {effectiveUsmapPath ?? "null"}");
            Usmap? mappings = LoadMappings(effectiveUsmapPath);
            
            // Use thread-safe collections for parallel processing
            var results = new System.Collections.Concurrent.ConcurrentBag<object>();
            int successCount = 0;
            int skipCount = 0;
            int errorCount = 0;
            
            // Process single file - returns (success, skip, error) counts
            (int success, int skip, int error) ProcessSingleTexture(string filePath)
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                {
                    results.Add(new { path = filePath, success = false, message = "File not found" });
                    return (0, 0, 1);
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
                        return (0, 0, 1);
                    }
                    
                    if (textureExport.PlatformData == null)
                    {
                        results.Add(new { path = filePath, success = false, message = "No PlatformData (texture not parsed)" });
                        return (0, 0, 1);
                    }
                    
                    int originalMipCount = textureExport.MipCount;
                    
                    if (originalMipCount <= 1)
                    {
                        results.Add(new { path = filePath, success = true, message = "Already has 1 mipmap", skipped = true });
                        return (0, 1, 0);
                    }
                    
                    // Target data_resource_id = 5 for Marvel Rivals textures
                    int targetDataResourceId = 5;
                    
                    // Strip mipmaps
                    bool stripped = textureExport.StripMipmaps();
                    if (!stripped)
                    {
                        results.Add(new { path = filePath, success = false, message = "Failed to strip mipmaps" });
                        return (0, 0, 1);
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
                    
                    Console.Error.WriteLine($"[UAssetTool] Stripped: {Path.GetFileName(filePath)} ({originalMipCount} -> {textureExport.MipCount})");
                    return (1, 0, 0);
                }
                catch (Exception ex)
                {
                    results.Add(new { path = filePath, success = false, message = ex.Message });
                    Console.Error.WriteLine($"[UAssetTool] Error processing {Path.GetFileName(filePath)}: {ex.Message}");
                    return (0, 0, 1);
                }
            }
            
            // Process files either in parallel or sequentially
            if (parallel && filePaths.Count > 1)
            {
                Console.Error.WriteLine($"[UAssetTool] Using parallel processing with {Environment.ProcessorCount} cores");
                var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };
                
                Parallel.ForEach(filePaths, parallelOptions, filePath =>
                {
                    var (s, sk, e) = ProcessSingleTexture(filePath);
                    Interlocked.Add(ref successCount, s);
                    Interlocked.Add(ref skipCount, sk);
                    Interlocked.Add(ref errorCount, e);
                });
            }
            else
            {
                // Sequential processing
                foreach (var filePath in filePaths)
                {
                    var (s, sk, e) = ProcessSingleTexture(filePath);
                    successCount += s;
                    skipCount += sk;
                    errorCount += e;
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
                    results = results.ToList()
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
    
    /// <summary>
    /// Pre-load all referenced assets to populate schema cache for proper JSON serialization.
    /// This fixes the "Failed to find a valid schema for parent name" error when converting
    /// Blueprint assets that inherit from classes defined in other asset files.
    /// </summary>
    private static void PreloadReferencedAssetsForSchemas(UAsset asset)
    {
        if (asset.Mappings == null || asset.Imports == null) return;
        
        int loadedCount = 0;
        var processedPaths = new HashSet<string>();
        
        // Iterate through all imports and try to load referenced assets
        foreach (var import in asset.Imports)
        {
            // Skip script imports (native classes)
            if (import.ClassPackage?.Value?.Value?.StartsWith("/Script") == true) continue;
            
            // Get the package path from the outer chain
            FName? packagePath = null;
            if (import.OuterIndex.IsImport())
            {
                var outer = import.OuterIndex.ToImport(asset);
                // Walk up the outer chain to find the package
                while (outer != null)
                {
                    if (outer.OuterIndex.Index == 0)
                    {
                        // This is the package
                        packagePath = outer.ObjectName;
                        break;
                    }
                    else if (outer.OuterIndex.IsImport())
                    {
                        outer = outer.OuterIndex.ToImport(asset);
                    }
                    else
                    {
                        break;
                    }
                }
            }
            
            if (packagePath != null && !processedPaths.Contains(packagePath.Value.Value))
            {
                processedPaths.Add(packagePath.Value.Value);
                
                // Try to load the referenced asset
                if (asset.PullSchemasFromAnotherAsset(packagePath, import.ObjectName))
                {
                    loadedCount++;
                }
            }
        }
        
        if (loadedCount > 0)
        {
            Console.Error.WriteLine($"[UAssetTool] Pre-loaded {loadedCount} referenced assets for schema resolution");
        }
        
        if (asset.OtherAssetsFailedToAccess.Count > 0)
        {
            Console.Error.WriteLine($"[UAssetTool] Warning: Could not find {asset.OtherAssetsFailedToAccess.Count} referenced assets on disk:");
            foreach (var missingPath in asset.OtherAssetsFailedToAccess)
            {
                Console.Error.WriteLine($"[UAssetTool]   - {missingPath.Value.Value}");
            }
            Console.Error.WriteLine($"[UAssetTool] To fix schema errors, extract these parent Blueprint assets to the same Content folder structure.");
        }
    }
    
    private static int CountOccurrences(byte[] data, byte[] pattern)
    {
        int count = 0;
        int end = data.Length - pattern.Length;
        for (int i = 0; i <= end; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (data[i + j] != pattern[j]) { match = false; break; }
            }
            if (match) { count++; i += pattern.Length - 1; }
        }
        return count;
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
        Console.Out.Flush(); // Ensure response is sent immediately
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
            byte[] zenData = ZenPackage.ZenConverter.ConvertLegacyToZen(filePath);
            
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
    
    /// <summary>
    /// Clone packages directly from game IoStore to create a mod IoStore.
    /// This preserves the exact Zen package structure without going through legacy conversion.
    /// </summary>
    private static int CliCloneModIoStore(string[] args)
    {
        if (args.Length < 4)
        {
            Console.Error.WriteLine("Usage: UAssetTool clone_mod_iostore <paks_path> <output_base> <package_paths...>");
            Console.Error.WriteLine("  Clones packages directly from game IoStore, preserving exact Zen structure.");
            Console.Error.WriteLine("  This avoids the legacy conversion which can add extra names to the name map.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Options:");
            Console.Error.WriteLine("  --compress            - Enable Oodle compression (default: enabled)");
            Console.Error.WriteLine("  --no-compress         - Disable compression");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Example:");
            Console.Error.WriteLine("  clone_mod_iostore \"E:\\Game\\Paks\" \"MyMod\" \"/Game/Marvel/Characters/1014/1014001/Meshes/SK_1014_1014001\"");
            return 1;
        }

        string paksPath = args[1];
        string outputBase = args[2];
        bool enableCompression = true;
        var packagePaths = new List<string>();

        for (int i = 3; i < args.Length; i++)
        {
            if (args[i] == "--compress")
                enableCompression = true;
            else if (args[i] == "--no-compress")
                enableCompression = false;
            else if (args[i].StartsWith("/Game/") || args[i].StartsWith("/Script/"))
                packagePaths.Add(args[i]);
            else
                Console.Error.WriteLine($"Warning: Ignoring unknown argument: {args[i]}");
        }

        if (packagePaths.Count == 0)
        {
            Console.Error.WriteLine("Error: No package paths provided");
            return 1;
        }

        if (!Directory.Exists(paksPath))
        {
            Console.Error.WriteLine($"Error: Paks directory not found: {paksPath}");
            return 1;
        }

        try
        {
            // Find all .utoc files
            var utocFiles = Directory.GetFiles(paksPath, "*.utoc", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f)
                .ToList();

            Console.Error.WriteLine($"[CloneModIoStore] Creating IoStore mod bundle: {outputBase}");
            Console.Error.WriteLine($"[CloneModIoStore]   Packages: {packagePaths.Count}");
            Console.Error.WriteLine($"[CloneModIoStore]   Compression: {(enableCompression ? "Oodle" : "None")}");
            Console.Error.WriteLine($"[CloneModIoStore]   Source containers: {utocFiles.Count}");

            // Parse AES key for Marvel Rivals
            byte[] aesKey = Convert.FromHexString("0C263D8C22DCB085894899C3A3796383E9BF9DE0CBFB08C9BF2DEF2E84F29D74");

            string utocPath = outputBase + ".utoc";
            string pakPath = outputBase + ".pak";
            string mountPoint = "../../../";

            using var ioStoreWriter = new IoStore.IoStoreWriter(
                utocPath,
                IoStore.EIoStoreTocVersion.PerfectHashWithOverflow,
                IoStore.EIoContainerHeaderVersion.NoExportInfo,
                mountPoint,
                enableCompression,
                false, // no encryption
                null);

            var filePaths = new List<string>();
            int successCount = 0;

            foreach (var packagePath in packagePaths)
            {
                Console.Error.WriteLine($"  Cloning: {packagePath}");

                var packageId = IoStore.FPackageId.FromName(packagePath);
                bool found = false;

                foreach (var utocFile in utocFiles)
                {
                    try
                    {
                        using var reader = new IoStore.IoStoreReader(utocFile, aesKey);
                        var chunkId = IoStore.FIoChunkId.FromPackageId(packageId, 0, IoStore.EIoChunkType.ExportBundleData);

                        if (reader.HasChunk(chunkId))
                        {
                            // Read original Zen data
                            byte[] zenData = reader.ReadChunk(chunkId);
                            Console.Error.WriteLine($"    Found in: {Path.GetFileName(utocFile)}");
                            Console.Error.WriteLine($"    Zen size: {zenData.Length} bytes");

                            // Parse the Zen header to get export count and imported packages
                            var zenHeader = ZenPackage.FZenPackageHeader.Deserialize(zenData, ZenPackage.EIoContainerHeaderVersion.NoExportInfo);

                            // Create store entry
                            var storeEntry = new IoStore.StoreEntry
                            {
                                ExportCount = zenHeader.ExportMap.Count,
                                ExportBundleCount = 1,
                                LoadOrder = 0
                            };

                            // Add imported packages
                            foreach (ulong importedPkgId in zenHeader.ImportedPackages)
                            {
                                storeEntry.ImportedPackages.Add(new IoStore.FPackageId(importedPkgId));
                            }
                            if (storeEntry.ImportedPackages.Count > 0)
                            {
                                Console.Error.WriteLine($"    Imported packages: {storeEntry.ImportedPackages.Count}");
                            }

                            // Convert package path to file path for directory index
                            // /Game/Marvel/Characters/... -> Marvel/Content/Marvel/Characters/...
                            string filePath;
                            if (packagePath.StartsWith("/Game/"))
                            {
                                filePath = "Marvel/Content/" + packagePath.Substring("/Game/".Length);
                            }
                            else
                            {
                                filePath = packagePath.TrimStart('/');
                            }

                            string fullPath = mountPoint + filePath + ".uasset";
                            ioStoreWriter.WritePackageChunk(chunkId, fullPath, zenData, storeEntry);
                            filePaths.Add(filePath + ".uasset");
                            filePaths.Add(filePath + ".uexp");

                            // Check for BulkData chunk
                            var bulkChunkId = IoStore.FIoChunkId.FromPackageId(packageId, 0, IoStore.EIoChunkType.BulkData);
                            if (reader.HasChunk(bulkChunkId))
                            {
                                byte[] bulkData = reader.ReadChunk(bulkChunkId);
                                string bulkFullPath = mountPoint + filePath + ".ubulk";
                                ioStoreWriter.WriteChunk(bulkChunkId, bulkFullPath, bulkData);
                                filePaths.Add(filePath + ".ubulk");
                                Console.Error.WriteLine($"    BulkData: {bulkData.Length} bytes");
                            }

                            found = true;
                            successCount++;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Silently continue to next container
                        _ = ex;
                    }
                }

                if (!found)
                {
                    Console.Error.WriteLine($"    ERROR: Package not found in any container");
                }
            }

            // Complete IoStore
            ioStoreWriter.Complete();

            // Create companion PAK with chunk names
            IoStore.ChunkNamesPakWriter.Create(pakPath, filePaths, mountPoint, 0, null);

            Console.Error.WriteLine();
            Console.Error.WriteLine($"SUCCESS: Created IoStore mod bundle:");
            Console.Error.WriteLine($"  {utocPath}");
            Console.Error.WriteLine($"  {Path.ChangeExtension(utocPath, ".ucas")}");
            Console.Error.WriteLine($"  {pakPath}");
            Console.Error.WriteLine($"  Packages cloned: {successCount}/{packagePaths.Count}");

            return successCount > 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
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
                    // Remove leading / or \
                    relativePath = relativePath.TrimStart('/', '\\');
                    
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
    
    #region PAK/IoStore JSON API Methods
    
    /// <summary>
    /// List all files in a PAK file
    /// </summary>
    private static UAssetResponse ListPakFiles(string? pakPath, string? aesKey)
    {
        if (string.IsNullOrEmpty(pakPath))
            return new UAssetResponse { Success = false, Message = "PAK path is required" };
        
        if (!File.Exists(pakPath))
            return new UAssetResponse { Success = false, Message = $"PAK file not found: {pakPath}" };
        
        try
        {
            using var pakReader = new IoStore.PakReader(pakPath, aesKey);
            
            var files = pakReader.Files.Select(f => new Dictionary<string, object?>
            {
                ["path"] = f,
                ["size"] = pakReader.GetEntry(f)?.UncompressedSize ?? 0,
                ["compressed_size"] = pakReader.GetEntry(f)?.CompressedSize ?? 0
            }).ToList();
            
            return new UAssetResponse
            {
                Success = true,
                Message = $"Found {files.Count} files in PAK",
                Data = new Dictionary<string, object?>
                {
                    ["file_count"] = files.Count,
                    ["mount_point"] = pakReader.MountPoint,
                    ["version"] = pakReader.Version,
                    ["encrypted_index"] = pakReader.EncryptedIndex,
                    ["files"] = files
                }
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Failed to read PAK: {ex.Message}" };
        }
    }
    
    /// <summary>
    /// Extract a single file from a PAK
    /// </summary>
    private static UAssetResponse ExtractPakFile(string? pakPath, string? internalPath, string? outputPath, string? aesKey)
    {
        if (string.IsNullOrEmpty(pakPath))
            return new UAssetResponse { Success = false, Message = "PAK path is required" };
        if (string.IsNullOrEmpty(internalPath))
            return new UAssetResponse { Success = false, Message = "Internal path is required" };
        if (string.IsNullOrEmpty(outputPath))
            return new UAssetResponse { Success = false, Message = "Output path is required" };
        
        if (!File.Exists(pakPath))
            return new UAssetResponse { Success = false, Message = $"PAK file not found: {pakPath}" };
        
        try
        {
            using var pakReader = new IoStore.PakReader(pakPath, aesKey);
            byte[] data = pakReader.Get(internalPath);
            
            string? dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            
            File.WriteAllBytes(outputPath, data);
            
            return new UAssetResponse
            {
                Success = true,
                Message = $"Extracted {internalPath} ({data.Length} bytes)",
                Data = new Dictionary<string, object?>
                {
                    ["output_path"] = outputPath,
                    ["size"] = data.Length
                }
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Failed to extract file: {ex.Message}" };
        }
    }
    
    /// <summary>
    /// Extract all files from a PAK to a directory
    /// </summary>
    private static UAssetResponse ExtractPakAll(string? pakPath, string? outputDir, string? aesKey)
    {
        if (string.IsNullOrEmpty(pakPath))
            return new UAssetResponse { Success = false, Message = "PAK path is required" };
        if (string.IsNullOrEmpty(outputDir))
            return new UAssetResponse { Success = false, Message = "Output directory is required" };
        
        if (!File.Exists(pakPath))
            return new UAssetResponse { Success = false, Message = $"PAK file not found: {pakPath}" };
        
        try
        {
            using var pakReader = new IoStore.PakReader(pakPath, aesKey);
            Directory.CreateDirectory(outputDir);
            
            int extracted = 0;
            int failed = 0;
            var extractedFiles = new List<string>();
            
            foreach (var file in pakReader.Files)
            {
                try
                {
                    byte[] data = pakReader.Get(file);
                    string outPath = Path.Combine(outputDir, file.Replace('/', Path.DirectorySeparatorChar));
                    
                    string? dir = Path.GetDirectoryName(outPath);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);
                    
                    File.WriteAllBytes(outPath, data);
                    extractedFiles.Add(file);
                    extracted++;
                }
                catch
                {
                    failed++;
                }
            }
            
            return new UAssetResponse
            {
                Success = true,
                Message = $"Extracted {extracted} files, {failed} failed",
                Data = new Dictionary<string, object?>
                {
                    ["extracted_count"] = extracted,
                    ["failed_count"] = failed,
                    ["output_dir"] = outputDir,
                    ["files"] = extractedFiles
                }
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Failed to extract PAK: {ex.Message}" };
        }
    }
    
    /// <summary>
    /// Create a PAK file from a list of files
    /// </summary>
    private static UAssetResponse CreatePakJson(string? outputPath, List<string>? filePaths, string? mountPoint, ulong pathHashSeed, string? aesKey)
    {
        if (string.IsNullOrEmpty(outputPath))
            return new UAssetResponse { Success = false, Message = "Output path is required" };
        if (filePaths == null || filePaths.Count == 0)
            return new UAssetResponse { Success = false, Message = "File paths are required" };
        
        try
        {
            mountPoint ??= "../../../";
            
            using var pakWriter = new IoStore.PakWriter(mountPoint, pathHashSeed, aesKey);
            
            foreach (var filePath in filePaths)
            {
                if (!File.Exists(filePath))
                    return new UAssetResponse { Success = false, Message = $"File not found: {filePath}" };
                
                string relativePath = Path.GetFileName(filePath);
                byte[] data = File.ReadAllBytes(filePath);
                pakWriter.AddEntry(relativePath, data);
            }
            
            pakWriter.Write(outputPath);
            
            return new UAssetResponse
            {
                Success = true,
                Message = $"Created PAK with {filePaths.Count} files",
                Data = new Dictionary<string, object?>
                {
                    ["output_path"] = outputPath,
                    ["file_count"] = filePaths.Count
                }
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Failed to create PAK: {ex.Message}" };
        }
    }
    
    /// <summary>
    /// Create a companion PAK file for IoStore bundles (contains chunknames)
    /// </summary>
    private static UAssetResponse CreateCompanionPakJson(string? outputPath, List<string>? filePaths, string? mountPoint, ulong pathHashSeed, string? aesKey)
    {
        if (string.IsNullOrEmpty(outputPath))
            return new UAssetResponse { Success = false, Message = "Output path is required" };
        if (filePaths == null || filePaths.Count == 0)
            return new UAssetResponse { Success = false, Message = "File paths are required" };
        
        try
        {
            mountPoint ??= "../../../";
            
            IoStore.ChunkNamesPakWriter.Create(outputPath, filePaths, mountPoint, pathHashSeed, aesKey);
            
            return new UAssetResponse
            {
                Success = true,
                Message = $"Created companion PAK listing {filePaths.Count} files",
                Data = new Dictionary<string, object?>
                {
                    ["output_path"] = outputPath,
                    ["file_count"] = filePaths.Count
                }
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Failed to create companion PAK: {ex.Message}" };
        }
    }
    
    /// <summary>
    /// List all packages in an IoStore container
    /// </summary>
    private static UAssetResponse ListIoStoreFiles(string? utocPath, string? aesKeyHex)
    {
        if (string.IsNullOrEmpty(utocPath))
            return new UAssetResponse { Success = false, Message = "UTOC path is required" };
        
        if (!File.Exists(utocPath))
            return new UAssetResponse { Success = false, Message = $"UTOC file not found: {utocPath}" };
        
        try
        {
            byte[]? aesKey = ParseAesKeyOrDefault(aesKeyHex);
            using var reader = new IoStore.IoStoreReader(utocPath, aesKey);
            
            // Get all chunks and their paths
            var chunks = reader.GetChunks()
                .Select((chunkId, idx) => new Dictionary<string, object?>
                {
                    ["index"] = idx,
                    ["chunk_type"] = chunkId.ChunkType.ToString(),
                    ["path"] = reader.GetChunkPath(chunkId)
                })
                .Where(c => c["path"] != null) // Only include chunks with paths
                .ToList();
            
            return new UAssetResponse
            {
                Success = true,
                Message = $"Found {chunks.Count} packages in IoStore",
                Data = new Dictionary<string, object?>
                {
                    ["package_count"] = chunks.Count,
                    ["container_name"] = reader.ContainerName,
                    ["files"] = chunks.Select(c => c["path"]).ToList()
                }
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Failed to read IoStore: {ex.Message}" };
        }
    }
    
    /// <summary>
    /// Parse AES key from hex string, or return null for default
    /// </summary>
    private static byte[]? ParseAesKeyOrDefault(string? aesKeyHex)
    {
        if (string.IsNullOrEmpty(aesKeyHex))
            return null; // Use default in IoStoreReader
        
        string hex = aesKeyHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? aesKeyHex[2..] : aesKeyHex;
        byte[] bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }
        return bytes;
    }
    
    /// <summary>
    /// Check if an IoStore container is compressed
    /// </summary>
    private static UAssetResponse IsIoStoreCompressed(string? utocPath)
    {
        if (string.IsNullOrEmpty(utocPath))
            return new UAssetResponse { Success = false, Message = "UTOC path is required" };
        
        if (!File.Exists(utocPath))
            return new UAssetResponse { Success = false, Message = $"UTOC file not found: {utocPath}" };
        
        try
        {
            bool isCompressed = IoStore.IoStoreReader.IsCompressed(utocPath);
            return new UAssetResponse
            {
                Success = true,
                Message = isCompressed ? "IoStore is compressed" : "IoStore is not compressed",
                Data = new Dictionary<string, object?> { ["compressed"] = isCompressed }
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Failed to check compression: {ex.Message}" };
        }
    }
    
    /// <summary>
    /// Check if an IoStore container is encrypted (obfuscated)
    /// </summary>
    private static UAssetResponse IsIoStoreEncrypted(string? utocPath)
    {
        if (string.IsNullOrEmpty(utocPath))
            return new UAssetResponse { Success = false, Message = "UTOC path is required" };
        
        if (!File.Exists(utocPath))
            return new UAssetResponse { Success = false, Message = $"UTOC file not found: {utocPath}" };
        
        try
        {
            bool isEncrypted = IoStore.IoStoreReader.IsEncrypted(utocPath);
            return new UAssetResponse
            {
                Success = true,
                Message = isEncrypted ? "IoStore is encrypted" : "IoStore is not encrypted",
                Data = new Dictionary<string, object?> { ["encrypted"] = isEncrypted }
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Failed to check encryption: {ex.Message}" };
        }
    }

    /// <summary>
    /// Recompress an IoStore container with Oodle
    /// </summary>
    private static UAssetResponse RecompressIoStore(string? utocPath)
    {
        if (string.IsNullOrEmpty(utocPath))
            return new UAssetResponse { Success = false, Message = "UTOC path is required" };
        
        if (!File.Exists(utocPath))
            return new UAssetResponse { Success = false, Message = $"UTOC file not found: {utocPath}" };
        
        try
        {
            // Check if already compressed
            if (IoStore.IoStoreReader.IsCompressed(utocPath))
            {
                return new UAssetResponse
                {
                    Success = true,
                    Message = "IoStore is already compressed",
                    Data = new Dictionary<string, object?> { ["already_compressed"] = true }
                };
            }
            
            string result = IoStore.IoStoreRecompressor.Recompress(utocPath);
            return new UAssetResponse
            {
                Success = true,
                Message = $"Recompressed IoStore: {result}",
                Data = new Dictionary<string, object?> { ["output_path"] = result }
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Failed to recompress: {ex.Message}" };
        }
    }
    
    /// <summary>
    /// Extract IoStore to legacy format
    /// </summary>
    private static UAssetResponse ExtractIoStoreJson(string? utocPath, string? outputDir, string? aesKeyHex)
    {
        if (string.IsNullOrEmpty(utocPath))
            return new UAssetResponse { Success = false, Message = "UTOC path is required" };
        if (string.IsNullOrEmpty(outputDir))
            return new UAssetResponse { Success = false, Message = "Output directory is required" };
        
        if (!File.Exists(utocPath))
            return new UAssetResponse { Success = false, Message = $"UTOC file not found: {utocPath}" };
        
        try
        {
            Directory.CreateDirectory(outputDir);
            
            // Create package context for proper Zen-to-Legacy conversion
            using var context = new ZenPackage.FZenPackageContext();
            
            // Set AES key
            string aesKey = string.IsNullOrEmpty(aesKeyHex) 
                ? "0C263D8C22DCB085894899C3A3796383E9BF9DE0CBFB08C9BF2DEF2E84F29D74" 
                : aesKeyHex;
            context.SetAesKey(aesKey);
            
            // Check if this is a mod in ~mods folder - need to load game containers for import resolution
            string? paksDir = null;
            string utocDir = Path.GetDirectoryName(utocPath) ?? "";
            
            // Walk up to find ~mods folder and get base paks directory
            string? current = utocDir;
            while (!string.IsNullOrEmpty(current))
            {
                string dirName = Path.GetFileName(current) ?? "";
                if (dirName.Equals("~mods", StringComparison.OrdinalIgnoreCase))
                {
                    paksDir = Path.GetDirectoryName(current);
                    break;
                }
                current = Path.GetDirectoryName(current);
            }
            
            // If we found a paks directory, load game containers first for import resolution
            if (!string.IsNullOrEmpty(paksDir) && Directory.Exists(paksDir))
            {
                // Load global.utoc first for script objects
                string globalPath = Path.Combine(paksDir, "global.utoc");
                if (File.Exists(globalPath))
                {
                    context.LoadContainer(globalPath);
                    context.LoadScriptObjectsFromContainer(0);
                }
                
                // Load other game containers
                var gameUtocs = Directory.GetFiles(paksDir, "*.utoc", SearchOption.TopDirectoryOnly)
                    .Where(f => !f.EndsWith("global.utoc", StringComparison.OrdinalIgnoreCase))
                    .Where(f => !f.Contains("optional", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f)
                    .ToList();
                
                foreach (var gameUtoc in gameUtocs)
                {
                    try { context.LoadContainer(gameUtoc); }
                    catch { /* Skip failed containers */ }
                }
            }
            
            // Load the target mod container with priority so it overrides game packages
            int modContainerIndex = context.ContainerCount;
            context.LoadContainerWithPriority(utocPath);
            
            int converted = 0;
            int failed = 0;
            var extractedFiles = new List<string>();
            
            // Get all packages from the mod container only
            var packageIds = context.GetPackageIdsFromContainer(modContainerIndex);
            
            foreach (var packageId in packageIds)
            {
                string? fullPath = context.GetPackagePath(packageId);
                var cached = context.GetCachedPackage(packageId);
                if (cached == null) continue;
                
                string packageName = !string.IsNullOrEmpty(fullPath) ? fullPath : cached.Header.PackageName();
                
                try
                {
                    // Convert to legacy format using ZenToLegacyConverter
                    var converter = new ZenPackage.ZenToLegacyConverter(context, packageId);
                    var legacyBundle = converter.Convert();
                    
                    // Normalize path for output
                    string relPath = packageName;
                    
                    // Resolve /../ patterns
                    if (relPath.Contains("/../"))
                    {
                        string tempRoot = Path.GetTempPath();
                        string tempPath = Path.Combine(tempRoot, relPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                        string resolved = Path.GetFullPath(tempPath);
                        relPath = resolved.Substring(tempRoot.Length).Replace(Path.DirectorySeparatorChar, '/');
                        if (!relPath.StartsWith("/"))
                            relPath = "/" + relPath;
                        int contentIdx = relPath.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
                        if (contentIdx >= 0)
                            relPath = "/Game" + relPath.Substring(contentIdx + "/Content".Length);
                    }
                    
                    // Remove /Game/ prefix
                    if (relPath.StartsWith("/Game/"))
                        relPath = relPath.Substring(6);
                    else if (relPath.StartsWith("/"))
                        relPath = relPath.Substring(1);
                    
                    relPath = relPath.Replace('/', Path.DirectorySeparatorChar);
                    if (!relPath.EndsWith(".uasset"))
                        relPath += ".uasset";
                    
                    string outputAssetPath = Path.Combine(outputDir, relPath);
                    string? outputAssetDir = Path.GetDirectoryName(outputAssetPath);
                    if (!string.IsNullOrEmpty(outputAssetDir))
                        Directory.CreateDirectory(outputAssetDir);
                    
                    // Write .uasset
                    File.WriteAllBytes(outputAssetPath, legacyBundle.AssetData);
                    extractedFiles.Add(relPath);
                    
                    // Write .uexp
                    string outputUexpPath = Path.ChangeExtension(outputAssetPath, ".uexp");
                    File.WriteAllBytes(outputUexpPath, legacyBundle.ExportsData);
                    extractedFiles.Add(Path.ChangeExtension(relPath, ".uexp"));
                    
                    // Write .ubulk if present
                    if (legacyBundle.BulkData != null && legacyBundle.BulkData.Length > 0)
                    {
                        string outputBulkPath = Path.ChangeExtension(outputAssetPath, ".ubulk");
                        File.WriteAllBytes(outputBulkPath, legacyBundle.BulkData);
                        extractedFiles.Add(Path.ChangeExtension(relPath, ".ubulk"));
                    }
                    
                    converted++;
                }
                catch
                {
                    failed++;
                }
            }
            
            return new UAssetResponse
            {
                Success = true,
                Message = $"Extracted {converted} packages from IoStore ({failed} failed)",
                Data = new Dictionary<string, object?>
                {
                    ["extracted_count"] = converted,
                    ["failed_count"] = failed,
                    ["output_dir"] = outputDir,
                    ["files"] = extractedFiles
                }
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Failed to extract IoStore: {ex.Message}" };
        }
    }
    
    /// <summary>
    /// Extract ScriptObjects.bin from game paks
    /// </summary>
    private static UAssetResponse ExtractScriptObjectsJson(string? paksPath, string? outputPath)
    {
        if (string.IsNullOrEmpty(paksPath))
            return new UAssetResponse { Success = false, Message = "Paks path is required" };
        if (string.IsNullOrEmpty(outputPath))
            return new UAssetResponse { Success = false, Message = "Output path is required" };
        
        if (!Directory.Exists(paksPath))
            return new UAssetResponse { Success = false, Message = $"Paks directory not found: {paksPath}" };
        
        try
        {
            byte[]? data = IoStore.IoStoreReader.ExtractScriptObjects(paksPath);
            if (data == null)
            {
                return new UAssetResponse { Success = false, Message = "ScriptObjects not found in any IoStore container" };
            }
            
            string? dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            
            File.WriteAllBytes(outputPath, data);
            
            return new UAssetResponse
            {
                Success = true,
                Message = $"Extracted ScriptObjects.bin ({data.Length} bytes)",
                Data = new Dictionary<string, object?>
                {
                    ["output_path"] = outputPath,
                    ["size"] = data.Length
                }
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Failed to extract ScriptObjects: {ex.Message}" };
        }
    }
    
    /// <summary>
    /// Create a mod IoStore bundle from a directory of legacy assets.
    /// This is the JSON API equivalent of retoc's action_to_zen.
    /// Converts .uasset/.uexp files to Zen format and creates .utoc/.ucas/.pak bundle.
    /// </summary>
    private static UAssetResponse CreateModIoStoreJson(string? outputPath, string? inputDir, string? mountPoint, bool compress, string? aesKey, bool parallel, bool obfuscate)
    {
        if (string.IsNullOrEmpty(outputPath))
            return new UAssetResponse { Success = false, Message = "Output path is required" };
        if (string.IsNullOrEmpty(inputDir))
            return new UAssetResponse { Success = false, Message = "Input directory is required" };
        
        // Marvel Rivals AES key for obfuscation
        const string MARVEL_RIVALS_AES_KEY = "0C263D8C22DCB085894899C3A3796383E9BF9DE0CBFB08C9BF2DEF2E84F29D74";
        
        // If obfuscate is enabled, use the game's AES key
        if (obfuscate)
        {
            aesKey = MARVEL_RIVALS_AES_KEY;
        }
        
        if (!Directory.Exists(inputDir))
            return new UAssetResponse { Success = false, Message = $"Input directory not found: {inputDir}" };
        
        try
        {
            // Collect all uasset files
            var uassetFiles = Directory.GetFiles(inputDir, "*.uasset", SearchOption.AllDirectories).ToList();
            
            if (uassetFiles.Count == 0)
                return new UAssetResponse { Success = false, Message = "No .uasset files found in input directory" };
            
            // Determine output paths
            string outputBase = outputPath.EndsWith(".utoc") ? outputPath.Substring(0, outputPath.Length - 5) : outputPath;
            string utocPath = outputBase + ".utoc";
            string pakPath = outputBase + ".pak";
            string mount = string.IsNullOrEmpty(mountPoint) ? "../../../" : mountPoint;
            
            // Thread count based on parallel flag: 75% when enabled, 50% otherwise
            int threadCount = parallel 
                ? Math.Max(1, (Environment.ProcessorCount * 3) / 4)  // 75% of cores
                : Math.Max(1, Environment.ProcessorCount / 2);       // 50% of cores
            Console.Error.WriteLine($"[CreateModIoStore] Processing {uassetFiles.Count} files using {threadCount} threads (parallel={parallel})...");
            Console.Error.Flush();
            
            // Phase 1: Parallel conversion to Zen format (CPU-intensive)
            var conversionResults = new System.Collections.Concurrent.ConcurrentBag<(string assetName, string uassetPath, byte[]? zenData, string packagePath, ZenPackage.FZenPackage? zenPackage, byte[]? ubulkData, string? error)>();
            int processedCount = 0;
            
            Parallel.ForEach(uassetFiles, new ParallelOptions { MaxDegreeOfParallelism = threadCount }, uassetPath =>
            {
                string assetName = Path.GetFileNameWithoutExtension(uassetPath);
                try
                {
                    var (zenData, packagePath, zenPackage) = ZenPackage.ZenConverter.ConvertLegacyToZenFull(
                        uassetPath, containerVersion: ZenPackage.EIoContainerHeaderVersion.NoExportInfo);
                    
                    byte[]? ubulkData = null;
                    string ubulkPath = Path.ChangeExtension(uassetPath, ".ubulk");
                    if (File.Exists(ubulkPath)) ubulkData = File.ReadAllBytes(ubulkPath);
                    
                    conversionResults.Add((assetName, uassetPath, zenData, packagePath, zenPackage, ubulkData, null));
                    
                    int count = Interlocked.Increment(ref processedCount);
                    if (count % 50 == 0 || count == uassetFiles.Count)
                    {
                        Console.Error.WriteLine($"[CreateModIoStore] Converted {count}/{uassetFiles.Count}...");
                        Console.Error.Flush();
                    }
                }
                catch (Exception ex)
                {
                    conversionResults.Add((assetName, uassetPath, null, "", null, null, ex.Message));
                    Interlocked.Increment(ref processedCount);
                }
            });
            
            Console.Error.WriteLine($"[CreateModIoStore] Parallel conversion done. Writing IoStore...");
            Console.Error.Flush();
            
            // Phase 2: Sequential write to IoStore
            using var ioStoreWriter = new IoStore.IoStoreWriter(
                utocPath,
                IoStore.EIoStoreTocVersion.PerfectHashWithOverflow,
                IoStore.EIoContainerHeaderVersion.NoExportInfo,
                mount,
                compress,
                !string.IsNullOrEmpty(aesKey),
                aesKey);
            
            var filePaths = new List<string>();
            int converted = 0;
            var errors = new List<string>();
            
            foreach (var result in conversionResults)
            {
                if (result.error != null || result.zenData == null)
                {
                    if (result.error != null) errors.Add($"{result.assetName}: {result.error}");
                    continue;
                }
                
                try
                {
                    var (assetName, uassetPath, zenData, packagePath, zenPackage, ubulkData, _) = result;
                    
                    // Create package ID using the /Game/... format
                    string gamePackagePath;
                    if (packagePath.StartsWith("Marvel/Content/"))
                    {
                        gamePackagePath = "/Game/" + packagePath.Substring("Marvel/Content/".Length);
                    }
                    else
                    {
                        gamePackagePath = "/" + packagePath;
                    }
                    
                    var packageId = IoStore.FPackageId.FromName(gamePackagePath);
                    var chunkId = IoStore.FIoChunkId.FromPackageId(packageId, 0, IoStore.EIoChunkType.ExportBundleData);
                    
                    // Create store entry with imported packages
                    var storeEntry = new IoStore.StoreEntry
                    {
                        ExportCount = zenPackage.ExportMap.Count,
                        ExportBundleCount = 1,
                        LoadOrder = 0
                    };
                    
                    foreach (ulong importedPkgId in zenPackage.ImportedPackages)
                    {
                        storeEntry.ImportedPackages.Add(new IoStore.FPackageId(importedPkgId));
                    }
                    
                    // Write to IoStore
                    string fullPath = mount + packagePath + ".uasset";
                    ioStoreWriter.WritePackageChunk(chunkId, fullPath, zenData, storeEntry);
                    
                    // Add to chunknames
                    filePaths.Add(packagePath + ".uasset");
                    filePaths.Add(packagePath + ".uexp");
                    
                    // Handle .ubulk if exists (already loaded during parallel phase)
                    if (ubulkData != null)
                    {
                        var bulkChunkId = IoStore.FIoChunkId.FromPackageId(packageId, 0, IoStore.EIoChunkType.BulkData);
                        string bulkFullPath = mount + packagePath + ".ubulk";
                        ioStoreWriter.WriteChunk(bulkChunkId, bulkFullPath, ubulkData);
                        filePaths.Add(packagePath + ".ubulk");
                    }
                    
                    converted++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{result.assetName}: {ex.Message}");
                }
            }
            
            if (converted == 0)
            {
                // Clean up empty files left by IoStoreWriter constructor
                ioStoreWriter.Dispose();
                try { File.Delete(utocPath); } catch { }
                try { File.Delete(Path.ChangeExtension(utocPath, ".ucas")); } catch { }
                return new UAssetResponse { Success = false, Message = $"No assets were converted. Errors: {string.Join("; ", errors)}" };
            }
            
            ioStoreWriter.Complete();
            
            // Create companion PAK with chunknames
            IoStore.ChunkNamesPakWriter.Create(pakPath, filePaths, mount, 0, aesKey);
            
            return new UAssetResponse
            {
                Success = true,
                Message = $"Created IoStore mod bundle with {converted} assets",
                Data = new Dictionary<string, object?>
                {
                    ["utoc_path"] = utocPath,
                    ["ucas_path"] = Path.ChangeExtension(utocPath, ".ucas"),
                    ["pak_path"] = pakPath,
                    ["converted_count"] = converted,
                    ["file_count"] = filePaths.Count,
                    ["errors"] = errors.Count > 0 ? errors : null
                }
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Failed to create mod IoStore: {ex.Message}" };
        }
    }
    
    /// <summary>
    /// Create an IoStore bundle from a directory of legacy assets
    /// </summary>
    private static UAssetResponse CreateIoStoreJson(string? outputPath, string? inputDir, bool compress, string? aesKey)
    {
        if (string.IsNullOrEmpty(outputPath))
            return new UAssetResponse { Success = false, Message = "Output path is required" };
        if (string.IsNullOrEmpty(inputDir))
            return new UAssetResponse { Success = false, Message = "Input directory is required" };
        
        if (!Directory.Exists(inputDir))
            return new UAssetResponse { Success = false, Message = $"Input directory not found: {inputDir}" };
        
        try
        {
            // Collect all uasset files
            var uassetFiles = Directory.GetFiles(inputDir, "*.uasset", SearchOption.AllDirectories).ToList();
            
            if (uassetFiles.Count == 0)
                return new UAssetResponse { Success = false, Message = "No .uasset files found in input directory" };
            
            string utocPath = outputPath.EndsWith(".utoc") ? outputPath : outputPath + ".utoc";
            string pakPath = Path.ChangeExtension(utocPath, ".pak");
            
            // Convert each asset to Zen format and write to IoStore
            using var ioStoreWriter = new IoStore.IoStoreWriter(
                utocPath,
                IoStore.EIoStoreTocVersion.PerfectHashWithOverflow,
                IoStore.EIoContainerHeaderVersion.NoExportInfo,
                "../../../",
                compress,
                false,
                aesKey);
            
            var filePaths = new List<string>();
            int converted = 0;
            
            foreach (var uassetPath in uassetFiles)
            {
                try
                {
                    byte[] zenData = ZenPackage.ZenConverter.ConvertLegacyToZen(uassetPath);
                    
                    // Get relative path for the package
                    string relativePath = Path.GetRelativePath(inputDir, uassetPath);
                    string packagePath = "/" + Path.ChangeExtension(relativePath, null).Replace('\\', '/');
                    
                    // Create chunk ID from package path and write
                    var packageId = IoStore.FPackageId.FromName(packagePath);
                    var chunkId = IoStore.FIoChunkId.FromPackageId(packageId, 0, IoStore.EIoChunkType.ExportBundleData);
                    ioStoreWriter.WriteChunk(chunkId, packagePath, zenData);
                    
                    filePaths.Add(relativePath.Replace('\\', '/'));
                    converted++;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[CreateIoStore] Failed to convert {uassetPath}: {ex.Message}");
                }
            }
            
            ioStoreWriter.Complete();
            
            // Create companion PAK
            IoStore.ChunkNamesPakWriter.Create(pakPath, filePaths, "../../../", 0, aesKey);
            
            return new UAssetResponse
            {
                Success = true,
                Message = $"Created IoStore bundle with {converted} assets",
                Data = new Dictionary<string, object?>
                {
                    ["utoc_path"] = utocPath,
                    ["ucas_path"] = Path.ChangeExtension(utocPath, ".ucas"),
                    ["pak_path"] = pakPath,
                    ["asset_count"] = converted,
                    ["files"] = filePaths
                }
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Failed to create IoStore: {ex.Message}" };
        }
    }
    
    #endregion
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
    
    // PAK/IoStore operations
    [JsonPropertyName("aes_key")]
    public string? AesKey { get; set; }
    
    [JsonPropertyName("internal_path")]
    public string? InternalPath { get; set; }
    
    [JsonPropertyName("mount_point")]
    public string? MountPoint { get; set; }
    
    [JsonPropertyName("path_hash_seed")]
    public ulong PathHashSeed { get; set; } = 0;
    
    [JsonPropertyName("input_dir")]
    public string? InputDir { get; set; }
    
    [JsonPropertyName("compress")]
    public bool Compress { get; set; } = true;
    
    [JsonPropertyName("filter_patterns")]
    public List<string>? FilterPatterns { get; set; }
    
    /// <summary>
    /// Enable parallel processing for batch operations
    /// </summary>
    [JsonPropertyName("parallel")]
    public bool Parallel { get; set; } = false;
    
    /// <summary>
    /// Enable obfuscation (encrypts with game's AES key to block extraction tools like FModel)
    /// </summary>
    [JsonPropertyName("obfuscate")]
    public bool Obfuscate { get; set; } = false;
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

public partial class Program
{
    private static int CliSkeletalMeshInfo(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: UAssetTool skeletal_mesh_info <uasset_path> <usmap_path>");
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
        
        Console.WriteLine($"=== Skeletal Mesh Analysis: {Path.GetFileName(uassetPath)} ===\n");
        
        foreach (var export in asset.Exports)
        {
            if (export is SkeletalMeshExport skelMesh)
            {
                // Ensure extra data is parsed (lazy parsing since Extras is populated after Read)
                skelMesh.EnsureExtraDataParsed();
                
                Console.WriteLine($"Export: {export.ObjectName?.Value?.Value ?? "Unknown"}");
                Console.WriteLine($"  ExtraDataParsed: {skelMesh.ExtraDataParsed}");
                Console.WriteLine($"  Extras Length: {skelMesh.Extras?.Length ?? 0} bytes");
                
                // Strip Flags
                if (skelMesh.StripFlags != null)
                {
                    Console.WriteLine($"\n  === Strip Flags ===");
                    Console.WriteLine($"    GlobalStripFlags: 0x{skelMesh.StripFlags.GlobalStripFlags:X2}");
                    Console.WriteLine($"    ClassStripFlags: 0x{skelMesh.StripFlags.ClassStripFlags:X2}");
                    Console.WriteLine($"    IsEditorDataStripped: {skelMesh.StripFlags.IsEditorDataStripped()}");
                    Console.WriteLine($"    IsDataStrippedForServer: {skelMesh.StripFlags.IsDataStrippedForServer()}");
                }
                
                // Bounds
                if (skelMesh.ImportedBounds != null)
                {
                    Console.WriteLine($"\n  === Imported Bounds ===");
                    Console.WriteLine($"    Origin: ({skelMesh.ImportedBounds.Origin.X:F2}, {skelMesh.ImportedBounds.Origin.Y:F2}, {skelMesh.ImportedBounds.Origin.Z:F2})");
                    Console.WriteLine($"    BoxExtent: ({skelMesh.ImportedBounds.BoxExtent.X:F2}, {skelMesh.ImportedBounds.BoxExtent.Y:F2}, {skelMesh.ImportedBounds.BoxExtent.Z:F2})");
                    Console.WriteLine($"    SphereRadius: {skelMesh.ImportedBounds.SphereRadius:F2}");
                }
                
                // Materials
                if (skelMesh.Materials != null && skelMesh.Materials.Count > 0)
                {
                    Console.WriteLine($"\n  === Materials ({skelMesh.Materials.Count}) ===");
                    for (int i = 0; i < skelMesh.Materials.Count; i++)
                    {
                        var mat = skelMesh.Materials[i];
                        Console.WriteLine($"    [{i}] MaterialInterface: {mat.MaterialInterface.Index}");
                        Console.WriteLine($"        SlotName: {mat.MaterialSlotName?.Value?.Value ?? "None"}");
                        Console.WriteLine($"        ImportedSlotName: {mat.ImportedMaterialSlotName?.Value?.Value ?? "None"}");
                        Console.WriteLine($"        GameplayTags: {mat.GameplayTagContainer?.GameplayTags?.Count ?? 0}");
                        if (mat.GameplayTagContainer?.GameplayTags != null && mat.GameplayTagContainer.GameplayTags.Count > 0)
                        {
                            foreach (var tag in mat.GameplayTagContainer.GameplayTags)
                            {
                                Console.WriteLine($"          - {tag.TagName?.Value?.Value ?? "Unknown"}");
                            }
                        }
                    }
                }
                
                // Reference Skeleton
                if (skelMesh.ReferenceSkeleton != null)
                {
                    Console.WriteLine($"\n  === Reference Skeleton ===");
                    Console.WriteLine($"    Bone Count: {skelMesh.ReferenceSkeleton.BoneCount}");
                    Console.WriteLine($"    RefBonePose Count: {skelMesh.ReferenceSkeleton.RefBonePose.Count}");
                    Console.WriteLine($"    NameToIndexMap Count: {skelMesh.ReferenceSkeleton.NameToIndexMap.Count}");
                    
                    // Show first few bones
                    Console.WriteLine($"\n    First 10 Bones:");
                    for (int i = 0; i < Math.Min(10, skelMesh.ReferenceSkeleton.BoneCount); i++)
                    {
                        var bone = skelMesh.ReferenceSkeleton.RefBoneInfo[i];
                        Console.WriteLine($"      [{i}] {bone.Name?.Value?.Value ?? "Unknown"} (Parent: {bone.ParentIndex})");
                    }
                }
                
                // LOD Info
                Console.WriteLine($"\n  === LOD Info ===");
                Console.WriteLine($"    bCooked: {skelMesh.bCooked}");
                Console.WriteLine($"    LODCount: {skelMesh.LODCount}");
                
                // Remaining Extra Data Analysis
                if (skelMesh.RemainingExtraData != null && skelMesh.RemainingExtraData.Length > 0)
                {
                    Console.WriteLine($"\n  === Remaining Extra Data ({skelMesh.RemainingExtraData.Length} bytes) ===");
                    Console.WriteLine($"    This contains LOD render data (sections, vertices, indices, etc.)");
                    Console.WriteLine($"    First 64 bytes (hex): {BitConverter.ToString(skelMesh.RemainingExtraData.Take(Math.Min(64, skelMesh.RemainingExtraData.Length)).ToArray()).Replace("-", " ")}");
                }
                
                // Since ExtraDataParsed failed, analyze raw Extras directly
                if (!skelMesh.ExtraDataParsed && skelMesh.Extras != null && skelMesh.Extras.Length > 0)
                {
                    Console.WriteLine($"\n  === Raw Extras Analysis ({skelMesh.Extras.Length} bytes) ===");
                    Console.WriteLine($"    First 128 bytes (hex):");
                    for (int row = 0; row < 8 && row * 16 < skelMesh.Extras.Length; row++)
                    {
                        int offset = row * 16;
                        string hex = BitConverter.ToString(skelMesh.Extras.Skip(offset).Take(16).ToArray()).Replace("-", " ");
                        Console.WriteLine($"      0x{offset:X4}: {hex}");
                    }
                    
                    // Try to find section data patterns in the raw extras
                    // Look for FSkelMeshRenderSection patterns
                    Console.WriteLine($"\n    Searching for section patterns...");
                    AnalyzeSkeletalMeshSections(skelMesh.Extras, asset);
                }
                
                // Also dump tagged properties that might control visibility
                if (export is NormalExport normalExport && normalExport.Data != null)
                {
                    Console.WriteLine($"\n  === All Tagged Properties ({normalExport.Data.Count}) ===");
                    foreach (var prop in normalExport.Data)
                    {
                        string propName = prop.Name?.Value?.Value ?? "Unknown";
                        Console.WriteLine($"    {propName}: {prop.GetType().Name}");
                        
                        // Dump LODInfo array in detail
                        if (propName == "LODInfo" && prop is ArrayPropertyData lodInfoArray)
                        {
                            Console.WriteLine($"\n  === LODInfo Details ({lodInfoArray.Value?.Length ?? 0} LODs) ===");
                            if (lodInfoArray.Value != null)
                            {
                                for (int lodIdx = 0; lodIdx < lodInfoArray.Value.Length; lodIdx++)
                                {
                                    Console.WriteLine($"    LOD {lodIdx}:");
                                    if (lodInfoArray.Value[lodIdx] is StructPropertyData lodStruct && lodStruct.Value != null)
                                    {
                                        foreach (var lodProp in lodStruct.Value)
                                        {
                                            string lodPropName = lodProp.Name?.Value?.Value ?? "?";
                                            Console.WriteLine($"      {lodPropName}: {lodProp.GetType().Name} = {GetPropertyValueString(lodProp)}");
                                        }
                                    }
                                }
                            }
                        }
                        
                        // Dump MeshClothingAssets
                        if (propName == "MeshClothingAssets" && prop is ArrayPropertyData clothArray)
                        {
                            Console.WriteLine($"\n  === MeshClothingAssets ({clothArray.Value?.Length ?? 0}) ===");
                            if (clothArray.Value != null)
                            {
                                for (int i = 0; i < clothArray.Value.Length; i++)
                                {
                                    Console.WriteLine($"    [{i}]: {clothArray.Value[i]?.GetType().Name}");
                                }
                            }
                        }
                    }
                }
                
                Console.WriteLine();
            }
        }
        
        return 0;
    }
    
    private static string GetPropertyValueString(PropertyData prop)
    {
        return prop switch
        {
            BoolPropertyData b => b.Value.ToString(),
            IntPropertyData i => i.Value.ToString(),
            FloatPropertyData f => f.Value.ToString("F4"),
            DoublePropertyData d => d.Value.ToString("F4"),
            StrPropertyData s => s.Value?.ToString() ?? "null",
            NamePropertyData n => n.Value?.Value?.Value ?? "null",
            ObjectPropertyData o => o.Value?.Index.ToString() ?? "null",
            ArrayPropertyData a => $"[{a.Value?.Length ?? 0} items]",
            StructPropertyData st => $"Struct({st.Value?.Count ?? 0} props)",
            BytePropertyData bp => bp.ByteType == BytePropertyType.Byte ? bp.Value.ToString() : bp.EnumValue?.Value?.Value ?? "null",
            EnumPropertyData e => e.Value?.Value?.Value ?? "null",
            _ => prop.GetType().Name
        };
    }
    
    
    private static void AnalyzeSkeletalMeshSections(byte[] data, UAsset asset)
    {
        // Per CUE4Parse, FSkelMeshRenderSection structure contains:
        // - MaterialIndex (uint16)
        // - BaseIndex (uint32)  
        // - NumTriangles (uint32)
        // - bRecomputeTangent (bool)
        // - RecomputeTangentsVertexMaskChannel (uint8)
        // - bCastShadow (bool)
        // - bVisibleInRayTracing (bool)
        // - BaseVertexIndex (uint32)
        // - ClothMappingDataLODs (array)
        // - BoneMap (array of uint16)
        // - NumVertices (int32)
        // - MaxBoneInfluences (int32)
        // - CorrespondClothAssetIndex (int16)
        // - ClothingData (FClothingSectionData)
        // - DuplicatedVerticesBuffer (FDuplicatedVerticesBuffer)
        // - bDisabled (bool)
        
        Console.WriteLine($"    Looking for FSkelMeshRenderSection patterns...");
        
        // Search for patterns that look like section data
        // MaterialIndex (0-50), followed by BaseIndex, NumTriangles
        int sectionsFound = 0;
        
        for (int i = 0; i < data.Length - 20; i++)
        {
            // Check for potential section start
            if (i + 14 >= data.Length) break;
            
            ushort matIdx = BitConverter.ToUInt16(data, i);
            uint baseIdx = BitConverter.ToUInt32(data, i + 2);
            uint numTris = BitConverter.ToUInt32(data, i + 6);
            
            // Heuristics: material index 0-20, reasonable base index and triangle count
            if (matIdx <= 20 && baseIdx < 10000000 && numTris > 10 && numTris < 500000)
            {
                // Check if there might be bool flags after
                byte possibleFlags = data[i + 10];
                
                // Look for bDisabled flag pattern - it's usually near the end of section
                // For now, just report potential sections
                if (sectionsFound < 20)
                {
                    Console.WriteLine($"      Potential section at 0x{i:X}: MatIdx={matIdx}, BaseIdx={baseIdx}, NumTris={numTris}");
                    
                    // Try to read more fields
                    if (i + 20 < data.Length)
                    {
                        byte bRecomputeTangent = data[i + 10];
                        byte recomputeChannel = data[i + 11];
                        byte bCastShadow = data[i + 12];
                        byte bVisibleInRayTracing = data[i + 13];
                        uint baseVertexIdx = BitConverter.ToUInt32(data, i + 14);
                        
                        Console.WriteLine($"        bRecomputeTangent={bRecomputeTangent}, bCastShadow={bCastShadow}, bVisibleInRayTracing={bVisibleInRayTracing}, BaseVertexIdx={baseVertexIdx}");
                    }
                }
                sectionsFound++;
            }
        }
        
        Console.WriteLine($"    Total potential sections found: {sectionsFound}");
        
        // Also look for bDisabled patterns (byte value 0 or 1 in specific contexts)
        Console.WriteLine($"\n    Looking for bDisabled flag patterns...");
        
        // Search for the string "bDisabled" or patterns that might indicate disabled sections
        // In cooked data, bools are typically single bytes
    }
    
    #region JSON Mode Parity Functions
    
    private static UAssetResponse DumpAssetJson(string? filePath, string? usmapPath)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "file_path is required" };
        
        try
        {
            var asset = new UAsset(filePath, EngineVersion.VER_UE5_3);
            if (!string.IsNullOrEmpty(usmapPath) && File.Exists(usmapPath))
            {
                asset.Mappings = new Usmap(usmapPath);
            }
            
            var result = new Dictionary<string, object>
            {
                ["file_path"] = filePath,
                ["package_flags"] = asset.PackageFlags.ToString(),
                ["has_unversioned_properties"] = asset.HasUnversionedProperties,
                ["name_count"] = asset.GetNameMapIndexList().Count,
                ["import_count"] = asset.Imports.Count,
                ["export_count"] = asset.Exports.Count,
                ["exports"] = asset.Exports.Select((e, i) => new Dictionary<string, object>
                {
                    ["index"] = i,
                    ["class_type"] = e.GetExportClassType()?.Value?.Value ?? "Unknown",
                    ["object_name"] = e.ObjectName?.Value?.Value ?? "Unknown",
                    ["serial_size"] = e.SerialSize,
                    ["serial_offset"] = e.SerialOffset
                }).ToList()
            };
            
            return new UAssetResponse 
            { 
                Success = true, 
                Message = "Asset dumped successfully",
                Data = result
            };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Failed to dump asset: {ex.Message}" };
        }
    }
    
    private static UAssetResponse GetSkeletalMeshInfoJson(string? filePath, string? usmapPath)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "file_path is required" };
        
        try
        {
            var asset = new UAsset(filePath, EngineVersion.VER_UE5_3);
            if (!string.IsNullOrEmpty(usmapPath) && File.Exists(usmapPath))
            {
                asset.Mappings = new Usmap(usmapPath);
            }
            
            var skExport = asset.Exports.OfType<UAssetAPI.ExportTypes.SkeletalMeshExport>().FirstOrDefault();
            if (skExport == null)
                return new UAssetResponse { Success = false, Message = "No SkeletalMesh export found" };
            
            var result = new Dictionary<string, object>
            {
                ["material_count"] = skExport.Materials?.Count ?? 0,
                ["bone_count"] = skExport.ReferenceSkeleton?.BoneCount ?? 0,
                ["materials"] = skExport.Materials?.Select((m, i) => new Dictionary<string, object>
                {
                    ["index"] = i,
                    ["slot_name"] = m.MaterialSlotName?.Value?.Value ?? "Unknown",
                    ["material_index"] = m.MaterialInterface?.Index ?? 0
                }).ToList() ?? new List<Dictionary<string, object>>()
            };
            
            return new UAssetResponse { Success = true, Data = result };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Failed to get SkeletalMesh info: {ex.Message}" };
        }
    }
    
    private static UAssetResponse ToJsonJson(string? filePath, string? usmapPath, string? outputPath)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "file_path is required" };
        
        try
        {
            var asset = new UAsset(filePath, EngineVersion.VER_UE5_3);
            if (!string.IsNullOrEmpty(usmapPath) && File.Exists(usmapPath))
            {
                asset.Mappings = new Usmap(usmapPath);
            }
            
            string jsonOutput = asset.SerializeJson(Newtonsoft.Json.Formatting.Indented);
            
            if (!string.IsNullOrEmpty(outputPath))
            {
                File.WriteAllText(outputPath, jsonOutput);
                return new UAssetResponse { Success = true, Message = $"JSON saved to {outputPath}" };
            }
            
            return new UAssetResponse { Success = true, Message = jsonOutput };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Failed to convert to JSON: {ex.Message}" };
        }
    }
    
    private static UAssetResponse FromJsonJson(string? jsonPath, string? outputPath, string? usmapPath)
    {
        if (string.IsNullOrEmpty(jsonPath))
            return new UAssetResponse { Success = false, Message = "file_path (JSON path) is required" };
        if (string.IsNullOrEmpty(outputPath))
            return new UAssetResponse { Success = false, Message = "output_path is required" };
        
        try
        {
            string jsonContent = File.ReadAllText(jsonPath);
            var asset = UAsset.DeserializeJson(jsonContent);
            
            if (!string.IsNullOrEmpty(usmapPath) && File.Exists(usmapPath))
            {
                asset.Mappings = new Usmap(usmapPath);
            }
            
            asset.Write(outputPath);
            return new UAssetResponse { Success = true, Message = $"Asset saved to {outputPath}" };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Failed to convert from JSON: {ex.Message}" };
        }
    }
    
    private static UAssetResponse CityHashJson(string? pathString)
    {
        if (string.IsNullOrEmpty(pathString))
            return new UAssetResponse { Success = false, Message = "file_path (path string to hash) is required" };
        
        ulong hash = IoStore.CityHash.CityHash64(pathString.ToLowerInvariant());
        return new UAssetResponse 
        { 
            Success = true, 
            Data = new Dictionary<string, object>
            {
                ["input"] = pathString,
                ["hash_decimal"] = hash,
                ["hash_hex"] = $"0x{hash:X16}"
            }
        };
    }
    
    private static UAssetResponse CloneModIoStoreJson(string? utocPath, string? outputPath)
    {
        if (string.IsNullOrEmpty(utocPath))
            return new UAssetResponse { Success = false, Message = "file_path (utoc path) is required" };
        if (string.IsNullOrEmpty(outputPath))
            return new UAssetResponse { Success = false, Message = "output_path is required" };
        
        try
        {
            // Use the existing CLI implementation
            string[] args = new[] { "clone_mod_iostore", utocPath, outputPath };
            int result = CliCloneModIoStore(args);
            
            if (result == 0)
                return new UAssetResponse { Success = true, Message = $"Cloned to {outputPath}" };
            else
                return new UAssetResponse { Success = false, Message = "Clone failed" };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Failed to clone: {ex.Message}" };
        }
    }
    
    private static UAssetResponse InspectZenJson(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return new UAssetResponse { Success = false, Message = "file_path is required" };
        
        try
        {
            byte[] data = File.ReadAllBytes(filePath);
            var zenHeader = ZenPackage.FZenPackageHeader.Deserialize(data, ZenPackage.EIoContainerHeaderVersion.NoExportInfo);
            
            var result = new Dictionary<string, object>
            {
                ["name_map_count"] = zenHeader.NameMap?.Count ?? 0,
                ["import_map_count"] = zenHeader.ImportMap?.Count ?? 0,
                ["export_map_count"] = zenHeader.ExportMap?.Count ?? 0,
                ["export_bundle_count"] = zenHeader.ExportBundleHeaders?.Count ?? 0,
                ["header_size"] = zenHeader.Summary?.HeaderSize ?? 0
            };
            
            return new UAssetResponse { Success = true, Data = result };
        }
        catch (Exception ex)
        {
            return new UAssetResponse { Success = false, Message = $"Failed to inspect Zen package: {ex.Message}" };
        }
    }
    
    #endregion
}

#endregion
