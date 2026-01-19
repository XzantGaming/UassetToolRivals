using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UAssetTool.IoStore;

/// <summary>
/// Creates companion PAK files for IoStore bundles.
/// The companion PAK contains a single "chunknames" entry listing all files in the IoStore.
/// This is required for the game's mod loader to recognize and mount the IoStore container.
/// 
/// Reference: repak-gui/src/install_mod/install_mod_logic/iotoc.rs lines 269-288
/// </summary>
public static class ChunkNamesPakWriter
{
    /// <summary>
    /// Create a companion PAK file for an IoStore bundle.
    /// </summary>
    /// <param name="pakPath">Output path for the .pak file</param>
    /// <param name="filePaths">List of file paths to include in chunknames</param>
    /// <param name="mountPoint">Mount point (default: "../../../")</param>
    /// <param name="pathHashSeed">Path hash seed (default: 0)</param>
    /// <param name="aesKeyHex">AES key in hex format (default: Marvel Rivals key)</param>
    public static void Create(
        string pakPath,
        IEnumerable<string> filePaths,
        string mountPoint = "../../../",
        ulong pathHashSeed = 0,
        string? aesKeyHex = null)
    {
        // Build chunknames content - newline-separated list of relative paths
        string chunkNamesContent = string.Join("\n", filePaths);
        byte[] chunkNamesBytes = Encoding.UTF8.GetBytes(chunkNamesContent);

        // Create PAK writer
        using var pakWriter = new PakWriter(mountPoint, pathHashSeed, aesKeyHex);

        // Add the chunknames entry (no compression)
        pakWriter.AddEntry("chunknames", chunkNamesBytes);

        // Write the PAK file
        pakWriter.Write(pakPath);

        Console.Error.WriteLine($"[ChunkNamesPakWriter] Created companion PAK: {pakPath}");
        Console.Error.WriteLine($"[ChunkNamesPakWriter]   Files listed: {chunkNamesContent.Split('\n').Length}");
    }

    /// <summary>
    /// Create a complete IoStore bundle (utoc + ucas + pak) from legacy assets.
    /// </summary>
    /// <param name="outputBasePath">Base path without extension (e.g., "C:/Mods/MyMod_P")</param>
    /// <param name="assets">Dictionary of relative paths to asset data</param>
    /// <param name="mountPoint">Mount point (default: "../../../")</param>
    /// <param name="pathHashSeed">Path hash seed (default: 0)</param>
    /// <param name="aesKeyHex">AES key in hex format (default: Marvel Rivals key)</param>
    public static void CreateIoStoreBundle(
        string outputBasePath,
        Dictionary<string, byte[]> assets,
        string mountPoint = "../../../",
        ulong pathHashSeed = 0,
        string? aesKeyHex = null)
    {
        string utocPath = outputBasePath + ".utoc";
        string pakPath = outputBasePath + ".pak";

        // Create IoStore container
        using var ioStoreWriter = new IoStoreWriter(
            utocPath,
            EIoStoreTocVersion.PerfectHashWithOverflow,
            EIoContainerHeaderVersion.OptionalSegmentPackages,
            mountPoint);

        var filePaths = new List<string>();

        foreach (var (relativePath, data) in assets)
        {
            // Create chunk ID from package name
            string packageName = relativePath;
            if (packageName.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
                packageName = packageName[..^7];
            else if (packageName.EndsWith(".uexp", StringComparison.OrdinalIgnoreCase))
                packageName = packageName[..^5];

            var packageId = FPackageId.FromName("/" + packageName.Replace('\\', '/'));
            var chunkId = FIoChunkId.FromPackageId(packageId, 0, EIoChunkType.ExportBundleData);

            // Create store entry
            var storeEntry = new StoreEntry
            {
                ExportCount = 1,
                ExportBundleCount = 1,
                LoadOrder = 0
            };

            // Write chunk
            string fullPath = mountPoint + relativePath.Replace('\\', '/');
            ioStoreWriter.WritePackageChunk(chunkId, fullPath, data, storeEntry);

            filePaths.Add(relativePath.Replace('\\', '/'));
        }

        // Complete IoStore
        ioStoreWriter.Complete();

        // Create companion PAK
        Create(pakPath, filePaths, mountPoint, pathHashSeed, aesKeyHex);

        Console.Error.WriteLine($"[CreateIoStoreBundle] Created complete IoStore bundle:");
        Console.Error.WriteLine($"[CreateIoStoreBundle]   {utocPath}");
        Console.Error.WriteLine($"[CreateIoStoreBundle]   {Path.ChangeExtension(utocPath, ".ucas")}");
        Console.Error.WriteLine($"[CreateIoStoreBundle]   {pakPath}");
    }
}
