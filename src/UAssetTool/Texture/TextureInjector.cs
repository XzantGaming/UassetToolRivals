using System;
using System.Collections.Generic;
using System.IO;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using BCnEncoder.ImageSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Pfim;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.ExportTypes.Texture;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace UAssetTool.Texture;

/// <summary>
/// Supported compression formats for texture injection.
/// </summary>
public enum TextureCompressionFormat
{
    /// <summary>BC1/DXT1 - 4bpp, no alpha or 1-bit alpha</summary>
    BC1,
    /// <summary>BC3/DXT5 - 8bpp, smooth alpha gradient</summary>
    BC3,
    /// <summary>BC4 - 4bpp, single channel (grayscale)</summary>
    BC4,
    /// <summary>BC5 - 8bpp, two channels (normal maps)</summary>
    BC5,
    /// <summary>BC7 - 8bpp, high quality RGBA</summary>
    BC7,
    /// <summary>Uncompressed BGRA</summary>
    BGRA8
}

/// <summary>
/// Result of a texture injection operation.
/// </summary>
public class TextureInjectionResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int MipCount { get; set; }
    public string? PixelFormat { get; set; }
    public long TotalDataSize { get; set; }
}

/// <summary>
/// Handles texture injection into UAsset files using UAssetAPI's object model.
/// Supports PNG, TGA, DDS input formats and BC1/BC3/BC5/BC7 compression.
/// </summary>
public class TextureInjector
{
    /// <summary>
    /// Inject an image file into a base texture uasset.
    /// </summary>
    /// <param name="baseUassetPath">Path to the base .uasset file to use as template</param>
    /// <param name="imagePath">Path to the image file (PNG, TGA, or DDS)</param>
    /// <param name="outputPath">Path for the output .uasset file</param>
    /// <param name="format">Compression format to use</param>
    /// <param name="generateMips">Whether to generate mipmaps</param>
    /// <param name="usmapPath">Optional path to usmap file for unversioned assets</param>
    /// <returns>Result of the injection operation</returns>
    public static TextureInjectionResult Inject(
        string baseUassetPath,
        string imagePath,
        string outputPath,
        TextureCompressionFormat format = TextureCompressionFormat.BC7,
        bool generateMips = true,
        string? usmapPath = null)
    {
        var result = new TextureInjectionResult();
        
        try
        {
            // Validate inputs
            if (!File.Exists(baseUassetPath))
            {
                result.ErrorMessage = $"Base uasset not found: {baseUassetPath}";
                return result;
            }
            
            string baseUexpPath = Path.ChangeExtension(baseUassetPath, ".uexp");
            if (!File.Exists(baseUexpPath))
            {
                result.ErrorMessage = $"Base uexp not found: {baseUexpPath}";
                return result;
            }
            
            // Load the image
            var imageData = LoadImage(imagePath);
            if (imageData == null)
            {
                result.ErrorMessage = $"Failed to load image: {imagePath}";
                return result;
            }
            
            // Read original files as raw bytes
            byte[] origUasset = File.ReadAllBytes(baseUassetPath);
            byte[] origUexp = File.ReadAllBytes(baseUexpPath);
            
            // Find the FString pixel format in the .uexp (e.g. "PF_DXT1", "PF_DXT5", "PF_BC7")
            // FString format: int32 length (including null terminator), then chars, then null
            int pixelFormatFStringPos = FindPixelFormatFString(origUexp);
            if (pixelFormatFStringPos < 0)
            {
                result.ErrorMessage = "Could not find pixel format FString in .uexp";
                return result;
            }
            
            // Read the original pixel format FString
            int origFStringLen = BitConverter.ToInt32(origUexp, pixelFormatFStringPos);
            string origPixelFormat = System.Text.Encoding.ASCII.GetString(origUexp, pixelFormatFStringPos + 4, origFStringLen - 1);
            
            // Find the FTexturePlatformData start: it's PlaceholderBytes(16-20) + SizeX + SizeY + PackedData before the FString
            // Work backwards from pixelFormatFStringPos to find SizeX/SizeY
            // PackedData is 4 bytes before FString, SizeY 4 before that, SizeX 4 before that
            int packedDataPos = pixelFormatFStringPos - 4;
            int sizeYPos = packedDataPos - 4;
            int sizeXPos = sizeYPos - 4;
            
            int origSizeX = BitConverter.ToInt32(origUexp, sizeXPos);
            int origSizeY = BitConverter.ToInt32(origUexp, sizeYPos);
            uint origPackedData = BitConverter.ToUInt32(origUexp, packedDataPos);
            
            // Auto-detect original compression format if user didn't explicitly specify
            TextureCompressionFormat actualFormat = format;
            var detectedFormat = DetectFormatFromUEName(origPixelFormat);
            if (detectedFormat.HasValue)
            {
                // Always use the original format to avoid FName mismatch
                // The PixelFormat FName in the header region references a name map entry;
                // changing the format would require updating the FName which is complex
                actualFormat = detectedFormat.Value;
                if (actualFormat != format)
                {
                    Console.WriteLine($"  NOTE: Using original format {origPixelFormat} instead of {GetUEPixelFormatName(format)} to preserve FName compatibility");
                }
            }
            
            Console.WriteLine($"  Original texture: {origSizeX}x{origSizeY}, format={origPixelFormat}");
            Console.WriteLine($"  New texture: {imageData.Width}x{imageData.Height}, format={GetUEPixelFormatName(actualFormat)}");
            
            // Compress to target format (single mip only) - AFTER detecting format
            var compressedMips = CompressMipmaps(new List<Image<Rgba32>> { imageData.Clone() }, actualFormat);
            byte[] compressedData = compressedMips[0].Data;
            
            // Find the SkipOffset by trying common placeholder sizes (16, 20) and validating
            // Structure: ...SkipOffset(8) + PlaceholderBytes(16/20) + SizeX(4) + SizeY(4) + ...
            // So SkipOffset is at sizeXPos - placeholderSize - 8
            int skipOffsetPos = -1;
            int placeholderSize = 0;
            long origSkipOffset = 0;
            
            foreach (int trySize in new[] { 16, 20, 0 })
            {
                int trySkipPos = sizeXPos - trySize - 8;
                if (trySkipPos < 0) continue;
                
                long trySkipVal = BitConverter.ToInt64(origUexp, trySkipPos);
                // SkipOffset should be positive and reasonable (< 100MB)
                if (trySkipVal > 0 && trySkipVal < 100_000_000)
                {
                    // Verify: skipOffsetPos + 8 + skipVal should point past the platform data
                    // and before the PACKAGE_FILE_TAG at end
                    long endPos = trySkipPos + 8 + trySkipVal;
                    if (endPos > sizeXPos && endPos <= origUexp.Length)
                    {
                        skipOffsetPos = trySkipPos;
                        placeholderSize = trySize;
                        origSkipOffset = trySkipVal;
                        break;
                    }
                }
            }
            
            if (skipOffsetPos < 0)
            {
                result.ErrorMessage = "Could not locate SkipOffset in .uexp";
                return result;
            }
            
            int placeholderStart = skipOffsetPos + 8;
            
            Console.WriteLine($"  SkipOffset at 0x{skipOffsetPos:X}: value={origSkipOffset}");
            Console.WriteLine($"  PlaceholderStart at 0x{placeholderStart:X}, size={placeholderSize}");
            
            // Find the first mip's DataResource index after the FString
            // After FString comes: [OptData(8)?] + FirstMipToSerialize(4) + MipCount(4) + Mip[0].DataResourceIndex(4) + ...
            int afterFString = pixelFormatFStringPos + 4 + origFStringLen;
            
            // Check for OptData (HasOptData flag in PackedData)
            bool hasOptData = (origPackedData & (1u << 30)) != 0;
            if (hasOptData)
            {
                afterFString += 8; // ExtData(4) + NumMipsInTail(4)
            }
            
            int firstMipToSerialize = BitConverter.ToInt32(origUexp, afterFString);
            int mipCount = BitConverter.ToInt32(origUexp, afterFString + 4);
            
            Console.WriteLine($"  Original mipCount={mipCount}, firstMipToSerialize={firstMipToSerialize}");
            
            // Each mip in UE5.3+ DataResources format is just a DataResourceIndex (int32)
            int mipHeadersStart = afterFString + 8; // after FirstMipToSerialize + MipCount
            
            // Read first mip's DataResourceIndex
            int firstMipDataResIdx = BitConverter.ToInt32(origUexp, mipHeadersStart);
            Console.WriteLine($"  First mip DataResourceIndex={firstMipDataResIdx}");
            
            // Build new platform data bytes - use the ORIGINAL pixel format string
            // to keep the FString consistent with the FName in the preserved header
            string newPixelFormatStr = origPixelFormat;
            byte[] newPlatformData;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                // Placeholder bytes (same size as original)
                bw.Write(new byte[placeholderSize]);
                
                // SizeX, SizeY
                bw.Write(imageData.Width);
                bw.Write(imageData.Height);
                
                // PackedData (keep original)
                bw.Write(origPackedData);
                
                // PixelFormat FString
                byte[] pfBytes = System.Text.Encoding.ASCII.GetBytes(newPixelFormatStr);
                bw.Write(pfBytes.Length + 1); // length including null terminator
                bw.Write(pfBytes);
                bw.Write((byte)0); // null terminator
                
                // OptData if present
                if (hasOptData)
                {
                    // Copy original OptData
                    bw.Write(origUexp, pixelFormatFStringPos + 4 + origFStringLen, 8);
                }
                
                // FirstMipToSerialize = 0 (single mip)
                bw.Write(0);
                
                // MipCount = 1
                bw.Write(1);
                
                // Single mip: DataResourceIndex (keep the first mip's original index)
                bw.Write(firstMipDataResIdx);
                
                // Pixel data (inline)
                bw.Write(compressedData);
                
                // Mip dimensions (after pixel data for DataResources format)
                bw.Write(imageData.Width);
                bw.Write(imageData.Height);
                bw.Write(1); // SizeZ
                
                // bIsVirtual = 0
                bw.Write(0);
                
                newPlatformData = ms.ToArray();
            }
            
            // Now build the full new .uexp
            // Copy from 0 to skipOffsetPos (everything before skip offset stays)
            // Write new skip offset (size of newPlatformData)
            // Write newPlatformData
            // Write None FName terminator (copy from original - it's the 8 bytes after original platform data ends)
            // Write PACKAGE_FILE_TAG (C1 83 2A 9E)
            
            byte[] newUexp;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                // Copy everything before skip offset (properties, LightingGuid, strip flags, etc.)
                bw.Write(origUexp, 0, skipOffsetPos);
                
                // Write new skip offset (relative: bytes from after this field to end of platform data)
                bw.Write((long)newPlatformData.Length);
                
                // Write new platform data
                bw.Write(newPlatformData);
                
                // Write None FName terminator
                // Find the None FName in original: it's after the original platform data
                // The original skip offset tells us where: skipOffsetPos + 8 + origSkipOffset
                long origPlatformDataEnd = skipOffsetPos + 8 + origSkipOffset;
                if (origPlatformDataEnd >= 0 && origPlatformDataEnd + 8 <= origUexp.Length)
                {
                    // Copy the None FName (8 bytes)
                    bw.Write(origUexp, (int)origPlatformDataEnd, 8);
                }
                else
                {
                    // Write a generic None FName (index 0, number 0 - "None" is usually at index 0)
                    bw.Write(0); // FName index
                    bw.Write(0); // FName number
                }
                
                // Write PACKAGE_FILE_TAG
                bw.Write((uint)0x9E2A83C1);
                
                newUexp = ms.ToArray();
            }
            
