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
using UAssetAPI.Unversioned;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;

namespace UAssetTool;

/// <summary>
/// Service for low-RAM Niagara asset editing using selective export parsing.
/// Only color-relevant exports are fully parsed; all others stay as raw bytes.
/// </summary>
public static class NiagaraService
{
    // ── Color-relevant export class types ──
    private static readonly HashSet<string> ColorClassTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "NiagaraDataInterfaceColorCurve",
        "NiagaraDataInterfaceArrayColor",
        "NiagaraDataInterfaceVector4Curve",
        "NiagaraDataInterfaceVectorCurve",
        "NiagaraDataInterfaceArrayFloat4",
        "NiagaraDataInterfaceArrayFloat3",
        "NiagaraDataInterfaceCurve",
        "NiagaraDataInterfaceVector2DCurve",
    };

    // ── JSON options ──
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ── Data models ──

    public class NiagaraColorInfo
    {
        public string FileName { get; set; } = "";
        public int TotalExports { get; set; }
        public int ColorExports { get; set; }
        public List<ColorExportInfo> Exports { get; set; } = new();
    }

    public class ColorExportInfo
    {
        public int ExportIndex { get; set; }
        public string ClassType { get; set; } = "";
        public string Name { get; set; } = "";
        public int Channels { get; set; }
        public ShaderLutInfo? ShaderLut { get; set; }
        public List<float[]>? ColorData { get; set; }
    }

    public class ShaderLutInfo
    {
        public int FloatCount { get; set; }
        public int SampleCount { get; set; }
        public float MinTime { get; set; }
        public float MaxTime { get; set; }
        public List<float[]> Samples { get; set; } = new();
    }

    public class NiagaraEditRequest
    {
        public int ExportIndex { get; set; }
        public float[]? FlatLut { get; set; }
        public List<float[]>? ColorData { get; set; }
    }

    // ── Core: Load asset with selective parsing ──

    /// <summary>
    /// Loads a Niagara asset, parsing only color-relevant exports.
    /// Returns (asset, colorExportIndices) for further operations.
    /// </summary>
    public static (UAsset asset, List<int> colorIndices) LoadSelective(string assetPath, Usmap? mappings)
    {
        // Pass 1: header-only load to discover export class types
        var headerAsset = new UAsset(assetPath, EngineVersion.VER_UE5_3, mappings,
            customSerializationFlags: CustomSerializationFlags.SkipParsingExports);

        var colorIndices = new List<int>();
        int exportCount = headerAsset.Exports.Count;

        for (int i = 0; i < exportCount; i++)
        {
            string classType = headerAsset.Exports[i].GetExportClassType().Value.Value;
            if (ColorClassTypes.Contains(classType))
                colorIndices.Add(i);
        }

        // Free pass-1 asset
        headerAsset = null;

        // Pass 2: reload with selective parsing
        var asset = new UAsset();
        asset.FilePath = assetPath;
        asset.Mappings = mappings;
        asset.CustomSerializationFlags = CustomSerializationFlags.None;
        asset.SetEngineVersion(EngineVersion.VER_UE5_3);

        int[] manualSkips = Enumerable.Range(0, exportCount).ToArray();
        int[] forceReads = colorIndices.ToArray();

        asset.Read(asset.PathToReader(assetPath), manualSkips, forceReads);

        return (asset, colorIndices);
    }

    // ── Details: Extract color info as JSON ──

    /// <summary>
    /// Returns JSON describing all color-relevant exports in a Niagara asset.
    /// </summary>
    public static string GetColorDetails(string assetPath, Usmap? mappings)
    {
        var (asset, colorIndices) = LoadSelective(assetPath, mappings);

        var info = new NiagaraColorInfo
        {
            FileName = Path.GetFileName(assetPath),
            TotalExports = asset.Exports.Count,
            ColorExports = colorIndices.Count,
        };

        foreach (int idx in colorIndices)
        {
            if (idx >= asset.Exports.Count) continue;
            var export = asset.Exports[idx];
            string classType = export.GetExportClassType().Value.Value;

            var exportInfo = new ColorExportInfo
            {
                ExportIndex = idx,
                ClassType = classType,
                Name = export.ObjectName?.Value?.Value ?? $"Export_{idx}",
                Channels = GetChannelCount(classType),
            };

            if (export is NormalExport normalExport)
            {
                // ShaderLUT (curve-based types)
                var lutProp = FindProp(normalExport.Data, "ShaderLUT");
                if (lutProp is ArrayPropertyData lutArray)
                {
                    int channels = exportInfo.Channels;
                    int sampleCount = lutArray.Value.Length / Math.Max(channels, 1);
                    float minTime = GetFloatProp(normalExport.Data, "LUTMinTime");
                    float maxTime = GetFloatProp(normalExport.Data, "LUTMaxTime");

                    var lutInfo = new ShaderLutInfo
                    {
                        FloatCount = lutArray.Value.Length,
                        SampleCount = sampleCount,
                        MinTime = minTime,
                        MaxTime = maxTime,
                    };

                    // Include all samples
                    for (int s = 0; s < sampleCount; s++)
                    {
                        var sample = new float[channels];
                        for (int c = 0; c < channels && (s * channels + c) < lutArray.Value.Length; c++)
                        {
                            if (lutArray.Value[s * channels + c] is FloatPropertyData fp)
                                sample[c] = fp.Value;
                        }
                        lutInfo.Samples.Add(sample);
                    }
                    exportInfo.ShaderLut = lutInfo;
                }

                // ColorData (array-based types)
                var colorDataProp = FindProp(normalExport.Data, "ColorData");
                if (colorDataProp is ArrayPropertyData colorArray)
                {
                    exportInfo.ColorData = new List<float[]>();
                    foreach (var entry in colorArray.Value)
                    {
                        if (entry is StructPropertyData colorStruct)
                        {
                            exportInfo.ColorData.Add(ExtractLinearColor(colorStruct));
                        }
                    }
                }

                // InternalFloatData (ArrayFloat3/Float4 types)
                var internalProp = FindProp(normalExport.Data, "InternalFloatData");
                if (internalProp is ArrayPropertyData internalArray)
                {
                    exportInfo.ColorData = new List<float[]>();
                    foreach (var entry in internalArray.Value)
                    {
                        if (entry is StructPropertyData vecStruct)
                        {
                            exportInfo.ColorData.Add(ExtractVectorAsFloats(vecStruct));
                        }
                    }
                }
            }

            info.Exports.Add(exportInfo);
        }

        return JsonSerializer.Serialize(info, JsonOpts);
    }

    // ── Edit: Apply color changes ──

    /// <summary>
    /// Edits color data in a Niagara asset and writes the result.
    /// </summary>
    public static void EditColors(string assetPath, string outputPath, Usmap? mappings, List<NiagaraEditRequest> edits)
    {
        var (asset, colorIndices) = LoadSelective(assetPath, mappings);
        var colorSet = new HashSet<int>(colorIndices);

        foreach (var edit in edits)
        {
            int idx = edit.ExportIndex;
            if (!colorSet.Contains(idx))
                throw new ArgumentException($"Export {idx} is not a color-relevant export.");
            if (idx >= asset.Exports.Count)
                throw new ArgumentException($"Export {idx} out of range (max {asset.Exports.Count - 1}).");

            var export = asset.Exports[idx];
            if (export is not NormalExport normalExport)
                throw new InvalidOperationException($"Export {idx} failed to parse — cannot edit.");

            string classType = normalExport.GetExportClassType().Value.Value;

            // Edit ShaderLUT
            if (edit.FlatLut != null)
            {
                var lutProp = FindProp(normalExport.Data, "ShaderLUT");
                if (lutProp is ArrayPropertyData lutArray)
                {
                    if (edit.FlatLut.Length != lutArray.Value.Length)
                        throw new ArgumentException(
                            $"Export {idx}: FlatLut length {edit.FlatLut.Length} != existing {lutArray.Value.Length}. Must match exactly.");

                    for (int i = 0; i < edit.FlatLut.Length; i++)
                    {
                        if (lutArray.Value[i] is FloatPropertyData fp)
                            fp.Value = edit.FlatLut[i];
                    }
                }
                else
                {
                    throw new InvalidOperationException($"Export {idx} has no ShaderLUT property.");
                }
            }

            // Edit ColorData / InternalFloatData
            if (edit.ColorData != null)
            {
                ArrayPropertyData? arrayProp = null;

                var colorDataProp = FindProp(normalExport.Data, "ColorData");
                if (colorDataProp is ArrayPropertyData cd) arrayProp = cd;

                var internalProp = FindProp(normalExport.Data, "InternalFloatData");
                if (internalProp is ArrayPropertyData ip) arrayProp = ip;

                if (arrayProp == null)
                    throw new InvalidOperationException($"Export {idx} has no ColorData or InternalFloatData property.");

                if (edit.ColorData.Count != arrayProp.Value.Length)
                    throw new ArgumentException(
                        $"Export {idx}: ColorData count {edit.ColorData.Count} != existing {arrayProp.Value.Length}. Must match exactly.");

                for (int i = 0; i < edit.ColorData.Count; i++)
                {
                    if (arrayProp.Value[i] is StructPropertyData structEntry)
                        WriteLinearColor(structEntry, edit.ColorData[i]);
                }
            }
        }

        asset.Write(outputPath);
    }

    // ── MakeGreen: Recolor all color exports to green ──

    /// <summary>
    /// Recolors all color-relevant exports in a Niagara asset to green and writes the result.
    /// - ColorCurve/Vector4Curve (4ch): R=0, G=max(original RGB brightness, 0.5), B=0, A=unchanged
    /// - VectorCurve (3ch): X=0, Y=max(original brightness, 0.5), Z=0
    /// - ArrayColor/ArrayFloat4 (4ch): R=0, G=max(original RGB brightness, 0.5), B=0, A=unchanged
    /// - ArrayFloat3 (3ch): X=0, Y=max(original brightness, 0.5), Z=0
    /// - Curve (1ch) and Vector2DCurve (2ch): unchanged (opacity/size/UV, not color)
    /// Returns the number of exports modified.
    /// </summary>
    public static int MakeGreen(string assetPath, string outputPath, Usmap? mappings)
    {
        var (asset, colorIndices) = LoadSelective(assetPath, mappings);
        int modified = 0;

        foreach (int idx in colorIndices)
        {
            if (idx >= asset.Exports.Count) continue;
            var export = asset.Exports[idx];
            if (export is not NormalExport ne) continue;

            string classType = ne.GetExportClassType().Value.Value;
            int channels = GetChannelCount(classType);

            // Skip 1-channel (scalar curves = opacity/size) and 2-channel (Vector2D = UV)
            if (channels < 3) continue;

            // ShaderLUT recolor
            var lutProp = FindProp(ne.Data, "ShaderLUT");
            if (lutProp is ArrayPropertyData lutArray && lutArray.Value.Length > 0)
            {
                for (int s = 0; s < lutArray.Value.Length / channels; s++)
                {
                    int baseIdx = s * channels;
                    // Read original values to preserve brightness
                    float origR = (lutArray.Value[baseIdx] is FloatPropertyData f0) ? f0.Value : 0;
                    float origG = (baseIdx + 1 < lutArray.Value.Length && lutArray.Value[baseIdx + 1] is FloatPropertyData f1) ? f1.Value : 0;
                    float origB = (baseIdx + 2 < lutArray.Value.Length && lutArray.Value[baseIdx + 2] is FloatPropertyData f2) ? f2.Value : 0;
                    float brightness = Math.Max(Math.Max(origR, origG), Math.Max(origB, 0.5f));

                    // Set R=0, G=brightness, B=0
                    if (lutArray.Value[baseIdx] is FloatPropertyData fpR) fpR.Value = 0f;
                    if (baseIdx + 1 < lutArray.Value.Length && lutArray.Value[baseIdx + 1] is FloatPropertyData fpG) fpG.Value = brightness;
                    if (baseIdx + 2 < lutArray.Value.Length && lutArray.Value[baseIdx + 2] is FloatPropertyData fpB) fpB.Value = 0f;
                    // A (index +3) stays unchanged for 4-channel types
                }
                modified++;
            }

            // ColorData recolor (ArrayColor)
            var colorDataProp = FindProp(ne.Data, "ColorData");
            if (colorDataProp is ArrayPropertyData colorArray && colorArray.Value.Length > 0)
            {
                foreach (var entry in colorArray.Value)
                {
                    if (entry is StructPropertyData colorStruct && colorStruct.Value != null)
                    {
                        float origR = 0, origG = 0, origB = 0;
                        foreach (var prop in colorStruct.Value)
                        {
                            if (prop is FloatPropertyData fp)
                            {
                                var n = prop.Name?.Value?.Value;
                                if (n == "R") origR = fp.Value;
                                else if (n == "G") origG = fp.Value;
                                else if (n == "B") origB = fp.Value;
                            }
                        }
                        float brightness = Math.Max(Math.Max(origR, origG), Math.Max(origB, 0.5f));
                        foreach (var prop in colorStruct.Value)
                        {
                            if (prop is FloatPropertyData fp)
                            {
                                var n = prop.Name?.Value?.Value;
                                if (n == "R") fp.Value = 0f;
                                else if (n == "G") fp.Value = brightness;
                                else if (n == "B") fp.Value = 0f;
                                // A stays unchanged
                            }
                        }
                    }
                }
                modified++;
            }

            // InternalFloatData recolor (ArrayFloat3/Float4)
            var internalProp = FindProp(ne.Data, "InternalFloatData");
            if (internalProp is ArrayPropertyData internalArray && internalArray.Value.Length > 0)
            {
                foreach (var entry in internalArray.Value)
                {
                    if (entry is StructPropertyData vecStruct && vecStruct.Value != null)
                    {
                        var floats = vecStruct.Value.OfType<FloatPropertyData>().ToList();
                        if (floats.Count >= 3)
                        {
                            float brightness = Math.Max(Math.Max(floats[0].Value, floats[1].Value), Math.Max(floats[2].Value, 0.5f));
                            floats[0].Value = 0f;       // R/X
                            floats[1].Value = brightness; // G/Y
                            floats[2].Value = 0f;       // B/Z
                            // A (index 3) stays unchanged if present
                        }
                    }
                }
                modified++;
            }
        }

        asset.Write(outputPath);
        return modified;
    }

    // ── Helpers ──

    private static int GetChannelCount(string classType)
    {
        if (classType.Contains("Color", StringComparison.OrdinalIgnoreCase)) return 4;
        if (classType.Contains("Vector4", StringComparison.OrdinalIgnoreCase)) return 4;
        if (classType.Contains("VectorCurve", StringComparison.OrdinalIgnoreCase)) return 3;
        if (classType.Contains("Vector2D", StringComparison.OrdinalIgnoreCase)) return 2;
        if (classType.Contains("ArrayFloat4", StringComparison.OrdinalIgnoreCase)) return 4;
        if (classType.Contains("ArrayFloat3", StringComparison.OrdinalIgnoreCase)) return 3;
        return 1; // scalar curve
    }

    private static PropertyData? FindProp(List<PropertyData>? props, string name)
    {
        if (props == null) return null;
        foreach (var p in props)
            if (p.Name?.Value?.Value == name) return p;
        return null;
    }

    private static float GetFloatProp(List<PropertyData>? props, string name)
    {
        var p = FindProp(props, name);
        return p is FloatPropertyData fp ? fp.Value : 0f;
    }

    private static float[] ExtractLinearColor(StructPropertyData colorStruct)
    {
        float r = 0, g = 0, b = 0, a = 0;
        if (colorStruct.Value != null)
        {
            foreach (var prop in colorStruct.Value)
            {
                if (prop is FloatPropertyData fp)
                {
                    var n = prop.Name?.Value?.Value;
                    if (n == "R") r = fp.Value;
                    else if (n == "G") g = fp.Value;
                    else if (n == "B") b = fp.Value;
                    else if (n == "A") a = fp.Value;
                }
            }
        }
        return new[] { r, g, b, a };
    }

    private static float[] ExtractVectorAsFloats(StructPropertyData vecStruct)
    {
        var vals = new List<float>();
        if (vecStruct.Value != null)
        {
            foreach (var prop in vecStruct.Value)
            {
                if (prop is FloatPropertyData fp)
                    vals.Add(fp.Value);
            }
        }
        return vals.ToArray();
    }

    private static void WriteLinearColor(StructPropertyData colorStruct, float[] rgba)
    {
        if (colorStruct.Value == null) return;
        int ci = 0;
        foreach (var prop in colorStruct.Value)
        {
            if (prop is FloatPropertyData fp)
            {
                var n = prop.Name?.Value?.Value;
                if (n == "R" && ci < rgba.Length) { fp.Value = rgba[0]; ci++; }
                else if (n == "G" && rgba.Length > 1) { fp.Value = rgba[1]; ci++; }
                else if (n == "B" && rgba.Length > 2) { fp.Value = rgba[2]; ci++; }
                else if (n == "A" && rgba.Length > 3) { fp.Value = rgba[3]; ci++; }
            }
        }
    }
}
