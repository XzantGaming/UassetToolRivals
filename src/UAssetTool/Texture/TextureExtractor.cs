using System;
using System.Collections.Generic;
using System.IO;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using BCnEncoder.ImageSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Bmp;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.ExportTypes.Texture;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace UAssetTool.Texture;

/// <summary>
/// Supported output formats for texture extraction.
/// </summary>
public enum TextureOutputFormat
{
    PNG,
    TGA,
    DDS,
    BMP
}

/// <summary>
/// Result of a texture extraction operation.
/// </summary>
public class TextureExtractionResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int MipCount { get; set; }
    public string? PixelFormat { get; set; }
    public string? OutputPath { get; set; }
}

/// <summary>
/// Extracts textures from Texture2D .uasset files to common image formats.
/// Uses UAssetAPI to parse the asset and BCnEncoder to decode block-compressed data.
/// </summary>
public class TextureExtractor
{
    /// <summary>
    /// Extract a texture from a .uasset file to an image file.
    /// </summary>
    /// <param name="uassetPath">Path to the .uasset file</param>
    /// <param name="outputPath">Path for the output image file</param>
    /// <param name="outputFormat">Output image format</param>
    /// <param name="mipIndex">Which mip to extract (0 = largest)</param>
    /// <param name="usmapPath">Optional path to usmap file for unversioned assets</param>
    /// <returns>Result of the extraction</returns>
    public static TextureExtractionResult Extract(
        string uassetPath,
        string outputPath,
        TextureOutputFormat outputFormat = TextureOutputFormat.PNG,
        int mipIndex = 0,
        string? usmapPath = null)
    {
        var result = new TextureExtractionResult();
        
        try
        {
            // Validate input
            if (!File.Exists(uassetPath))
            {
                result.ErrorMessage = $"File not found: {uassetPath}";
                return result;
            }
            
            // Load usmap if provided
            Usmap? mappings = null;
            if (!string.IsNullOrEmpty(usmapPath) && File.Exists(usmapPath))
            {
                mappings = new Usmap(usmapPath);
            }
            
            // Load the asset
            var asset = new UAsset(uassetPath, EngineVersion.VER_UE5_3, mappings);
            
            // Find the TextureExport
            TextureExport? texExport = null;
            foreach (var export in asset.Exports)
            {
                if (export is TextureExport tex)
                {
                    texExport = tex;
                    break;
                }
            }
            
            if (texExport == null)
            {
                result.ErrorMessage = "No Texture2D export found in asset. Make sure a usmap is provided for unversioned assets.";
                return result;
            }
            
            if (texExport.PlatformData == null)
            {
                result.ErrorMessage = "TextureExport has no PlatformData";
                return result;
            }
            
            var platformData = texExport.PlatformData;
            string pixelFormat = platformData.PixelFormat;
            int sizeX = platformData.SizeX;
            int sizeY = platformData.SizeY;
            
            Console.WriteLine($"  Texture: {sizeX}x{sizeY}, format={pixelFormat}");
            Console.WriteLine($"  Mip count: {platformData.Mips.Count}");
            
            if (platformData.Mips.Count == 0)
            {
                result.ErrorMessage = "Texture has no mipmaps";
                return result;
            }
            
            if (mipIndex >= platformData.Mips.Count)
            {
                result.ErrorMessage = $"Mip index {mipIndex} out of range (texture has {platformData.Mips.Count} mips)";
                return result;
            }
            
            // Get the mip data
            var mip = platformData.Mips[mipIndex];
            byte[] pixelData = mip.GetData();
            int mipWidth = mip.SizeX > 0 ? mip.SizeX : sizeX >> mipIndex;
            int mipHeight = mip.SizeY > 0 ? mip.SizeY : sizeY >> mipIndex;
            
            // For DataResources format (UE5.3+), pixel data may not have been read
            // by UAssetAPI because it lives in external .ubulk or inline in .uexp
            if (pixelData == null || pixelData.Length == 0)
            {
                pixelData = ReadMipDataFromFiles(asset, mip, uassetPath);
            }
            
            // Validate data size - if wrong for this mip's dimensions, treat as missing
            int expectedMipSize = CalculateDataSize(mipWidth, mipHeight, pixelFormat);
            if (pixelData != null && pixelData.Length > 0 && pixelData.Length < expectedMipSize)
            {
                Console.WriteLine($"  Mip[{mipIndex}] data ({pixelData.Length}) too small for {mipWidth}x{mipHeight} {pixelFormat} (expected {expectedMipSize})");
                pixelData = null;
            }
            
            if (pixelData == null || pixelData.Length == 0)
            {
                // Try to find the first mip that has correctly-sized data
                int fallbackMip = -1;
                byte[]? fallbackData = null;
                for (int m = 0; m < platformData.Mips.Count; m++)
                {
                    var tryMip = platformData.Mips[m];
                    int tryW = tryMip.SizeX > 0 ? tryMip.SizeX : sizeX >> m;
                    int tryH = tryMip.SizeY > 0 ? tryMip.SizeY : sizeY >> m;
                    int tryExpected = CalculateDataSize(tryW, tryH, pixelFormat);
                    
                    // Try GetData first, then ReadMipDataFromFiles
                    byte[] tryData = tryMip.GetData();
                    if (tryData == null || tryData.Length < tryExpected)
                        tryData = ReadMipDataFromFiles(asset, tryMip, uassetPath);
                    if (tryData == null || tryData.Length < tryExpected)
                        continue;
                    
                    fallbackMip = m;
                    fallbackData = tryData;
                    break;
                }
                
                if (fallbackMip >= 0 && fallbackData != null)
                {
                    if (fallbackMip != mipIndex)
                        Console.WriteLine($"  Mip[{mipIndex}] has no local data. Falling back to mip[{fallbackMip}].");
                    mip = platformData.Mips[fallbackMip];
                    pixelData = fallbackData;
                    mipWidth = mip.SizeX > 0 ? mip.SizeX : sizeX >> fallbackMip;
                    mipHeight = mip.SizeY > 0 ? mip.SizeY : sizeY >> fallbackMip;
                    mipIndex = fallbackMip;
                }
                else
                {
                    result.ErrorMessage = "No pixel data found in any mip. The largest mips may be stored in IoStore BulkData chunks not available locally.";
                    return result;
                }
            }
            
            // Validate data size matches expected dimensions
            int expectedSize = CalculateDataSize(mipWidth, mipHeight, pixelFormat);
            if (pixelData.Length != expectedSize && pixelData.Length > expectedSize)
            {
                // Data may contain multiple mips concatenated - truncate to expected size
                Console.WriteLine($"  Note: data ({pixelData.Length}) > expected ({expectedSize}), trimming to mip size");
                byte[] trimmed = new byte[expectedSize];
                Array.Copy(pixelData, 0, trimmed, 0, expectedSize);
                pixelData = trimmed;
            }
            
            Console.WriteLine($"  Mip[{mipIndex}]: {mipWidth}x{mipHeight}, {pixelData.Length} bytes");
            
            // Decode the pixel data
            Image<Rgba32> image;
            
            var compressionFormat = GetBCnCompressionFormat(pixelFormat);
            if (compressionFormat.HasValue)
            {
                // Block-compressed format - decode with BCnEncoder
                var decoder = new BcDecoder();
                image = decoder.DecodeRawToImageRgba32(pixelData, mipWidth, mipHeight, compressionFormat.Value);
                Console.WriteLine($"  Decoded {pixelFormat} -> RGBA32");
            }
            else if (pixelFormat == "PF_B8G8R8A8")
            {
                // Uncompressed BGRA8 - convert to RGBA
                image = new Image<Rgba32>(mipWidth, mipHeight);
                int pixelCount = mipWidth * mipHeight;
                for (int i = 0; i < pixelCount && i * 4 + 3 < pixelData.Length; i++)
                {
                    byte b = pixelData[i * 4 + 0];
                    byte g = pixelData[i * 4 + 1];
                    byte r = pixelData[i * 4 + 2];
                    byte a = pixelData[i * 4 + 3];
                    image[i % mipWidth, i / mipWidth] = new Rgba32(r, g, b, a);
                }
                Console.WriteLine($"  Converted BGRA8 -> RGBA32");
            }
            else if (pixelFormat == "PF_R8G8B8A8")
            {
                // Uncompressed RGBA8
                image = new Image<Rgba32>(mipWidth, mipHeight);
                int pixelCount = mipWidth * mipHeight;
                for (int i = 0; i < pixelCount && i * 4 + 3 < pixelData.Length; i++)
                {
                    byte r = pixelData[i * 4 + 0];
                    byte g = pixelData[i * 4 + 1];
                    byte b = pixelData[i * 4 + 2];
                    byte a = pixelData[i * 4 + 3];
                    image[i % mipWidth, i / mipWidth] = new Rgba32(r, g, b, a);
                }
                Console.WriteLine($"  Converted RGBA8 -> RGBA32");
            }
            else if (pixelFormat == "PF_G8")
            {
                // Single channel grayscale
                image = new Image<Rgba32>(mipWidth, mipHeight);
                int pixelCount = mipWidth * mipHeight;
                for (int i = 0; i < pixelCount && i < pixelData.Length; i++)
                {
                    byte v = pixelData[i];
                    image[i % mipWidth, i / mipWidth] = new Rgba32(v, v, v, 255);
                }
                Console.WriteLine($"  Converted G8 -> RGBA32");
            }
            else
            {
                result.ErrorMessage = $"Unsupported pixel format: {pixelFormat}";
                return result;
            }
            
            // Ensure output directory exists
            string outputDir = Path.GetDirectoryName(outputPath) ?? ".";
            if (!Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);
            
            // Save in requested format
            switch (outputFormat)
            {
                case TextureOutputFormat.PNG:
                    if (!outputPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                        outputPath = Path.ChangeExtension(outputPath, ".png");
                    image.SaveAsPng(outputPath);
                    break;
                    
                case TextureOutputFormat.BMP:
                    if (!outputPath.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
                        outputPath = Path.ChangeExtension(outputPath, ".bmp");
                    image.SaveAsBmp(outputPath);
                    break;
                    
                case TextureOutputFormat.TGA:
                    if (!outputPath.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
                        outputPath = Path.ChangeExtension(outputPath, ".tga");
                    image.SaveAsTga(outputPath);
                    break;
                    
                case TextureOutputFormat.DDS:
                    if (!outputPath.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                        outputPath = Path.ChangeExtension(outputPath, ".dds");
                    // Write raw DDS with the original compressed data (no re-encoding)
                    WriteDdsFile(outputPath, mipWidth, mipHeight, pixelFormat, pixelData);
                    break;
            }
            
            Console.WriteLine($"  Saved: {outputPath}");
            
            result.Success = true;
            result.Width = mipWidth;
            result.Height = mipHeight;
            result.MipCount = platformData.Mips.Count;
            result.PixelFormat = pixelFormat;
            result.OutputPath = outputPath;
            
            image.Dispose();
            return result;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"Extraction failed: {ex.Message}\n{ex.StackTrace}";
            return result;
        }
    }
    
    /// <summary>
    /// Read mip pixel data from .ubulk or .uexp files using DataResource metadata.
    /// </summary>
    private static byte[] ReadMipDataFromFiles(UAsset asset, FTexture2DMipMap mip, string uassetPath)
    {
        var header = mip.BulkData?.Header;
        if (header == null) return Array.Empty<byte>();
        
        // Try DataResources approach (UE5.3+)
        if (header.DataResourceIndex >= 0 && asset.DataResources != null && header.DataResourceIndex < asset.DataResources.Count)
        {
            var dr = asset.DataResources[header.DataResourceIndex];
            int size = (int)dr.RawSize;
            if (size <= 0) return Array.Empty<byte>();
            
            // Check if data is inline (in .uexp) or external (.ubulk)
            bool isInline = (dr.Flags & EObjectDataResourceFlags.Inline) != 0;
            
            if (isInline)
            {
                // Read from .uexp at the DataResource's SerialOffset
                string uexpPath = Path.ChangeExtension(uassetPath, ".uexp");
                if (File.Exists(uexpPath))
                {
                    byte[] uexpBytes = File.ReadAllBytes(uexpPath);
                    long offset = dr.SerialOffset;
                    if (offset >= 0 && offset + size <= uexpBytes.Length)
                    {
                        byte[] data = new byte[size];
                        Array.Copy(uexpBytes, offset, data, 0, size);
                        Console.WriteLine($"  Read {size} bytes from .uexp DataResource[{header.DataResourceIndex}] at offset {offset}");
                        return data;
                    }
                }
            }
            else
            {
                // Try .ubulk, .uptnl (optional/high-res mips), and .m.ubulk
                string[] bulkExtensions = { ".ubulk", ".uptnl", ".m.ubulk" };
                foreach (string ext in bulkExtensions)
                {
                    string bulkPath = ext == ".m.ubulk" 
                        ? Path.ChangeExtension(uassetPath, ".m.ubulk")
                        : Path.ChangeExtension(uassetPath, ext);
                    if (!File.Exists(bulkPath)) continue;
                    
                    byte[] bulkBytes = File.ReadAllBytes(bulkPath);
                    long offset = dr.SerialOffset;
                    if (offset >= 0 && offset + size <= bulkBytes.Length)
                    {
                        byte[] data = new byte[size];
                        Array.Copy(bulkBytes, offset, data, 0, size);
                        Console.WriteLine($"  Read {size} bytes from {ext} DataResource[{header.DataResourceIndex}] at offset {offset}");
                        return data;
                    }
                    // If offset is out of range, file might contain just this mip's data
                    if (offset == 0 && bulkBytes.Length == size)
                    {
                        Console.WriteLine($"  Read {size} bytes from {ext} (entire file)");
                        return bulkBytes;
                    }
                }
                
                // No external bulk file found — fall back to .uexp
                // (zen-extracted assets may not set Inline flag even though data is in .uexp)
                string uexpFallback = Path.ChangeExtension(uassetPath, ".uexp");
                if (File.Exists(uexpFallback))
                {
                    byte[] uexpBytes = File.ReadAllBytes(uexpFallback);
                    long offset = dr.SerialOffset;
                    if (offset >= 0 && offset + size <= uexpBytes.Length)
                    {
                        byte[] data = new byte[size];
                        Array.Copy(uexpBytes, offset, data, 0, size);
                        Console.WriteLine($"  Read {size} bytes from .uexp fallback DataResource[{header.DataResourceIndex}] at offset {offset}");
                        return data;
                    }
                }
            }
        }
        
        // Fallback: try legacy bulk data header approach
        if (header.ElementCount > 0)
        {
            if (header.IsInSeparateFile)
            {
                string ubulkPath = Path.ChangeExtension(uassetPath, ".ubulk");
                if (File.Exists(ubulkPath))
                {
                    byte[] ubulkBytes = File.ReadAllBytes(ubulkPath);
                    long offset = header.OffsetInFile;
                    int size = (int)header.ElementCount;
                    if (offset >= 0 && offset + size <= ubulkBytes.Length)
                    {
                        byte[] data = new byte[size];
                        Array.Copy(ubulkBytes, offset, data, 0, size);
                        Console.WriteLine($"  Read {size} bytes from .ubulk at legacy offset {offset}");
                        return data;
                    }
                }
            }
        }
        
        return Array.Empty<byte>();
    }
    
    /// <summary>
    /// Map UE pixel format names to BCnEncoder CompressionFormat.
    /// </summary>
    private static CompressionFormat? GetBCnCompressionFormat(string pixelFormat)
    {
        return pixelFormat switch
        {
            "PF_DXT1" => CompressionFormat.Bc1,
            "PF_DXT3" => CompressionFormat.Bc2,
            "PF_DXT5" => CompressionFormat.Bc3,
            "PF_BC4" => CompressionFormat.Bc4,
            "PF_BC5" => CompressionFormat.Bc5,
            "PF_BC7" => CompressionFormat.Bc7,
            "PF_BC6H" => CompressionFormat.Bc6U,
            _ => null
        };
    }
    
    /// <summary>
    /// Write a DDS file with the raw compressed pixel data (no re-encoding needed).
    /// </summary>
    private static void WriteDdsFile(string path, int width, int height, string pixelFormat, byte[] data)
    {
        using var fs = new FileStream(path, FileMode.Create);
        using var bw = new BinaryWriter(fs);
        
        // DDS Magic
        bw.Write(0x20534444); // "DDS "
        
        // DDS_HEADER (124 bytes)
        bw.Write(124); // dwSize
        
        uint flags = 0x1 | 0x2 | 0x4 | 0x1000; // CAPS | HEIGHT | WIDTH | PIXELFORMAT
        if (IsBCFormat(pixelFormat))
            flags |= 0x80000; // LINEARSIZE
        else
            flags |= 0x8; // PITCH
        bw.Write(flags); // dwFlags
        
        bw.Write(height); // dwHeight
        bw.Write(width); // dwWidth
        
        // dwPitchOrLinearSize
        int linearSize = CalculateDataSize(width, height, pixelFormat);
        bw.Write(linearSize);
        
        bw.Write(0); // dwDepth
        bw.Write(1); // dwMipMapCount
        
        // dwReserved1[11]
        for (int i = 0; i < 11; i++) bw.Write(0);
        
        // DDS_PIXELFORMAT (32 bytes)
        bw.Write(32); // dwSize
        
        if (pixelFormat == "PF_BC7" || pixelFormat == "PF_BC6H" || pixelFormat == "PF_BC5" || pixelFormat == "PF_BC4")
        {
            // Use DX10 extended header for BC4/5/6/7
            bw.Write(0x4); // dwFlags = FOURCC
            bw.Write(0x30315844); // dwFourCC = "DX10"
            bw.Write(0); // dwRGBBitCount
            bw.Write((uint)0); // dwRBitMask
            bw.Write((uint)0); // dwGBitMask
            bw.Write((uint)0); // dwBBitMask
            bw.Write((uint)0); // dwABitMask
        }
        else if (pixelFormat == "PF_DXT1")
        {
            bw.Write(0x4); // dwFlags = FOURCC
            bw.Write(0x31545844); // dwFourCC = "DXT1"
            bw.Write(0); bw.Write((uint)0); bw.Write((uint)0); bw.Write((uint)0); bw.Write((uint)0);
        }
        else if (pixelFormat == "PF_DXT3")
        {
            bw.Write(0x4); // dwFlags = FOURCC
            bw.Write(0x33545844); // dwFourCC = "DXT3"
            bw.Write(0); bw.Write((uint)0); bw.Write((uint)0); bw.Write((uint)0); bw.Write((uint)0);
        }
        else if (pixelFormat == "PF_DXT5")
        {
            bw.Write(0x4); // dwFlags = FOURCC
            bw.Write(0x35545844); // dwFourCC = "DXT5"
            bw.Write(0); bw.Write((uint)0); bw.Write((uint)0); bw.Write((uint)0); bw.Write((uint)0);
        }
        else if (pixelFormat == "PF_B8G8R8A8" || pixelFormat == "PF_R8G8B8A8")
        {
            bw.Write(0x41); // dwFlags = RGBA
            bw.Write(0); // dwFourCC
            bw.Write(32); // dwRGBBitCount
            if (pixelFormat == "PF_B8G8R8A8")
            {
                bw.Write((uint)0x00FF0000); // R
                bw.Write((uint)0x0000FF00); // G
                bw.Write((uint)0x000000FF); // B
                bw.Write((uint)0xFF000000); // A
            }
            else
            {
                bw.Write((uint)0x000000FF); // R
                bw.Write((uint)0x0000FF00); // G
                bw.Write((uint)0x00FF0000); // B
                bw.Write((uint)0xFF000000); // A
            }
        }
        else
        {
            // Generic fallback
            bw.Write(0x4); // FOURCC
            bw.Write(0); // unknown
            bw.Write(0); bw.Write((uint)0); bw.Write((uint)0); bw.Write((uint)0); bw.Write((uint)0);
        }
        
        // dwCaps
        bw.Write(0x1000); // TEXTURE
        bw.Write(0); // dwCaps2
        bw.Write(0); // dwCaps3
        bw.Write(0); // dwCaps4
        bw.Write(0); // dwReserved2
        
        // DX10 extended header for BC4/5/6/7
        if (pixelFormat == "PF_BC7" || pixelFormat == "PF_BC6H" || pixelFormat == "PF_BC5" || pixelFormat == "PF_BC4")
        {
            uint dxgiFormat = pixelFormat switch
            {
                "PF_BC4" => 80,   // DXGI_FORMAT_BC4_UNORM
                "PF_BC5" => 83,   // DXGI_FORMAT_BC5_UNORM
                "PF_BC6H" => 95,  // DXGI_FORMAT_BC6H_UF16
                "PF_BC7" => 98,   // DXGI_FORMAT_BC7_UNORM
                _ => 0
            };
            bw.Write(dxgiFormat); // dxgiFormat
            bw.Write(3); // resourceDimension = D3D10_RESOURCE_DIMENSION_TEXTURE2D
            bw.Write((uint)0); // miscFlag
            bw.Write(1); // arraySize
            bw.Write((uint)0); // miscFlags2
        }
        
        // Write the raw pixel data
        bw.Write(data);
    }
    
    private static bool IsBCFormat(string pixelFormat)
    {
        return pixelFormat switch
        {
            "PF_DXT1" or "PF_DXT3" or "PF_DXT5" or
            "PF_BC4" or "PF_BC5" or "PF_BC6H" or "PF_BC7" => true,
            _ => false
        };
    }
    
    private static int CalculateDataSize(int width, int height, string pixelFormat)
    {
        int blockSize = pixelFormat switch
        {
            "PF_DXT1" => 8,
            "PF_BC4" => 8,
            "PF_DXT3" => 16,
            "PF_DXT5" => 16,
            "PF_BC5" => 16,
            "PF_BC6H" => 16,
            "PF_BC7" => 16,
            _ => 0
        };
        
        if (blockSize > 0)
        {
            int blocksX = Math.Max(1, (width + 3) / 4);
            int blocksY = Math.Max(1, (height + 3) / 4);
            return blocksX * blocksY * blockSize;
        }
        
        // Uncompressed
        return pixelFormat switch
        {
            "PF_B8G8R8A8" or "PF_R8G8B8A8" => width * height * 4,
            "PF_G8" => width * height,
            _ => width * height * 4
        };
    }
    
    /// <summary>
    /// Parse output format from string.
    /// </summary>
    public static TextureOutputFormat ParseOutputFormat(string formatStr)
    {
        return formatStr.ToUpperInvariant() switch
        {
            "PNG" => TextureOutputFormat.PNG,
            "TGA" => TextureOutputFormat.TGA,
            "DDS" => TextureOutputFormat.DDS,
            "BMP" => TextureOutputFormat.BMP,
            _ => TextureOutputFormat.PNG
        };
    }
}