            // The newUexp we built is: [export data] + [None FName 8 bytes] + [PACKAGE_FILE_TAG 4 bytes]
            // UAssetAPI's Write() appends PACKAGE_FILE_TAG itself, so we need just the export data
            // (everything before the None FName terminator and tag)
            // Export data = newUexp minus last 12 bytes (8 None FName + 4 PACKAGE_FILE_TAG)
            byte[] exportData = new byte[newUexp.Length - 4]; // minus PACKAGE_FILE_TAG only
            Array.Copy(newUexp, 0, exportData, 0, exportData.Length);
            
            // Update the .uasset using UAssetAPI with RawExport approach
            // This ensures SerialSize is computed correctly from actual data
            var (finalUasset, finalUexp) = PatchUassetWithUAssetAPI(
                baseUassetPath, exportData, firstMipDataResIdx, compressedData.Length, usmapPath);
            
            // Write output files
            string outputDir = Path.GetDirectoryName(outputPath) ?? ".";
            if (!Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);
            
            File.WriteAllBytes(outputPath, finalUasset);
            File.WriteAllBytes(Path.ChangeExtension(outputPath, ".uexp"), finalUexp);
            
            // Populate result
            result.Success = true;
            result.Width = imageData.Width;
            result.Height = imageData.Height;
            result.MipCount = 1;
            result.PixelFormat = GetUEPixelFormatName(actualFormat);
            result.TotalDataSize = compressedData.Length;
            
