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
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    // Custom converter for full float precision output
    private class FullPrecisionFloatConverter : JsonConverter<float>
    {
        public override float Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.GetSingle();

        public override void Write(Utf8JsonWriter writer, float value, JsonSerializerOptions options)
            => writer.WriteRawValue(value.ToString("G9", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static readonly JsonSerializerOptions JsonOptionsFullPrecision = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new FullPrecisionFloatConverter() }
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
        public double R { get; set; }
        public double G { get; set; }
        public double B { get; set; }
        public double A { get; set; }
    }

    /// <summary>
    /// Info for NiagaraDataInterfaceCurve (float curves)
    /// </summary>
    public class FloatCurveInfo
    {
        public int ExportIndex { get; set; }
        public string ExportName { get; set; } = "";
        public string ClassName { get; set; } = "";
        public int ValueCount { get; set; }
        public List<FloatValue> SampleValues { get; set; } = new();
    }

    public class FloatValue
    {
        public int Index { get; set; }
        public double Value { get; set; }
    }

    /// <summary>
    /// Info for NiagaraDataInterfaceVector2DCurve
    /// </summary>
    public class Vector2DCurveInfo
    {
        public int ExportIndex { get; set; }
        public string ExportName { get; set; } = "";
        public string ClassName { get; set; } = "";
        public int ValueCount { get; set; }
        public List<Vector2DValue> SampleValues { get; set; } = new();
    }

    public class Vector2DValue
    {
        public int Index { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
    }

    /// <summary>
    /// Info for NiagaraDataInterfaceVectorCurve (Vector3/RGB curves)
    /// </summary>
    public class Vector3CurveInfo
    {
        public int ExportIndex { get; set; }
        public string ExportName { get; set; } = "";
        public string ClassName { get; set; } = "";
        public int ValueCount { get; set; }
        public List<Vector3Value> SampleValues { get; set; } = new();
    }

    public class Vector3Value
    {
        public int Index { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
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
        public int FloatCurveCount { get; set; }
        public int TotalFloatCount { get; set; }
        public List<FloatCurveInfo> FloatCurves { get; set; } = new();
        public int Vector2DCurveCount { get; set; }
        public int TotalVector2DCount { get; set; }
        public List<Vector2DCurveInfo> Vector2DCurves { get; set; } = new();
        public int Vector3CurveCount { get; set; }
        public int TotalVector3Count { get; set; }
        public List<Vector3CurveInfo> Vector3Curves { get; set; } = new();
        // Array-based data interfaces
        public int ArrayColorCount { get; set; }
        public int TotalArrayColorValues { get; set; }
        public List<ArrayColorInfo> ArrayColors { get; set; } = new();
        public int ArrayFloatCount { get; set; }
        public int TotalArrayFloatValues { get; set; }
        public List<ArrayFloatInfo> ArrayFloats { get; set; } = new();
        public int ArrayFloat3Count { get; set; }
        public int TotalArrayFloat3Values { get; set; }
        public List<ArrayFloat3Info> ArrayFloat3s { get; set; } = new();
    }

    /// <summary>
    /// Info for NiagaraDataInterfaceArrayColor
    /// </summary>
    public class ArrayColorInfo
    {
        public int ExportIndex { get; set; }
        public string ExportName { get; set; } = "";
        public string ClassName { get; set; } = "";
        public int ColorCount { get; set; }
        public List<ColorValue> SampleColors { get; set; } = new();
    }

    /// <summary>
    /// Info for NiagaraDataInterfaceArrayFloat
    /// </summary>
    public class ArrayFloatInfo
    {
        public int ExportIndex { get; set; }
        public string ExportName { get; set; } = "";
        public string ClassName { get; set; } = "";
        public int ValueCount { get; set; }
        public List<FloatValue> SampleValues { get; set; } = new();
    }

    /// <summary>
    /// Info for NiagaraDataInterfaceArrayFloat3 (RGB arrays)
    /// </summary>
    public class ArrayFloat3Info
    {
        public int ExportIndex { get; set; }
        public string ExportName { get; set; } = "";
        public string ClassName { get; set; } = "";
        public int ValueCount { get; set; }
        public List<Vector3Value> SampleValues { get; set; } = new();
    }

    public class ColorEditRequest
    {
        public string AssetPath { get; set; } = "";
        public float R { get; set; }
        public float G { get; set; }
        public float B { get; set; }
        public float A { get; set; } = 1.0f;
        public int? ExportIndex { get; set; }  // Optional: only edit specific export by index
        public string? ExportNameFilter { get; set; }  // Optional: only edit exports matching this pattern (case-insensitive)
        public int? ColorIndex { get; set; }   // Optional: only edit specific color by index
        public int? ColorIndexStart { get; set; }  // Optional: start of color index range (inclusive)
        public int? ColorIndexEnd { get; set; }    // Optional: end of color index range (inclusive)
        public bool? ModifyR { get; set; }  // Optional: if false, don't modify R channel (default: true)
        public bool? ModifyG { get; set; }  // Optional: if false, don't modify G channel (default: true)
        public bool? ModifyB { get; set; }  // Optional: if false, don't modify B channel (default: true)
        public bool? ModifyA { get; set; }  // Optional: if false, don't modify A channel (default: true)
    }

    public class ColorEditResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public string Path { get; set; } = "";
        public int ModifiedCount { get; set; }
    }

    /// <summary>
    /// Request to edit float curve values (NiagaraDataInterfaceCurve)
    /// </summary>
    public class FloatCurveEditRequest
    {
        public string AssetPath { get; set; } = "";
        public float Value { get; set; }
        public int? ExportIndex { get; set; }
        public string? ExportNameFilter { get; set; }
        public int? ValueIndex { get; set; }
        public int? ValueIndexStart { get; set; }
        public int? ValueIndexEnd { get; set; }
    }

    /// <summary>
    /// Request to edit Vector2D curve values (NiagaraDataInterfaceVector2DCurve)
    /// </summary>
    public class Vector2DCurveEditRequest
    {
        public string AssetPath { get; set; } = "";
        public float X { get; set; }
        public float Y { get; set; }
        public int? ExportIndex { get; set; }
        public string? ExportNameFilter { get; set; }
        public int? ValueIndex { get; set; }
        public int? ValueIndexStart { get; set; }
        public int? ValueIndexEnd { get; set; }
        public bool? ModifyX { get; set; }  // Default: true
        public bool? ModifyY { get; set; }  // Default: true
    }

    /// <summary>
    /// Request to edit Vector3 curve values (NiagaraDataInterfaceVectorCurve - XYZ/RGB)
    /// </summary>
    public class Vector3CurveEditRequest
    {
        public string AssetPath { get; set; } = "";
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public int? ExportIndex { get; set; }
        public string? ExportNameFilter { get; set; }
        public int? ValueIndex { get; set; }
        public int? ValueIndexStart { get; set; }
        public int? ValueIndexEnd { get; set; }
        public bool? ModifyX { get; set; }  // Default: true
        public bool? ModifyY { get; set; }  // Default: true
        public bool? ModifyZ { get; set; }  // Default: true
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
    /// <param name="assetPath">Path to the .uasset file</param>
    /// <param name="usmapPath">Optional path to .usmap mappings file</param>
    /// <param name="fullData">If true, returns all values instead of just samples (first, middle, last)</param>
    public static string GetNiagaraDetails(string assetPath, string? usmapPath = null, bool fullData = false)
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
                string exportName = export.ObjectName?.Value?.Value ?? $"Export_{i}";

                // NiagaraDataInterfaceColorCurve - RGBA colors
                if (export is NiagaraDataInterfaceColorCurveExport colorCurveExport)
                {
                    var curveInfo = new ColorCurveInfo
                    {
                        ExportIndex = i,
                        ExportName = exportName,
                        ClassName = className
                    };

                    var colors = ExtractColorsFromStructuredExport(colorCurveExport);
                    curveInfo.ColorCount = colors.Count;

                    if (fullData)
                    {
                        // Return all colors
                        curveInfo.SampleColors.AddRange(colors);
                    }
                    else
                    {
                        // Sample first, middle, and last colors
                        if (colors.Count > 0)
                        {
                            curveInfo.SampleColors.Add(colors[0]);
                            if (colors.Count > 2)
                                curveInfo.SampleColors.Add(colors[colors.Count / 2]);
                            if (colors.Count > 1)
                                curveInfo.SampleColors.Add(colors[colors.Count - 1]);
                        }
                    }

                    result.ColorCurves.Add(curveInfo);
                    result.TotalColorCount += curveInfo.ColorCount;
                }
                // NiagaraDataInterfaceCurve - single float values
                else if (export is NiagaraDataInterfaceCurveExport floatCurveExport)
                {
                    var curveInfo = new FloatCurveInfo
                    {
                        ExportIndex = i,
                        ExportName = exportName,
                        ClassName = className,
                        ValueCount = floatCurveExport.ValueCount
                    };

                    if (fullData)
                    {
                        // Return all values
                        for (int j = 0; j < floatCurveExport.ValueCount; j++)
                        {
                            curveInfo.SampleValues.Add(new FloatValue { Index = j, Value = floatCurveExport.GetValue(j) ?? 0 });
                        }
                    }
                    else
                    {
                        // Sample first, middle, and last values
                        if (floatCurveExport.ValueCount > 0)
                        {
                            curveInfo.SampleValues.Add(new FloatValue { Index = 0, Value = floatCurveExport.GetValue(0) ?? 0 });
                            if (floatCurveExport.ValueCount > 2)
                            {
                                int mid = floatCurveExport.ValueCount / 2;
                                curveInfo.SampleValues.Add(new FloatValue { Index = mid, Value = floatCurveExport.GetValue(mid) ?? 0 });
                            }
                            if (floatCurveExport.ValueCount > 1)
                            {
                                int last = floatCurveExport.ValueCount - 1;
                                curveInfo.SampleValues.Add(new FloatValue { Index = last, Value = floatCurveExport.GetValue(last) ?? 0 });
                            }
                        }
                    }

                    result.FloatCurves.Add(curveInfo);
                    result.TotalFloatCount += curveInfo.ValueCount;
                }
                // NiagaraDataInterfaceVector2DCurve - Vector2D values
                else if (export is NiagaraDataInterfaceVector2DCurveExport vec2CurveExport)
                {
                    var curveInfo = new Vector2DCurveInfo
                    {
                        ExportIndex = i,
                        ExportName = exportName,
                        ClassName = className,
                        ValueCount = vec2CurveExport.ValueCount
                    };

                    if (fullData)
                    {
                        // Return all values
                        for (int j = 0; j < vec2CurveExport.ValueCount; j++)
                        {
                            var v = vec2CurveExport.GetValue(j);
                            if (v.HasValue)
                                curveInfo.SampleValues.Add(new Vector2DValue { Index = j, X = v.Value.X, Y = v.Value.Y });
                        }
                    }
                    else
                    {
                        // Sample first, middle, and last values
                        if (vec2CurveExport.ValueCount > 0)
                        {
                            var v0 = vec2CurveExport.GetValue(0);
                            if (v0.HasValue)
                                curveInfo.SampleValues.Add(new Vector2DValue { Index = 0, X = v0.Value.X, Y = v0.Value.Y });
                            
                            if (vec2CurveExport.ValueCount > 2)
                            {
                                int mid = vec2CurveExport.ValueCount / 2;
                                var vm = vec2CurveExport.GetValue(mid);
                                if (vm.HasValue)
                                    curveInfo.SampleValues.Add(new Vector2DValue { Index = mid, X = vm.Value.X, Y = vm.Value.Y });
                            }
                            if (vec2CurveExport.ValueCount > 1)
                            {
                                int last = vec2CurveExport.ValueCount - 1;
                                var vl = vec2CurveExport.GetValue(last);
                                if (vl.HasValue)
                                    curveInfo.SampleValues.Add(new Vector2DValue { Index = last, X = vl.Value.X, Y = vl.Value.Y });
                            }
                        }
                    }

                    result.Vector2DCurves.Add(curveInfo);
                    result.TotalVector2DCount += curveInfo.ValueCount;
                }
                // Fallback for unrecognized color curve types
                else if (className.Contains("ColorCurve") && export is NormalExport normalExport)
                {
                    var curveInfo = new ColorCurveInfo
                    {
                        ExportIndex = i,
                        ExportName = exportName,
                        ClassName = className
                    };

                    var colors = ExtractColorsFromExport(normalExport);
                    curveInfo.ColorCount = colors.Count;

                    if (fullData)
                    {
                        curveInfo.SampleColors.AddRange(colors);
                    }
                    else
                    {
                        if (colors.Count > 0)
                        {
                            curveInfo.SampleColors.Add(colors[0]);
                            if (colors.Count > 2)
                                curveInfo.SampleColors.Add(colors[colors.Count / 2]);
                            if (colors.Count > 1)
                                curveInfo.SampleColors.Add(colors[colors.Count - 1]);
                        }
                    }

                    result.ColorCurves.Add(curveInfo);
                    result.TotalColorCount += curveInfo.ColorCount;
                }
                // Fallback for unrecognized float curve types
                else if (className.Contains("DataInterfaceCurve") && !className.Contains("Color") && !className.Contains("Vector") && export is NormalExport floatNormalExport)
                {
                    var curveInfo = new FloatCurveInfo
                    {
                        ExportIndex = i,
                        ExportName = exportName,
                        ClassName = className
                    };

                    var values = ExtractFloatsFromExport(floatNormalExport);
                    curveInfo.ValueCount = values.Count;

                    if (fullData)
                    {
                        curveInfo.SampleValues.AddRange(values);
                    }
                    else
                    {
                        if (values.Count > 0)
                        {
                            curveInfo.SampleValues.Add(values[0]);
                            if (values.Count > 2)
                                curveInfo.SampleValues.Add(values[values.Count / 2]);
                            if (values.Count > 1)
                                curveInfo.SampleValues.Add(values[values.Count - 1]);
                        }
                    }

                    result.FloatCurves.Add(curveInfo);
                    result.TotalFloatCount += curveInfo.ValueCount;
                }
                // Fallback for unrecognized Vector2D curve types
                else if (className.Contains("Vector2DCurve") && export is NormalExport vec2NormalExport)
                {
                    var curveInfo = new Vector2DCurveInfo
                    {
                        ExportIndex = i,
                        ExportName = exportName,
                        ClassName = className
                    };

                    var values = ExtractVector2DsFromExport(vec2NormalExport);
                    curveInfo.ValueCount = values.Count;

                    if (fullData)
                    {
                        curveInfo.SampleValues.AddRange(values);
                    }
                    else
                    {
                        if (values.Count > 0)
                        {
                            curveInfo.SampleValues.Add(values[0]);
                            if (values.Count > 2)
                                curveInfo.SampleValues.Add(values[values.Count / 2]);
                            if (values.Count > 1)
                                curveInfo.SampleValues.Add(values[values.Count - 1]);
                        }
                    }

                    result.Vector2DCurves.Add(curveInfo);
                    result.TotalVector2DCount += curveInfo.ValueCount;
                }
                // NiagaraDataInterfaceVectorCurve - Vector3 values (can be RGB colors!)
                else if (export is NiagaraDataInterfaceVectorCurveExport vec3CurveExport)
                {
                    var curveInfo = new Vector3CurveInfo
                    {
                        ExportIndex = i,
                        ExportName = exportName,
                        ClassName = className,
                        ValueCount = vec3CurveExport.ValueCount
                    };

                    if (fullData)
                    {
                        for (int j = 0; j < vec3CurveExport.ValueCount; j++)
                        {
                            var v = vec3CurveExport.GetValue(j);
                            if (v.HasValue)
                                curveInfo.SampleValues.Add(new Vector3Value { Index = j, X = v.Value.X, Y = v.Value.Y, Z = v.Value.Z });
                        }
                    }
                    else
                    {
                        if (vec3CurveExport.ValueCount > 0)
                        {
                            var v0 = vec3CurveExport.GetValue(0);
                            if (v0.HasValue)
                                curveInfo.SampleValues.Add(new Vector3Value { Index = 0, X = v0.Value.X, Y = v0.Value.Y, Z = v0.Value.Z });
                            
                            if (vec3CurveExport.ValueCount > 2)
                            {
                                int mid = vec3CurveExport.ValueCount / 2;
                                var vm = vec3CurveExport.GetValue(mid);
                                if (vm.HasValue)
                                    curveInfo.SampleValues.Add(new Vector3Value { Index = mid, X = vm.Value.X, Y = vm.Value.Y, Z = vm.Value.Z });
                            }
                            if (vec3CurveExport.ValueCount > 1)
                            {
                                int last = vec3CurveExport.ValueCount - 1;
                                var vl = vec3CurveExport.GetValue(last);
                                if (vl.HasValue)
                                    curveInfo.SampleValues.Add(new Vector3Value { Index = last, X = vl.Value.X, Y = vl.Value.Y, Z = vl.Value.Z });
                            }
                        }
                    }

                    result.Vector3Curves.Add(curveInfo);
                    result.TotalVector3Count += curveInfo.ValueCount;
                }
                // Fallback for unrecognized VectorCurve types (not Vector2D)
                else if (className.Contains("VectorCurve") && !className.Contains("2D") && export is NormalExport vec3NormalExport)
                {
                    var curveInfo = new Vector3CurveInfo
                    {
                        ExportIndex = i,
                        ExportName = exportName,
                        ClassName = className
                    };

                    var values = ExtractVector3sFromExport(vec3NormalExport);
                    curveInfo.ValueCount = values.Count;

                    if (fullData)
                    {
                        curveInfo.SampleValues.AddRange(values);
                    }
                    else
                    {
                        if (values.Count > 0)
                        {
                            curveInfo.SampleValues.Add(values[0]);
                            if (values.Count > 2)
                                curveInfo.SampleValues.Add(values[values.Count / 2]);
                            if (values.Count > 1)
                                curveInfo.SampleValues.Add(values[values.Count - 1]);
                        }
                    }

                    result.Vector3Curves.Add(curveInfo);
                    result.TotalVector3Count += curveInfo.ValueCount;
                }
                // NiagaraDataInterfaceArrayColor - direct color arrays
                else if (export is NiagaraDataInterfaceArrayColorExport arrayColorExport)
                {
                    var info = new ArrayColorInfo
                    {
                        ExportIndex = i,
                        ExportName = exportName,
                        ClassName = className,
                        ColorCount = arrayColorExport.ColorCount
                    };

                    if (fullData)
                    {
                        for (int j = 0; j < arrayColorExport.ColorCount; j++)
                        {
                            var c = arrayColorExport.GetColor(j);
                            if (c.HasValue)
                                info.SampleColors.Add(new ColorValue { Index = j, R = c.Value.R, G = c.Value.G, B = c.Value.B, A = c.Value.A });
                        }
                    }
                    else
                    {
                        if (arrayColorExport.ColorCount > 0)
                        {
                            var c0 = arrayColorExport.GetColor(0);
                            if (c0.HasValue)
                                info.SampleColors.Add(new ColorValue { Index = 0, R = c0.Value.R, G = c0.Value.G, B = c0.Value.B, A = c0.Value.A });
                            
                            if (arrayColorExport.ColorCount > 2)
                            {
                                int mid = arrayColorExport.ColorCount / 2;
                                var cm = arrayColorExport.GetColor(mid);
                                if (cm.HasValue)
                                    info.SampleColors.Add(new ColorValue { Index = mid, R = cm.Value.R, G = cm.Value.G, B = cm.Value.B, A = cm.Value.A });
                            }
                            if (arrayColorExport.ColorCount > 1)
                            {
                                int last = arrayColorExport.ColorCount - 1;
                                var cl = arrayColorExport.GetColor(last);
                                if (cl.HasValue)
                                    info.SampleColors.Add(new ColorValue { Index = last, R = cl.Value.R, G = cl.Value.G, B = cl.Value.B, A = cl.Value.A });
                            }
                        }
                    }

                    result.ArrayColors.Add(info);
                    result.TotalArrayColorValues += info.ColorCount;
                }
                // NiagaraDataInterfaceArrayFloat - float arrays (opacity, scale)
                else if (export is NiagaraDataInterfaceArrayFloatExport arrayFloatExport)
                {
                    var info = new ArrayFloatInfo
                    {
                        ExportIndex = i,
                        ExportName = exportName,
                        ClassName = className,
                        ValueCount = arrayFloatExport.ValueCount
                    };

                    if (fullData)
                    {
                        for (int j = 0; j < arrayFloatExport.ValueCount; j++)
                        {
                            info.SampleValues.Add(new FloatValue { Index = j, Value = arrayFloatExport.GetValue(j) ?? 0 });
                        }
                    }
                    else if (arrayFloatExport.ValueCount > 0)
                    {
                        info.SampleValues.Add(new FloatValue { Index = 0, Value = arrayFloatExport.GetValue(0) ?? 0 });
                        if (arrayFloatExport.ValueCount > 2)
                        {
                            int mid = arrayFloatExport.ValueCount / 2;
                            info.SampleValues.Add(new FloatValue { Index = mid, Value = arrayFloatExport.GetValue(mid) ?? 0 });
                        }
                        if (arrayFloatExport.ValueCount > 1)
                        {
                            int last = arrayFloatExport.ValueCount - 1;
                            info.SampleValues.Add(new FloatValue { Index = last, Value = arrayFloatExport.GetValue(last) ?? 0 });
                        }
                    }

                    result.ArrayFloats.Add(info);
                    result.TotalArrayFloatValues += info.ValueCount;
                }
                // NiagaraDataInterfaceArrayFloat3 - Vector3/RGB arrays
                else if (export is NiagaraDataInterfaceArrayFloat3Export arrayFloat3Export)
                {
                    var info = new ArrayFloat3Info
                    {
                        ExportIndex = i,
                        ExportName = exportName,
                        ClassName = className,
                        ValueCount = arrayFloat3Export.ValueCount
                    };

                    if (fullData)
                    {
                        for (int j = 0; j < arrayFloat3Export.ValueCount; j++)
                        {
                            var v = arrayFloat3Export.GetValue(j);
                            if (v.HasValue)
                                info.SampleValues.Add(new Vector3Value { Index = j, X = v.Value.X, Y = v.Value.Y, Z = v.Value.Z });
                        }
                    }
                    else if (arrayFloat3Export.ValueCount > 0)
                    {
                        var v0 = arrayFloat3Export.GetValue(0);
                        if (v0.HasValue)
                            info.SampleValues.Add(new Vector3Value { Index = 0, X = v0.Value.X, Y = v0.Value.Y, Z = v0.Value.Z });
                        
                        if (arrayFloat3Export.ValueCount > 2)
                        {
                            int mid = arrayFloat3Export.ValueCount / 2;
                            var vm = arrayFloat3Export.GetValue(mid);
                            if (vm.HasValue)
                                info.SampleValues.Add(new Vector3Value { Index = mid, X = vm.Value.X, Y = vm.Value.Y, Z = vm.Value.Z });
                        }
                        if (arrayFloat3Export.ValueCount > 1)
                        {
                            int last = arrayFloat3Export.ValueCount - 1;
                            var vl = arrayFloat3Export.GetValue(last);
                            if (vl.HasValue)
                                info.SampleValues.Add(new Vector3Value { Index = last, X = vl.Value.X, Y = vl.Value.Y, Z = vl.Value.Z });
                        }
                    }

                    result.ArrayFloat3s.Add(info);
                    result.TotalArrayFloat3Values += info.ValueCount;
                }
            }

            result.ColorCurveCount = result.ColorCurves.Count;
            result.FloatCurveCount = result.FloatCurves.Count;
            result.Vector2DCurveCount = result.Vector2DCurves.Count;
            result.Vector3CurveCount = result.Vector3Curves.Count;
            result.ArrayColorCount = result.ArrayColors.Count;
            result.ArrayFloatCount = result.ArrayFloats.Count;
            result.ArrayFloat3Count = result.ArrayFloat3s.Count;
            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
        }

        return JsonSerializer.Serialize(result, JsonOptionsFullPrecision);
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

            // Determine which channels to modify (default: all)
            bool modifyR = request.ModifyR ?? true;
            bool modifyG = request.ModifyG ?? true;
            bool modifyB = request.ModifyB ?? true;
            bool modifyA = request.ModifyA ?? true;

            for (int exportIdx = 0; exportIdx < asset.Exports.Count; exportIdx++)
            {
                // Skip if targeting specific export by index
                if (request.ExportIndex.HasValue && request.ExportIndex.Value != exportIdx)
                    continue;

                var export = asset.Exports[exportIdx];
                string exportName = export.ObjectName?.Value?.Value ?? "";
                string className = export.GetExportClassType()?.Value?.Value ?? "";

                // Skip if export name doesn't match filter pattern
                if (!string.IsNullOrEmpty(request.ExportNameFilter))
                {
                    if (!exportName.Contains(request.ExportNameFilter, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                // Handle specialized ColorCurve export type
                if (export is NiagaraDataInterfaceColorCurveExport colorCurveExport && colorCurveExport.ShaderLUT != null)
                {
                    for (int i = 0; i < colorCurveExport.ShaderLUT.Colors.Count; i++)
                    {
                        if (request.ColorIndex.HasValue && request.ColorIndex.Value != i)
                            continue;
                        if (request.ColorIndexStart.HasValue && i < request.ColorIndexStart.Value)
                            continue;
                        if (request.ColorIndexEnd.HasValue && i > request.ColorIndexEnd.Value)
                            continue;

                        var current = colorCurveExport.ShaderLUT.Colors[i];
                        float newR = modifyR ? request.R : current.R;
                        float newG = modifyG ? request.G : current.G;
                        float newB = modifyB ? request.B : current.B;
                        float newA = modifyA ? request.A : current.A;
                        colorCurveExport.SetColor(i, newR, newG, newB, newA);
                        modifiedCount++;
                    }
                }
                // Fallback for unrecognized ColorCurve types
                else if (className.Contains("ColorCurve") && export is NormalExport normalExport)
                {
                    modifiedCount += ModifyShaderLUTSelective(
                        normalExport.Data, 
                        targetColor, 
                        request.ColorIndex,
                        request.ColorIndexStart,
                        request.ColorIndexEnd,
                        modifyR, modifyG, modifyB, modifyA
                    );
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

    /// <summary>
    /// Edit float curve values in a NiagaraSystem file
    /// </summary>
    public static string EditFloatCurve(FloatCurveEditRequest request, string? usmapPath = null)
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

            int modifiedCount = 0;

            for (int exportIdx = 0; exportIdx < asset.Exports.Count; exportIdx++)
            {
                if (request.ExportIndex.HasValue && request.ExportIndex.Value != exportIdx)
                    continue;

                var export = asset.Exports[exportIdx];
                string exportName = export.ObjectName?.Value?.Value ?? "";
                string className = export.GetExportClassType()?.Value?.Value ?? "";

                if (!string.IsNullOrEmpty(request.ExportNameFilter))
                {
                    if (!exportName.Contains(request.ExportNameFilter, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                // Handle structured export
                if (export is NiagaraDataInterfaceCurveExport floatCurveExport && floatCurveExport.ShaderLUT != null)
                {
                    for (int i = 0; i < floatCurveExport.ShaderLUT.Values.Count; i++)
                    {
                        if (request.ValueIndex.HasValue && request.ValueIndex.Value != i)
                            continue;
                        if (request.ValueIndexStart.HasValue && i < request.ValueIndexStart.Value)
                            continue;
                        if (request.ValueIndexEnd.HasValue && i > request.ValueIndexEnd.Value)
                            continue;

                        floatCurveExport.SetValue(i, request.Value);
                        modifiedCount++;
                    }
                }
                // Fallback for unrecognized types
                else if (className.Contains("DataInterfaceCurve") && !className.Contains("Color") && !className.Contains("Vector") && export is NormalExport normalExport)
                {
                    modifiedCount += ModifyFloatShaderLUT(normalExport.Data, request.Value, request.ValueIndex, request.ValueIndexStart, request.ValueIndexEnd);
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

    /// <summary>
    /// Edit Vector2D curve values in a NiagaraSystem file
    /// </summary>
    public static string EditVector2DCurve(Vector2DCurveEditRequest request, string? usmapPath = null)
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

            bool modifyX = request.ModifyX ?? true;
            bool modifyY = request.ModifyY ?? true;
            int modifiedCount = 0;

            for (int exportIdx = 0; exportIdx < asset.Exports.Count; exportIdx++)
            {
                if (request.ExportIndex.HasValue && request.ExportIndex.Value != exportIdx)
                    continue;

                var export = asset.Exports[exportIdx];
                string exportName = export.ObjectName?.Value?.Value ?? "";
                string className = export.GetExportClassType()?.Value?.Value ?? "";

                if (!string.IsNullOrEmpty(request.ExportNameFilter))
                {
                    if (!exportName.Contains(request.ExportNameFilter, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                // Handle structured export
                if (export is NiagaraDataInterfaceVector2DCurveExport vec2CurveExport && vec2CurveExport.ShaderLUT != null)
                {
                    for (int i = 0; i < vec2CurveExport.ShaderLUT.Values.Count; i++)
                    {
                        if (request.ValueIndex.HasValue && request.ValueIndex.Value != i)
                            continue;
                        if (request.ValueIndexStart.HasValue && i < request.ValueIndexStart.Value)
                            continue;
                        if (request.ValueIndexEnd.HasValue && i > request.ValueIndexEnd.Value)
                            continue;

                        var current = vec2CurveExport.ShaderLUT.Values[i];
                        float newX = modifyX ? request.X : current.X;
                        float newY = modifyY ? request.Y : current.Y;
                        vec2CurveExport.SetValue(i, newX, newY);
                        modifiedCount++;
                    }
                }
                // Fallback for unrecognized types
                else if (className.Contains("Vector2DCurve") && export is NormalExport normalExport)
                {
                    modifiedCount += ModifyVector2DShaderLUT(normalExport.Data, request.X, request.Y, request.ValueIndex, request.ValueIndexStart, request.ValueIndexEnd, modifyX, modifyY);
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

    /// <summary>
    /// Edit Vector3 curve values in a NiagaraSystem file (XYZ/RGB)
    /// </summary>
    public static string EditVector3Curve(Vector3CurveEditRequest request, string? usmapPath = null)
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

            bool modifyX = request.ModifyX ?? true;
            bool modifyY = request.ModifyY ?? true;
            bool modifyZ = request.ModifyZ ?? true;
            int modifiedCount = 0;

            for (int exportIdx = 0; exportIdx < asset.Exports.Count; exportIdx++)
            {
                if (request.ExportIndex.HasValue && request.ExportIndex.Value != exportIdx)
                    continue;

                var export = asset.Exports[exportIdx];
                string exportName = export.ObjectName?.Value?.Value ?? "";
                string className = export.GetExportClassType()?.Value?.Value ?? "";

                if (!string.IsNullOrEmpty(request.ExportNameFilter))
                {
                    if (!exportName.Contains(request.ExportNameFilter, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                // Handle structured export
                if (export is NiagaraDataInterfaceVectorCurveExport vec3CurveExport && vec3CurveExport.ShaderLUT != null)
                {
                    for (int i = 0; i < vec3CurveExport.ShaderLUT.Values.Count; i++)
                    {
                        if (request.ValueIndex.HasValue && request.ValueIndex.Value != i)
                            continue;
                        if (request.ValueIndexStart.HasValue && i < request.ValueIndexStart.Value)
                            continue;
                        if (request.ValueIndexEnd.HasValue && i > request.ValueIndexEnd.Value)
                            continue;

                        var current = vec3CurveExport.ShaderLUT.Values[i];
                        float newX = modifyX ? request.X : current.X;
                        float newY = modifyY ? request.Y : current.Y;
                        float newZ = modifyZ ? request.Z : current.Z;
                        vec3CurveExport.SetValue(i, newX, newY, newZ);
                        modifiedCount++;
                    }
                }
                // Fallback for unrecognized types
                else if (className.Contains("VectorCurve") && export is NormalExport normalExport)
                {
                    modifiedCount += ModifyVector3ShaderLUT(normalExport.Data, request.X, request.Y, request.Z, request.ValueIndex, request.ValueIndexStart, request.ValueIndexEnd, modifyX, modifyY, modifyZ);
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

    private static List<FloatValue> ExtractFloatsFromExport(NormalExport export)
    {
        var values = new List<FloatValue>();
        if (export.Data == null) return values;

        foreach (var prop in export.Data)
        {
            if (prop.Name?.Value?.Value != "ShaderLUT") continue;
            if (prop is not ArrayPropertyData lutArray) continue;

            for (int i = 0; i < lutArray.Value.Length; i++)
            {
                if (lutArray.Value[i] is FloatPropertyData floatProp)
                {
                    values.Add(new FloatValue
                    {
                        Index = i,
                        Value = floatProp.Value
                    });
                }
            }
        }

        return values;
    }

    private static List<Vector2DValue> ExtractVector2DsFromExport(NormalExport export)
    {
        var values = new List<Vector2DValue>();
        if (export.Data == null) return values;

        foreach (var prop in export.Data)
        {
            if (prop.Name?.Value?.Value != "ShaderLUT") continue;
            if (prop is not ArrayPropertyData lutArray) continue;

            for (int i = 0; i + 1 < lutArray.Value.Length; i += 2)
            {
                if (lutArray.Value[i] is FloatPropertyData xProp &&
                    lutArray.Value[i + 1] is FloatPropertyData yProp)
                {
                    values.Add(new Vector2DValue
                    {
                        Index = i / 2,
                        X = xProp.Value,
                        Y = yProp.Value
                    });
                }
            }
        }

        return values;
    }

    private static List<Vector3Value> ExtractVector3sFromExport(NormalExport export)
    {
        var values = new List<Vector3Value>();
        if (export.Data == null) return values;

        foreach (var prop in export.Data)
        {
            if (prop.Name?.Value?.Value != "ShaderLUT") continue;
            if (prop is not ArrayPropertyData lutArray) continue;

            for (int i = 0; i + 2 < lutArray.Value.Length; i += 3)
            {
                if (lutArray.Value[i] is FloatPropertyData xProp &&
                    lutArray.Value[i + 1] is FloatPropertyData yProp &&
                    lutArray.Value[i + 2] is FloatPropertyData zProp)
                {
                    values.Add(new Vector3Value
                    {
                        Index = i / 3,
                        X = xProp.Value,
                        Y = yProp.Value,
                        Z = zProp.Value
                    });
                }
            }
        }

        return values;
    }

    private static int ModifyFloatShaderLUT(List<PropertyData> properties, float value, int? specificIndex, int? indexStart, int? indexEnd)
    {
        int count = 0;

        foreach (var prop in properties)
        {
            if (prop.Name?.Value?.Value != "ShaderLUT") continue;
            if (prop is not ArrayPropertyData lutArray) continue;

            for (int i = 0; i < lutArray.Value.Length; i++)
            {
                if (specificIndex.HasValue && specificIndex.Value != i)
                    continue;
                if (indexStart.HasValue && i < indexStart.Value)
                    continue;
                if (indexEnd.HasValue && i > indexEnd.Value)
                    continue;

                if (lutArray.Value[i] is FloatPropertyData floatProp)
                {
                    floatProp.Value = value;
                    count++;
                }
            }
        }

        return count;
    }

    private static int ModifyVector2DShaderLUT(List<PropertyData> properties, float x, float y, int? specificIndex, int? indexStart, int? indexEnd, bool modifyX, bool modifyY)
    {
        int count = 0;

        foreach (var prop in properties)
        {
            if (prop.Name?.Value?.Value != "ShaderLUT") continue;
            if (prop is not ArrayPropertyData lutArray) continue;

            for (int i = 0; i + 1 < lutArray.Value.Length; i += 2)
            {
                int vecIndex = i / 2;

                if (specificIndex.HasValue && specificIndex.Value != vecIndex)
                    continue;
                if (indexStart.HasValue && vecIndex < indexStart.Value)
                    continue;
                if (indexEnd.HasValue && vecIndex > indexEnd.Value)
                    continue;

                if (lutArray.Value[i] is FloatPropertyData xProp &&
                    lutArray.Value[i + 1] is FloatPropertyData yProp)
                {
                    if (modifyX) xProp.Value = x;
                    if (modifyY) yProp.Value = y;
                    count++;
                }
            }
        }

        return count;
    }

    private static int ModifyVector3ShaderLUT(List<PropertyData> properties, float x, float y, float z, int? specificIndex, int? indexStart, int? indexEnd, bool modifyX, bool modifyY, bool modifyZ)
    {
        int count = 0;

        foreach (var prop in properties)
        {
            if (prop.Name?.Value?.Value != "ShaderLUT") continue;
            if (prop is not ArrayPropertyData lutArray) continue;

            for (int i = 0; i + 2 < lutArray.Value.Length; i += 3)
            {
                int vecIndex = i / 3;

                if (specificIndex.HasValue && specificIndex.Value != vecIndex)
                    continue;
                if (indexStart.HasValue && vecIndex < indexStart.Value)
                    continue;
                if (indexEnd.HasValue && vecIndex > indexEnd.Value)
                    continue;

                if (lutArray.Value[i] is FloatPropertyData xProp &&
                    lutArray.Value[i + 1] is FloatPropertyData yProp &&
                    lutArray.Value[i + 2] is FloatPropertyData zProp)
                {
                    if (modifyX) xProp.Value = x;
                    if (modifyY) yProp.Value = y;
                    if (modifyZ) zProp.Value = z;
                    count++;
                }
            }
        }

        return count;
    }

    private static int ModifyShaderLUT(List<PropertyData> properties, FLinearColor targetColor, int? specificColorIndex = null)
    {
        return ModifyShaderLUTSelective(properties, targetColor, specificColorIndex, null, null, true, true, true, true);
    }

    /// <summary>
    /// Modify ShaderLUT with selective filtering options.
    /// </summary>
    /// <param name="properties">The property list containing ShaderLUT</param>
    /// <param name="targetColor">Target color values</param>
    /// <param name="specificColorIndex">If set, only modify this exact color index</param>
    /// <param name="colorIndexStart">If set, start of color index range (inclusive)</param>
    /// <param name="colorIndexEnd">If set, end of color index range (inclusive)</param>
    /// <param name="modifyR">Whether to modify R channel</param>
    /// <param name="modifyG">Whether to modify G channel</param>
    /// <param name="modifyB">Whether to modify B channel</param>
    /// <param name="modifyA">Whether to modify A channel</param>
    private static int ModifyShaderLUTSelective(
        List<PropertyData> properties, 
        FLinearColor targetColor, 
        int? specificColorIndex,
        int? colorIndexStart,
        int? colorIndexEnd,
        bool modifyR, bool modifyG, bool modifyB, bool modifyA)
    {
        int count = 0;

        foreach (var prop in properties)
        {
            if (prop.Name?.Value?.Value != "ShaderLUT") continue;
            if (prop is not ArrayPropertyData lutArray) continue;

            for (int i = 0; i + 3 < lutArray.Value.Length; i += 4)
            {
                int colorIndex = i / 4;

                // Skip if targeting specific color index
                if (specificColorIndex.HasValue && specificColorIndex.Value != colorIndex)
                    continue;

                // Skip if outside color index range
                if (colorIndexStart.HasValue && colorIndex < colorIndexStart.Value)
                    continue;
                if (colorIndexEnd.HasValue && colorIndex > colorIndexEnd.Value)
                    continue;

                if (lutArray.Value[i] is FloatPropertyData rProp &&
                    lutArray.Value[i + 1] is FloatPropertyData gProp &&
                    lutArray.Value[i + 2] is FloatPropertyData bProp &&
                    lutArray.Value[i + 3] is FloatPropertyData aProp)
                {
                    if (modifyR) rProp.Value = targetColor.R;
                    if (modifyG) gProp.Value = targetColor.G;
                    if (modifyB) bProp.Value = targetColor.B;
                    if (modifyA) aProp.Value = targetColor.A;
                    count++;
                }
            }
        }

        return count;
    }

    #endregion
}
