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
    /// <returns>Result of the injection operation</returns>
    public static TextureInjectionResult Inject(
        string baseUassetPath,
        string imagePath,
        string outputPath,
        TextureCompressionFormat format = TextureCompressionFormat.BC7,
        bool generateMips = true)
    {
        var result = new TextureInjectionResult();
        
        try
        {
            // Load the base uasset
            if (!File.Exists(baseUassetPath))
            {
                result.ErrorMessage = $"Base uasset not found: {baseUassetPath}";
                return result;
            }
            
            var asset = new UAsset(baseUassetPath, EngineVersion.VER_UE5_3);
            
            // Find the TextureExport
            TextureExport? textureExport = null;
            int exportIndex = -1;
            for (int i = 0; i < asset.Exports.Count; i++)
            {
                if (asset.Exports[i] is TextureExport tex)
                {
                    textureExport = tex;
                    exportIndex = i;
                    break;
                }
            }
            
            if (textureExport == null)
            {
                result.ErrorMessage = "No TextureExport found in base uasset";
                return result;
            }
            
            if (textureExport.PlatformData == null)
            {
                result.ErrorMessage = "TextureExport has no PlatformData";
                return result;
            }
            
            // Load the image
            var imageData = LoadImage(imagePath);
            if (imageData == null)
            {
                result.ErrorMessage = $"Failed to load image: {imagePath}";
                return result;
            }
            
            // Generate mipmaps
            var mipImages = generateMips 
                ? GenerateMipmaps(imageData) 
                : new List<Image<Rgba32>> { imageData };
            
            // Compress to the target format
            var compressedMips = CompressMipmaps(mipImages, format);
            
            // Update the TextureExport's PlatformData
            UpdatePlatformData(textureExport.PlatformData, compressedMips, format, imageData.Width, imageData.Height);
            
            // Update pixel format FName
            textureExport.PixelFormatFName = UAssetAPI.UnrealTypes.FName.FromString(asset, GetUEPixelFormatName(format));
            
            // Save the modified asset
            string outputDir = Path.GetDirectoryName(outputPath) ?? ".";
            if (!Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);
            
            asset.Write(outputPath);
            
            // Populate result
            result.Success = true;
            result.Width = imageData.Width;
            result.Height = imageData.Height;
            result.MipCount = compressedMips.Count;
            result.PixelFormat = GetUEPixelFormatName(format);
            result.TotalDataSize = compressedMips.Sum(m => m.Data.Length);
            
            // Dispose images
            foreach (var img in mipImages)
                img.Dispose();
            imageData.Dispose();
            
            return result;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"Injection failed: {ex.Message}";
            return result;
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
