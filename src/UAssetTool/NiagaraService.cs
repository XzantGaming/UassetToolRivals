using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.Unversioned;
using UAssetAPI.UnrealTypes;

namespace UAssetTool;

/// <summary>
/// Frontend-friendly service for NiagaraSystem asset operations.
/// All methods return JSON-serializable objects for GUI integration.
/// </summary>
public static class NiagaraService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    #region Data Models

    public class NiagaraFileInfo
    {
        public string Path { get; set; } = "";
        public string FileName { get; set; } = "";
        public string RelativePath { get; set; } = "";
        public long FileSize { get; set; }
        public DateTime LastModified { get; set; }
        public int ColorCurveCount { get; set; }
        public int TotalColorCount { get; set; }
    }

    public class NiagaraListResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public string BaseDirectory { get; set; } = "";
        public int TotalFiles { get; set; }
        public List<NiagaraFileInfo> Files { get; set; } = new();
    }

    public class ColorCurveInfo
    {
        public int ExportIndex { get; set; }
        public string ExportName { get; set; } = "";
        public string ClassName { get; set; } = "";
        public int ColorCount { get; set; }
        public List<ColorValue> SampleColors { get; set; } = new();
    }

    public class ColorValue
    {
        public int Index { get; set; }
        public float R { get; set; }
        public float G { get; set; }
        public float B { get; set; }
        public float A { get; set; }
    }

    public class NiagaraDetailsResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public string Path { get; set; } = "";
        public string FileName { get; set; } = "";
        public int TotalExports { get; set; }
        public int ColorCurveCount { get; set; }
        public int TotalColorCount { get; set; }
        public List<ColorCurveInfo> ColorCurves { get; set; } = new();
    }

    public class ColorEditRequest
    {
        public string AssetPath { get; set; } = "";
        public float R { get; set; }
        public float G { get; set; }
        public float B { get; set; }
        public float A { get; set; } = 1.0f;
        public int? ExportIndex { get; set; }  // Optional: only edit specific export
        public int? ColorIndex { get; set; }   // Optional: only edit specific color
    }

    public class ColorEditResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public string Path { get; set; } = "";
        public int ModifiedCount { get; set; }
    }

    #endregion

    #region Public API

    /// <summary>
    /// List all NiagaraSystem files in a directory with metadata
    /// </summary>
    public static string ListNiagaraFiles(string directory, string? usmapPath = null)
    {
        var result = new NiagaraListResult { BaseDirectory = directory };

        try
        {
            if (!Directory.Exists(directory))
            {
                result.Success = false;
                result.Error = $"Directory not found: {directory}";
                return JsonSerializer.Serialize(result, JsonOptions);
            }

            Usmap? mappings = LoadMappings(usmapPath);

            var nsFiles = Directory.GetFiles(directory, "NS_*.uasset", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(directory, "*.uasset", SearchOption.AllDirectories)
                    .Where(f => Path.GetFileName(f).StartsWith("NS_", StringComparison.OrdinalIgnoreCase)));

            foreach (var filePath in nsFiles.Distinct())
            {
                try
                {
                    var fileInfo = new FileInfo(filePath);
                    var info = new NiagaraFileInfo
                    {
                        Path = filePath,
                        FileName = Path.GetFileName(filePath),
                        RelativePath = Path.GetRelativePath(directory, filePath),
                        FileSize = fileInfo.Length,
                        LastModified = fileInfo.LastWriteTime
                    };

                    // Quick scan for color curve count using structured exports
                    var asset = new UAsset(filePath, EngineVersion.VER_UE5_3, mappings);
                    foreach (var export in asset.Exports)
                    {
                        // Use structured NiagaraDataInterfaceColorCurveExport if available
                        if (export is NiagaraDataInterfaceColorCurveExport colorCurveExport)
                        {
                            info.ColorCurveCount++;
                            info.TotalColorCount += colorCurveExport.ColorCount;
                        }
                        else
                        {
                            // Fallback for unrecognized color curve types
                            string className = export.GetExportClassType()?.Value?.Value ?? "";
                            if (className.Contains("ColorCurve") && export is NormalExport normalExport)
                            {
                                info.ColorCurveCount++;
                                info.TotalColorCount += CountColorsInExport(normalExport);
                            }
                        }
                    }

                    result.Files.Add(info);
                }
                catch
                {
                    // Skip files that can't be parsed
                }
            }

            result.Success = true;
            result.TotalFiles = result.Files.Count;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
        }

        return JsonSerializer.Serialize(result, JsonOptions);
    }

    /// <summary>
    /// Get detailed information about a specific NiagaraSystem file
    /// </summary>
    public static string GetNiagaraDetails(string assetPath, string? usmapPath = null)
    {
        var result = new NiagaraDetailsResult
        {
            Path = assetPath,
            FileName = Path.GetFileName(assetPath)
        };

        try
        {
            if (!File.Exists(assetPath))
            {
                result.Success = false;
                result.Error = $"File not found: {assetPath}";
                return JsonSerializer.Serialize(result, JsonOptions);
            }

            Usmap? mappings = LoadMappings(usmapPath);
            var asset = new UAsset(assetPath, EngineVersion.VER_UE5_3, mappings);

            result.TotalExports = asset.Exports.Count;

            for (int i = 0; i < asset.Exports.Count; i++)
            {
                var export = asset.Exports[i];
                string className = export.GetExportClassType()?.Value?.Value ?? "";

                // Use structured NiagaraDataInterfaceColorCurveExport if available
                if (export is NiagaraDataInterfaceColorCurveExport colorCurveExport)
                {
                    var curveInfo = new ColorCurveInfo
                    {
                        ExportIndex = i,
                        ExportName = export.ObjectName?.Value?.Value ?? $"Export_{i}",
                        ClassName = className
                    };

                    // Extract color data from structured export
                    var colors = ExtractColorsFromStructuredExport(colorCurveExport);
                    curveInfo.ColorCount = colors.Count;

                    // Sample first, middle, and last colors
                    if (colors.Count > 0)
                    {
                        curveInfo.SampleColors.Add(colors[0]);
                        if (colors.Count > 2)
                            curveInfo.SampleColors.Add(colors[colors.Count / 2]);
                        if (colors.Count > 1)
                            curveInfo.SampleColors.Add(colors[colors.Count - 1]);
                    }

                    result.ColorCurves.Add(curveInfo);
                    result.TotalColorCount += curveInfo.ColorCount;
                }
                else if (className.Contains("ColorCurve") && export is NormalExport normalExport)
                {
                    // Fallback for unrecognized color curve types
                    var curveInfo = new ColorCurveInfo
                    {
                        ExportIndex = i,
                        ExportName = export.ObjectName?.Value?.Value ?? $"Export_{i}",
                        ClassName = className
                    };

                    // Extract color data from properties
                    var colors = ExtractColorsFromExport(normalExport);
                    curveInfo.ColorCount = colors.Count;

                    // Sample first, middle, and last colors
                    if (colors.Count > 0)
                    {
                        curveInfo.SampleColors.Add(colors[0]);
                        if (colors.Count > 2)
                            curveInfo.SampleColors.Add(colors[colors.Count / 2]);
                        if (colors.Count > 1)
                            curveInfo.SampleColors.Add(colors[colors.Count - 1]);
                    }

                    result.ColorCurves.Add(curveInfo);
                    result.TotalColorCount += curveInfo.ColorCount;
                }
            }

            result.ColorCurveCount = result.ColorCurves.Count;
            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
        }

        return JsonSerializer.Serialize(result, JsonOptions);
    }

    /// <summary>
    /// Edit colors in a NiagaraSystem file
    /// </summary>
    public static string EditNiagaraColors(string requestJson, string? usmapPath = null)
    {
        ColorEditRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<ColorEditRequest>(requestJson, JsonOptions);
            if (request == null)
            {
                return JsonSerializer.Serialize(new ColorEditResult
                {
                    Success = false,
                    Error = "Invalid request JSON"
                }, JsonOptions);
            }
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new ColorEditResult
            {
                Success = false,
                Error = $"Failed to parse request: {ex.Message}"
            }, JsonOptions);
        }

        return EditNiagaraColors(request, usmapPath);
    }

    /// <summary>
    /// Edit colors in a NiagaraSystem file (typed request)
    /// </summary>
    public static string EditNiagaraColors(ColorEditRequest request, string? usmapPath = null)
    {
        var result = new ColorEditResult { Path = request.AssetPath };

        try
        {
            if (!File.Exists(request.AssetPath))
            {
                result.Success = false;
                result.Error = $"File not found: {request.AssetPath}";
                return JsonSerializer.Serialize(result, JsonOptions);
            }

            Usmap? mappings = LoadMappings(usmapPath);
            var asset = new UAsset(request.AssetPath, EngineVersion.VER_UE5_3, mappings);

            var targetColor = new FLinearColor(request.R, request.G, request.B, request.A);
            int modifiedCount = 0;

            for (int exportIdx = 0; exportIdx < asset.Exports.Count; exportIdx++)
            {
                // Skip if targeting specific export
                if (request.ExportIndex.HasValue && request.ExportIndex.Value != exportIdx)
                    continue;

                var export = asset.Exports[exportIdx];
                string className = export.GetExportClassType()?.Value?.Value ?? "";

                if (className.Contains("ColorCurve") && export is NormalExport normalExport)
                {
                    modifiedCount += ModifyShaderLUT(normalExport.Data, targetColor, request.ColorIndex);
                }
            }

            if (modifiedCount > 0)
            {
                asset.Write(request.AssetPath);
            }

            result.Success = true;
            result.ModifiedCount = modifiedCount;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
        }

        return JsonSerializer.Serialize(result, JsonOptions);
    }

    #endregion

    #region Helper Methods

    private static Usmap? LoadMappings(string? usmapPath)
    {
        if (!string.IsNullOrEmpty(usmapPath) && File.Exists(usmapPath))
        {
            return new Usmap(usmapPath);
        }
        return null;
    }

    private static int CountColorsInExport(NormalExport export)
    {
        if (export.Data == null) return 0;

        foreach (var prop in export.Data)
        {
            if (prop.Name?.Value?.Value == "ShaderLUT" && prop is ArrayPropertyData lutArray)
            {
                return lutArray.Value.Length / 4; // 4 floats per color
            }
        }
        return 0;
    }

    private static List<ColorValue> ExtractColorsFromExport(NormalExport export)
    {
        var colors = new List<ColorValue>();
        if (export.Data == null) return colors;

        foreach (var prop in export.Data)
        {
            if (prop.Name?.Value?.Value != "ShaderLUT") continue;
            if (prop is not ArrayPropertyData lutArray) continue;

            for (int i = 0; i + 3 < lutArray.Value.Length; i += 4)
            {
                if (lutArray.Value[i] is FloatPropertyData rProp &&
                    lutArray.Value[i + 1] is FloatPropertyData gProp &&
                    lutArray.Value[i + 2] is FloatPropertyData bProp &&
                    lutArray.Value[i + 3] is FloatPropertyData aProp)
                {
                    colors.Add(new ColorValue
                    {
                        Index = i / 4,
                        R = rProp.Value,
                        G = gProp.Value,
                        B = bProp.Value,
                        A = aProp.Value
                    });
                }
            }
        }

        return colors;
    }

    private static List<ColorValue> ExtractColorsFromStructuredExport(NiagaraDataInterfaceColorCurveExport export)
    {
        var colors = new List<ColorValue>();
        if (export.ShaderLUT == null) return colors;

        for (int i = 0; i < export.ShaderLUT.Colors.Count; i++)
        {
            var color = export.ShaderLUT.Colors[i];
            colors.Add(new ColorValue
            {
                Index = i,
                R = color.R,
                G = color.G,
                B = color.B,
                A = color.A
            });
        }

        return colors;
    }

    private static int ModifyShaderLUT(List<PropertyData> properties, FLinearColor targetColor, int? specificColorIndex = null)
    {
        int count = 0;

        foreach (var prop in properties)
        {
            if (prop.Name?.Value?.Value != "ShaderLUT") continue;
            if (prop is not ArrayPropertyData lutArray) continue;

            for (int i = 0; i + 3 < lutArray.Value.Length; i += 4)
            {
                int colorIndex = i / 4;

                // Skip if targeting specific color
                if (specificColorIndex.HasValue && specificColorIndex.Value != colorIndex)
                    continue;

                if (lutArray.Value[i] is FloatPropertyData rProp &&
                    lutArray.Value[i + 1] is FloatPropertyData gProp &&
                    lutArray.Value[i + 2] is FloatPropertyData bProp &&
                    lutArray.Value[i + 3] is FloatPropertyData aProp)
                {
                    rProp.Value = targetColor.R;
                    gProp.Value = targetColor.G;
                    bProp.Value = targetColor.B;
                    aProp.Value = targetColor.A;
                    count++;
                }
            }
        }

        return count;
    }

    #endregion
}
