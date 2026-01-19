using System;
using System.Collections.Generic;
using System.IO;

namespace UAssetTool.IoStore;

/// <summary>
/// IoStore recompressor - recompresses IoStore containers with Oodle compression
/// Equivalent to retoc::recompress_iostore()
/// </summary>
public static class IoStoreRecompressor
{
    private const string DEFAULT_AES_KEY_HEX = "0C263D8C22DCB085894899C3A3796383E9BF9DE0CBFB08C9BF2DEF2E84F29D74";

    /// <summary>
    /// Recompress an IoStore container by reading all chunks and writing them back with Oodle compression.
    /// This is useful for mods that have uncompressed .ucas files.
    /// </summary>
    /// <param name="utocPath">Path to the .utoc file</param>
    /// <param name="aesKeyHex">AES key in hex format (optional, defaults to Marvel Rivals key)</param>
    /// <returns>Path to the recompressed .utoc file</returns>
    public static string Recompress(string utocPath, string? aesKeyHex = null)
    {
        aesKeyHex ??= DEFAULT_AES_KEY_HEX;
        byte[] aesKey = ParseAesKey(aesKeyHex);

        string basePath = Path.ChangeExtension(utocPath, null);
        string ucasPath = basePath + ".ucas";
        string tempUtocPath = basePath + ".utoc.tmp";
        string tempUcasPath = basePath + ".ucas.tmp";

        Console.Error.WriteLine($"[IoStoreRecompressor] Recompressing: {Path.GetFileName(utocPath)}");

        // Read original IoStore
        using var reader = new IoStoreReader(utocPath, aesKey);
        var toc = reader.Toc;

        // Collect all chunks with their data
        var chunks = new List<(FIoChunkId ChunkId, string? Path, byte[] Data)>();
        int chunkIndex = 0;
        foreach (var chunkId in reader.GetChunks())
        {
            try
            {
                byte[] data = reader.ReadChunk(chunkId);
                string? path = reader.GetChunkPath(chunkId);
                chunks.Add((chunkId, path, data));
                chunkIndex++;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[IoStoreRecompressor] Warning: Failed to read chunk {chunkIndex}: {ex.Message}");
            }
        }

        Console.Error.WriteLine($"[IoStoreRecompressor] Read {chunks.Count} chunks, writing with Oodle compression...");

        // Create new IoStore with Oodle compression (enableCompression: true)
        using (var writer = new IoStoreWriter(
            tempUtocPath,
            toc.Version,
            EIoContainerHeaderVersion.OptionalSegmentPackages,
            toc.MountPoint,
            enableCompression: true,
            enableEncryption: false,
            aesKeyHex: aesKeyHex))
        {
            foreach (var (chunkId, path, data) in chunks)
            {
                // Determine store entry based on chunk type
                var storeEntry = new StoreEntry
                {
                    ExportCount = 1,
                    ExportBundleCount = 1,
                    LoadOrder = 0
                };

                // Write chunk with path if available
                if (!string.IsNullOrEmpty(path))
                {
                    writer.WritePackageChunk(chunkId, path, data, storeEntry);
                }
                else
                {
                    // Write raw chunk without path
                    writer.WriteRawChunk(chunkId, data);
                }
            }

            writer.Complete();
        }

        // Replace original files with recompressed ones
        File.Delete(utocPath);
        File.Delete(ucasPath);
        File.Move(tempUtocPath, utocPath);
        File.Move(tempUcasPath, ucasPath);

        // Get new sizes
        var utocInfo = new FileInfo(utocPath);
        var ucasInfo = new FileInfo(ucasPath);
        Console.Error.WriteLine($"[IoStoreRecompressor] Done. New sizes: .utoc={utocInfo.Length}, .ucas={ucasInfo.Length}");

        return utocPath;
    }

    /// <summary>
    /// Check if recompression would be beneficial (container is not already compressed)
    /// </summary>
    public static bool ShouldRecompress(string utocPath)
    {
        return !IoStoreReader.IsCompressed(utocPath);
    }

    /// <summary>
    /// Parse AES key from hex string
    /// </summary>
    private static byte[] ParseAesKey(string hex)
    {
        hex = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex;
        byte[] bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }
        return bytes;
    }
}
