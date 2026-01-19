using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;

namespace UAssetTool.IoStore;

/// <summary>
/// Oodle compression compressor types.
/// Reference: oodle_loader/src/lib.rs
/// </summary>
public enum OodleCompressor : int
{
    None = 3,
    Kraken = 8,
    Leviathan = 13,
    Mermaid = 9,
    Selkie = 11,
    Hydra = 12,
}

/// <summary>
/// Oodle compression levels.
/// Reference: oodle_loader/src/lib.rs
/// </summary>
public enum OodleCompressionLevel : int
{
    None = 0,
    SuperFast = 1,
    VeryFast = 2,
    Fast = 3,
    Normal = 4,
    Optimal1 = 5,
    Optimal2 = 6,
    Optimal3 = 7,
    Optimal4 = 8,
    Optimal5 = 9,
    HyperFast1 = -1,
    HyperFast2 = -2,
    HyperFast3 = -3,
    HyperFast4 = -4,
}

/// <summary>
/// Oodle compression wrapper using P/Invoke.
/// Reference: oodle_loader/src/lib.rs
/// </summary>
public static class OodleCompression
{
    private const string OODLE_DLL_NAME = "oo2core_9_win64.dll";
    private const string OODLE_DOWNLOAD_URL = "https://github.com/new-world-tools/go-oodle/releases/download/v0.2.3-files/oo2core_9_win64.dll";
    private const long OODLE_MIN_VALID_SIZE = 500_000; // 500KB minimum

    private static bool _initialized = false;
    private static bool _available = false;

    [DllImport(OODLE_DLL_NAME, CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr OodleLZ_Compress(
        OodleCompressor compressor,
        IntPtr rawBuf,
        IntPtr rawLen,
        IntPtr compBuf,
        OodleCompressionLevel level,
        IntPtr pOptions,
        IntPtr dictionaryBase,
        IntPtr lrm,
        IntPtr scratchMem,
        IntPtr scratchSize);

    [DllImport(OODLE_DLL_NAME, CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr OodleLZ_Decompress(
        IntPtr compBuf,
        IntPtr compBufSize,
        IntPtr rawBuf,
        IntPtr rawLen,
        int fuzzSafe,
        int checkCRC,
        int verbosity,
        IntPtr decBufBase,
        IntPtr decBufSize,
        IntPtr fpCallback,
        IntPtr callbackUserData,
        IntPtr decoderMemory,
        IntPtr decoderMemorySize,
        int threadPhase);

    [DllImport(OODLE_DLL_NAME, CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr OodleLZ_GetCompressedBufferSizeNeeded(
        OodleCompressor compressor,
        IntPtr rawSize);

    /// <summary>
    /// Check if Oodle is available.
    /// </summary>
    public static bool IsAvailable
    {
        get
        {
            if (!_initialized)
                Initialize();
            return _available;
        }
    }

    /// <summary>
    /// Initialize Oodle - download DLL if needed.
    /// </summary>
    public static void Initialize()
    {
        if (_initialized)
            return;

        _initialized = true;

        try
        {
            string exePath = AppContext.BaseDirectory;
            string dllPath = Path.Combine(exePath, OODLE_DLL_NAME);

            // Check if DLL exists and is valid
            if (!IsOodleValid(dllPath))
            {
                // Try to download
                Console.Error.WriteLine($"[Oodle] Downloading Oodle library...");
                DownloadOodle(dllPath);
            }

            // Try to load and test
            if (File.Exists(dllPath))
            {
                // Test by calling GetCompressedBufferSizeNeeded
                var size = OodleLZ_GetCompressedBufferSizeNeeded(OodleCompressor.Kraken, (IntPtr)1024);
                if (size.ToInt64() > 0)
                {
                    _available = true;
                    Console.Error.WriteLine($"[Oodle] Initialized successfully");
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Oodle] Failed to initialize: {ex.Message}");
            _available = false;
        }
    }

    private static bool IsOodleValid(string path)
    {
        if (!File.Exists(path))
            return false;

        var info = new FileInfo(path);
        if (info.Length < OODLE_MIN_VALID_SIZE)
        {
            Console.Error.WriteLine($"[Oodle] DLL at {path} is too small ({info.Length} bytes), likely corrupted");
            return false;
        }

        return true;
    }

    private static void DownloadOodle(string targetPath)
    {
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(2);
            
            var response = client.GetAsync(OODLE_DOWNLOAD_URL).Result;
            response.EnsureSuccessStatusCode();
            
            var bytes = response.Content.ReadAsByteArrayAsync().Result;
            
            if (bytes.Length < OODLE_MIN_VALID_SIZE)
                throw new Exception($"Downloaded file is too small ({bytes.Length} bytes)");

            File.WriteAllBytes(targetPath, bytes);
            Console.Error.WriteLine($"[Oodle] Downloaded successfully ({bytes.Length} bytes)");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Oodle] Download failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Compress data using Oodle.
    /// </summary>
    public static byte[]? Compress(
        byte[] input,
        OodleCompressor compressor = OodleCompressor.Kraken,
        OodleCompressionLevel level = OodleCompressionLevel.Normal)
    {
        if (!IsAvailable)
            return null;

        try
        {
            // Get required buffer size
            var bufferSize = OodleLZ_GetCompressedBufferSizeNeeded(compressor, (IntPtr)input.Length);
            if (bufferSize.ToInt64() <= 0)
                return null;

            byte[] output = new byte[bufferSize.ToInt64()];

            // Pin arrays for P/Invoke
            var inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
            var outputHandle = GCHandle.Alloc(output, GCHandleType.Pinned);

            try
            {
                var compressedSize = OodleLZ_Compress(
                    compressor,
                    inputHandle.AddrOfPinnedObject(),
                    (IntPtr)input.Length,
                    outputHandle.AddrOfPinnedObject(),
                    level,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero);

                if (compressedSize.ToInt64() <= 0)
                    return null;

                // Truncate to actual size
                Array.Resize(ref output, (int)compressedSize.ToInt64());
                return output;
            }
            finally
            {
                inputHandle.Free();
                outputHandle.Free();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Oodle] Compression failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Decompress data using Oodle.
    /// </summary>
    public static byte[]? Decompress(byte[] input, int uncompressedSize)
    {
        if (!IsAvailable)
            return null;

        try
        {
            byte[] output = new byte[uncompressedSize];

            var inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
            var outputHandle = GCHandle.Alloc(output, GCHandleType.Pinned);

            try
            {
                var decompressedSize = OodleLZ_Decompress(
                    inputHandle.AddrOfPinnedObject(),
                    (IntPtr)input.Length,
                    outputHandle.AddrOfPinnedObject(),
                    (IntPtr)uncompressedSize,
                    1, // fuzzSafe
                    1, // checkCRC
                    0, // verbosity
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    3); // threadPhase

                if (decompressedSize.ToInt64() != uncompressedSize)
                    return null;

                return output;
            }
            finally
            {
                inputHandle.Free();
                outputHandle.Free();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Oodle] Decompression failed: {ex.Message}");
            return null;
        }
    }
}