            imageData.Dispose();
            return result;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"Injection failed: {ex.Message}\n{ex.StackTrace}";
            return result;
        }
    }
    
    /// <summary>
    /// Find the FString pixel format in the .uexp binary.
    /// Searches for known pixel format strings like "PF_DXT1", "PF_DXT5", "PF_BC7", etc.
    /// </summary>
    private static int FindPixelFormatFString(byte[] uexp)
    {
        // Search for FString patterns: int32 length + "PF_" prefix
        byte[] pfPrefix = System.Text.Encoding.ASCII.GetBytes("PF_");
        
        for (int i = 4; i < uexp.Length - 10; i++)
        {
            // Check if bytes at i match "PF_"
            if (uexp[i] == pfPrefix[0] && uexp[i + 1] == pfPrefix[1] && uexp[i + 2] == pfPrefix[2])
            {
                // Check if i-4 has a reasonable FString length (4-20)
                int strLen = BitConverter.ToInt32(uexp, i - 4);
                if (strLen >= 4 && strLen <= 20)
                {
                    // Verify null terminator at expected position
                    if (i - 4 + 4 + strLen - 1 < uexp.Length && uexp[i - 4 + 4 + strLen - 1] == 0)
                    {
                        return i - 4; // Return position of the FString length field
                    }
                }
            }
        }
        
        return -1;
    }
    
    /// <summary>
    /// Patch .uasset using UAssetAPI to update DataResources and SerialSize.
    /// We replace the export with a RawExport containing our .uexp bytes so that
    /// UAssetAPI computes correct SerialSize/SerialOffset during Write().
    /// Returns (newUassetBytes, newUexpBytes) - the .uexp from UAssetAPI's Write since
    /// it includes the correct PACKAGE_FILE_TAG placement.
    /// </summary>
    private static (byte[] uasset, byte[] uexp) PatchUassetWithUAssetAPI(
        string origUassetPath, byte[] newExportData, int dataResourceIndex, int newPixelDataSize, string? usmapPath)
    {
        // Load with usmap so UAssetAPI can parse the asset structure
        Usmap? mappings = null;
        if (!string.IsNullOrEmpty(usmapPath) && File.Exists(usmapPath))
        {
            mappings = new Usmap(usmapPath);
        }
        var asset = new UAsset(origUassetPath, EngineVersion.VER_UE5_3, mappings);
        
        // Update DataResources
        if (asset.DataResources != null && dataResourceIndex >= 0 && dataResourceIndex < asset.DataResources.Count)
        {
            var dr = asset.DataResources[dataResourceIndex];
            dr.SerialSize = newPixelDataSize;
            dr.RawSize = newPixelDataSize;
            dr.Flags = EObjectDataResourceFlags.Inline;
            dr.LegacyBulkDataFlags = (uint)EBulkDataFlags.BULKDATA_ForceInlinePayload;
            asset.DataResources[dataResourceIndex] = dr;
            Console.WriteLine($"  Patched DataResource[{dataResourceIndex}]: SerialSize={newPixelDataSize}, Flags=Inline");
        }
        
        // Replace the export with a RawExport containing our exact bytes
        // UAssetAPI's Write() will serialize these bytes and compute the correct SerialSize
        if (asset.Exports.Count > 0)
        {
            var origExport = asset.Exports[0];
            var rawExport = origExport.ConvertToChildExport<RawExport>();
            ((RawExport)rawExport).Data = newExportData;
            asset.Exports[0] = rawExport;
            Console.WriteLine($"  Replaced Export[0] with RawExport ({newExportData.Length} bytes)");
        }
        
        // Write to temp files and read back
        string tempPath = Path.GetTempFileName();
        string tempUexp = Path.ChangeExtension(tempPath, ".uexp");
        try
        {
            asset.Write(tempPath);
            byte[] uassetBytes = File.ReadAllBytes(tempPath);
            byte[] uexpBytes = File.ReadAllBytes(tempUexp);
            return (uassetBytes, uexpBytes);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            if (File.Exists(tempUexp)) File.Delete(tempUexp);
        }
    }
    
    /// <summary>
    /// Load an image from file (supports PNG, TGA, DDS, BMP, JPEG).
    /// </summary>
    private static Image<Rgba32>? LoadImage(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        
        if (ext == ".dds" || ext == ".tga")
        {
            // Use Pfim for DDS and TGA
            return LoadWithPfim(path);
        }
        else
        {
            // Use ImageSharp for PNG, BMP, JPEG, etc.
            return Image.Load<Rgba32>(path);
        }
    }
    
    /// <summary>
    /// Load DDS or TGA using Pfim library.
    /// </summary>
    private static Image<Rgba32>? LoadWithPfim(string path)
    {
        using var image = Pfimage.FromFile(path);
        
        // Decompress if needed (for DXT compressed textures)
        if (image.Compressed)
        {
            image.Decompress();
        }
        
        // Convert Pfim image to ImageSharp
        byte[] data = image.Data;
        int width = image.Width;
        int height = image.Height;
        int stride = image.Stride;
        int bytesPerPixel = image.BitsPerPixel / 8;
        
        var result = new Image<Rgba32>(width, height);
        
        switch (image.Format)
        {
            case Pfim.ImageFormat.Rgba32:
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int i = y * stride + x * 4;
                        // BGRA format
                        result[x, y] = new Rgba32(data[i + 2], data[i + 1], data[i], data[i + 3]);
                    }
                }
                break;
                
            case Pfim.ImageFormat.Rgb24:
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int i = y * stride + x * 3;
                        // BGR format
                        result[x, y] = new Rgba32(data[i + 2], data[i + 1], data[i], 255);
                    }
                }
                break;
                
            default:
                // Generic handling based on bytes per pixel
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int i = y * stride + x * bytesPerPixel;
                        
                        if (bytesPerPixel >= 4)
                        {
                            result[x, y] = new Rgba32(data[i + 2], data[i + 1], data[i], data[i + 3]);
                        }
                        else if (bytesPerPixel == 3)
                        {
                            result[x, y] = new Rgba32(data[i + 2], data[i + 1], data[i], 255);
                        }
                        else if (bytesPerPixel == 1)
                        {
                            result[x, y] = new Rgba32(data[i], data[i], data[i], 255);
                        }
                    }
                }
                break;
        }
        
        return result;
    }
    
    /// <summary>
    /// Generate mipmap chain from source image.
    /// </summary>
    private static List<Image<Rgba32>> GenerateMipmaps(Image<Rgba32> source)
    {
        var mips = new List<Image<Rgba32>>();
        
        int width = source.Width;
        int height = source.Height;
        
        // Add the original as mip 0
        mips.Add(source.Clone());
        
        // Generate smaller mips until we reach 1x1 or 4x4 (minimum for BC compression)
        while (width > 4 && height > 4)
        {
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
            
            var mip = source.Clone();
            mip.Mutate(x => x.Resize(width, height));
            mips.Add(mip);
        }
        
        return mips;
    }
    
    /// <summary>
    /// Compress mipmaps to the target BC format.
    /// </summary>
    private static List<CompressedMip> CompressMipmaps(List<Image<Rgba32>> mips, TextureCompressionFormat format)
    {
        var result = new List<CompressedMip>();
        
        if (format == TextureCompressionFormat.BGRA8)
        {
            // Uncompressed - just extract raw pixels
            foreach (var mip in mips)
            {
                byte[] data = new byte[mip.Width * mip.Height * 4];
                for (int y = 0; y < mip.Height; y++)
                {
                    for (int x = 0; x < mip.Width; x++)
                    {
                        var pixel = mip[x, y];
                        int i = (y * mip.Width + x) * 4;
                        data[i] = pixel.B;
                        data[i + 1] = pixel.G;
                        data[i + 2] = pixel.R;
                        data[i + 3] = pixel.A;
                    }
                }
                result.Add(new CompressedMip(mip.Width, mip.Height, data));
            }
        }
        else
        {
            // Use BCnEncoder for BC compression
            var encoder = new BcEncoder();
            encoder.OutputOptions.GenerateMipMaps = false; // We already have mips
            encoder.OutputOptions.Quality = CompressionQuality.BestQuality;
            encoder.OutputOptions.Format = GetBCnFormat(format);
            
            foreach (var mip in mips)
            {
                // EncodeToRawBytes returns byte[][] (one array per mip), we want just the first
                byte[][] compressedMips = encoder.EncodeToRawBytes(mip);
                byte[] compressed = compressedMips.Length > 0 ? compressedMips[0] : Array.Empty<byte>();
                result.Add(new CompressedMip(mip.Width, mip.Height, compressed));
            }
        }
        
        return result;
    }
    
    /// <summary>
    /// Update the FTexturePlatformData with new mipmap data.
    /// </summary>
    private static void UpdatePlatformData(
        FTexturePlatformData platformData,
        List<CompressedMip> mips,
        TextureCompressionFormat format,
        int width,
        int height)
    {
        // Update dimensions
        platformData.SizeX = width;
        platformData.SizeY = height;
        
        // Update pixel format
        platformData.PixelFormat = GetUEPixelFormatName(format);
        
        // Clear existing mips
        platformData.Mips.Clear();
        platformData.FirstMipToSerialize = 0;
        
        // Add new mips
        foreach (var mip in mips)
        {
            var mipMap = new FTexture2DMipMap();
            mipMap.SizeX = mip.Width;
            mipMap.SizeY = mip.Height;
            mipMap.SizeZ = 1;
            
            // Create bulk data with inline storage
            mipMap.BulkData = new FByteBulkData(mip.Data);
            mipMap.BulkData.ConvertToInline();
            
            platformData.Mips.Add(mipMap);
        }
    }
    
    /// <summary>
    /// Get BCnEncoder format from our enum.
    /// </summary>
    private static CompressionFormat GetBCnFormat(TextureCompressionFormat format)
    {
        return format switch
        {
            TextureCompressionFormat.BC1 => CompressionFormat.Bc1,
            TextureCompressionFormat.BC3 => CompressionFormat.Bc3,
            TextureCompressionFormat.BC4 => CompressionFormat.Bc4,
            TextureCompressionFormat.BC5 => CompressionFormat.Bc5,
            TextureCompressionFormat.BC7 => CompressionFormat.Bc7,
            _ => CompressionFormat.Bc7
        };
    }
    
    /// <summary>
    /// Get UE pixel format name string.
    /// </summary>
    private static string GetUEPixelFormatName(TextureCompressionFormat format)
    {
        return format switch
        {
            TextureCompressionFormat.BC1 => "PF_DXT1",
            TextureCompressionFormat.BC3 => "PF_DXT5",
            TextureCompressionFormat.BC4 => "PF_BC4",
            TextureCompressionFormat.BC5 => "PF_BC5",
            TextureCompressionFormat.BC7 => "PF_BC7",
            TextureCompressionFormat.BGRA8 => "PF_B8G8R8A8",
            _ => "PF_BC7"
        };
    }
    
    /// <summary>
    /// Detect compression format from UE pixel format name string.
    /// </summary>
    private static TextureCompressionFormat? DetectFormatFromUEName(string ueFormatName)
    {
        return ueFormatName switch
        {
            "PF_DXT1" => TextureCompressionFormat.BC1,
            "PF_DXT5" => TextureCompressionFormat.BC3,
            "PF_BC4" => TextureCompressionFormat.BC4,
            "PF_BC5" => TextureCompressionFormat.BC5,
            "PF_BC7" => TextureCompressionFormat.BC7,
            "PF_B8G8R8A8" => TextureCompressionFormat.BGRA8,
            _ => null
        };
    }
    
    /// <summary>
    /// Parse compression format from string.
    /// </summary>
    public static TextureCompressionFormat ParseFormat(string formatStr)
    {
        return formatStr.ToUpperInvariant() switch
        {
            "BC1" or "DXT1" => TextureCompressionFormat.BC1,
            "BC3" or "DXT5" => TextureCompressionFormat.BC3,
            "BC4" => TextureCompressionFormat.BC4,
            "BC5" => TextureCompressionFormat.BC5,
            "BC7" => TextureCompressionFormat.BC7,
            "BGRA8" or "BGRA" or "UNCOMPRESSED" => TextureCompressionFormat.BGRA8,
            _ => TextureCompressionFormat.BC7
        };
    }
}

/// <summary>
/// Represents a compressed mipmap level.
/// </summary>
public class CompressedMip
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Data { get; }
    
    public CompressedMip(int width, int height, byte[] data)
    {
        Width = width;
        Height = height;
        Data = data;
    }
}
