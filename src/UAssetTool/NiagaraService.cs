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
        public int OuterIndex { get; set; }
        public string? EmitterName { get; set; }
        public bool EmitterHasEnemyParams { get; set; }
        public List<string>? EnemyParams { get; set; }
        public string? ParentName { get; set; }
        public string? ParentChain { get; set; }
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
        var info = BuildColorInfo(asset, colorIndices, assetPath);
        return JsonSerializer.Serialize(info, JsonOpts);
    }

    /// <summary>
    /// Returns a rich Dictionary matching the format the Avalonia app expects
    /// (equivalent to the old ProcessSingleNiagaraDetails).
    /// Includes: outerIndex, emitterName, enemyParams, classification, sampleColors.
    /// </summary>
    public static Dictionary<string, object?> GetColorDetailsForUI(string assetPath, Usmap? mappings, bool fullMode = true)
    {
        var (asset, colorIndices) = LoadSelective(assetPath, mappings);
        var info = BuildColorInfo(asset, colorIndices, assetPath);

        // Detect enemy FNames in the asset name map
        var enemyColorFNames = DetectEnemyFNames(asset);
        var emittersWithEnemyParams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ef in enemyColorFNames)
        {
            if (ef.TryGetValue("emitter", out var em) && em is string emStr && !string.IsNullOrEmpty(emStr))
                emittersWithEnemyParams.Add(emStr);
        }

        var colorCurves = new List<Dictionary<string, object?>>();
        var arrayColors = new List<Dictionary<string, object?>>();
        int totalColorCount = 0;
        int totalArrayColorValues = 0;

        foreach (var exp in info.Exports)
        {
            // Resolve outer chain
            string? emitterName = ResolveEmitterName(asset, exp.ExportIndex);
            string parentChain = ResolveOuterChain(asset, exp.ExportIndex);
            string? parentName = ResolveOuterName(asset, exp.ExportIndex);
            int outerIndex = asset.Exports[exp.ExportIndex].OuterIndex.Index;

            bool emitterHasEnemy = emitterName != null && emittersWithEnemyParams.Contains(emitterName);
            var curveEnemyParams = emitterName != null
                ? enemyColorFNames
                    .Where(f => f.TryGetValue("emitter", out var em) && em is string emS && emS.Equals(emitterName, StringComparison.OrdinalIgnoreCase))
                    .Select(f => f.TryGetValue("fname", out var fn) ? fn?.ToString() ?? "" : "")
                    .Where(s => s.Length > 0)
                    .ToList()
                : new List<string>();

            // Build sample colors list (unified RGBA format)
            var sampleColors = new List<Dictionary<string, object>>();
            if (exp.ShaderLut != null)
            {
                int channels = exp.Channels;
                for (int s = 0; s < exp.ShaderLut.Samples.Count; s++)
                {
                    var sample = exp.ShaderLut.Samples[s];
                    sampleColors.Add(new Dictionary<string, object>
                    {
                        ["index"] = s,
                        ["r"] = channels >= 1 ? sample[0] : 0f,
                        ["g"] = channels >= 2 ? sample[1] : 0f,
                        ["b"] = channels >= 3 ? sample[2] : 0f,
                        ["a"] = channels >= 4 ? sample[3] : 1f,
                    });
                }

                var classification = ClassifyColorCurve(sampleColors);
                colorCurves.Add(new Dictionary<string, object?>
                {
                    ["exportIndex"] = exp.ExportIndex,
                    ["exportName"] = exp.Name,
                    ["colorCount"] = exp.ShaderLut.SampleCount,
                    ["outerIndex"] = outerIndex,
                    ["sampleColors"] = sampleColors,
                    ["parentName"] = parentName,
                    ["parentChain"] = parentChain,
                    ["emitterName"] = emitterName,
                    ["emitterHasEnemyParams"] = emitterHasEnemy,
                    ["enemyParams"] = curveEnemyParams,
                    ["classification"] = classification,
                });
                totalColorCount += exp.ShaderLut.SampleCount;
            }
            else if (exp.ColorData != null)
            {
                for (int s = 0; s < exp.ColorData.Count; s++)
                {
                    var cd = exp.ColorData[s];
                    sampleColors.Add(new Dictionary<string, object>
                    {
                        ["index"] = s,
                        ["r"] = cd.Length >= 1 ? cd[0] : 0f,
                        ["g"] = cd.Length >= 2 ? cd[1] : 0f,
                        ["b"] = cd.Length >= 3 ? cd[2] : 0f,
                        ["a"] = cd.Length >= 4 ? cd[3] : 1f,
                    });
                }

                var classification = ClassifyColorCurve(sampleColors);
                arrayColors.Add(new Dictionary<string, object?>
                {
                    ["exportIndex"] = exp.ExportIndex,
                    ["exportName"] = exp.Name,
                    ["colorCount"] = exp.ColorData.Count,
                    ["outerIndex"] = outerIndex,
                    ["sampleColors"] = sampleColors,
                    ["parentName"] = parentName,
                    ["parentChain"] = parentChain,
                    ["emitterName"] = emitterName,
                    ["emitterHasEnemyParams"] = emitterHasEnemy,
                    ["enemyParams"] = curveEnemyParams,
                    ["classification"] = classification,
                });
                totalArrayColorValues += exp.ColorData.Count;
            }
        }

        var result = new Dictionary<string, object?>
        {
            ["success"] = true,
            ["totalExports"] = info.TotalExports,
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

        return result;
    }

    /// <summary>
    /// Build NiagaraColorInfo from a selectively-loaded asset.
    /// </summary>
    private static NiagaraColorInfo BuildColorInfo(UAsset asset, List<int> colorIndices, string assetPath)
    {
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
                OuterIndex = export.OuterIndex.Index,
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
                            exportInfo.ColorData.Add(ExtractLinearColor(colorStruct));
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
                            exportInfo.ColorData.Add(ExtractVectorAsFloats(vecStruct));
                    }
                }
            }

            info.Exports.Add(exportInfo);
        }

        return info;
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

    // ── OuterIndex / EmitterName / EnemyParams Resolution ──

    /// <summary>
    /// Walk the outer chain from an export index up to the NiagaraEmitter parent.
    /// </summary>
    private static string? ResolveEmitterName(UAsset asset, int exportIndex)
    {
        int walkIdx = exportIndex;
        while (walkIdx >= 0 && walkIdx < asset.Exports.Count)
        {
            var walkExp = asset.Exports[walkIdx];
            string walkClass = walkExp.GetExportClassType()?.Value?.Value ?? "";
            if (walkClass == "NiagaraEmitter")
                return walkExp.ObjectName?.Value?.Value;
            int outerRaw = walkExp.OuterIndex.Index;
            if (outerRaw > 0) walkIdx = outerRaw - 1; else break;
        }
        return null;
    }

    /// <summary>
    /// Build a display string for the outer export chain (e.g. "NiagaraSystem > NiagaraEmitter > NiagaraScript").
    /// </summary>
    private static string ResolveOuterChain(UAsset asset, int exportIndex)
    {
        var parts = new List<string>();
        int walkIdx = exportIndex;
        int safety = 0;
        while (walkIdx >= 0 && walkIdx < asset.Exports.Count && safety++ < 50)
        {
            var exp = asset.Exports[walkIdx];
            string cls = exp.GetExportClassType()?.Value?.Value ?? "Unknown";
            string name = exp.ObjectName?.Value?.Value ?? $"Export_{walkIdx}";
            parts.Add($"{cls}:{name}");
            int outerRaw = exp.OuterIndex.Index;
            if (outerRaw > 0) walkIdx = outerRaw - 1; else break;
        }
        parts.Reverse();
        return string.Join(" > ", parts);
    }

    /// <summary>
    /// Get the immediate outer export's name.
    /// </summary>
    private static string? ResolveOuterName(UAsset asset, int exportIndex)
    {
        if (exportIndex < 0 || exportIndex >= asset.Exports.Count) return null;
        int outerRaw = asset.Exports[exportIndex].OuterIndex.Index;
        if (outerRaw > 0 && outerRaw - 1 < asset.Exports.Count)
            return asset.Exports[outerRaw - 1].ObjectName?.Value?.Value;
        return null;
    }

    /// <summary>
    /// Scan the asset's name map for enemy-related FNames (EnemyColor, EnemyValue).
    /// Returns list of dicts with "fname" and "emitter" keys.
    /// </summary>
    private static List<Dictionary<string, object?>> DetectEnemyFNames(UAsset asset)
    {
        var results = new List<Dictionary<string, object?>>();
        if (asset.GetNameMapIndexList() == null) return results;

        foreach (var nameEntry in asset.GetNameMapIndexList())
        {
            string name = nameEntry.Value;
            string lower = name.ToLowerInvariant();
            if (!lower.Contains("enemycolor") && !lower.Contains("enemyvalue")) continue;

            // Parse dot-prefixed emitter names (e.g. "NS_Path_002.EnemyColor0")
            string? emitter = null;
            int dotIdx = name.IndexOf('.');
            if (dotIdx > 0)
                emitter = name.Substring(0, dotIdx);

            results.Add(new Dictionary<string, object?>
            {
                ["fname"] = name,
                ["emitter"] = emitter,
            });
        }

        return results;
    }

    /// <summary>
    /// Heuristic classification of a color curve based on its sample values.
    /// </summary>
    public static Dictionary<string, object> ClassifyColorCurve(List<Dictionary<string, object>> samples)
    {
        if (samples.Count == 0)
            return new Dictionary<string, object> { ["type"] = "unknown", ["confidence"] = 0.0, ["reason"] = "no samples", ["suggestEdit"] = false };

        double maxVal = 0, minVal = double.MaxValue;
        double sumR = 0, sumG = 0, sumB = 0, sumA = 0;
        bool allZero = true, allAlphaOne = true;
        int nonZeroCount = 0;

        foreach (var s in samples)
        {
            float r = Convert.ToSingle(s.GetValueOrDefault("r", 0f));
            float g = Convert.ToSingle(s.GetValueOrDefault("g", 0f));
            float b = Convert.ToSingle(s.GetValueOrDefault("b", 0f));
            float a = Convert.ToSingle(s.GetValueOrDefault("a", 1f));

            sumR += r; sumG += g; sumB += b; sumA += a;
            double brightness = Math.Max(r, Math.Max(g, b));
            if (brightness > maxVal) maxVal = brightness;
            if (brightness < minVal) minVal = brightness;
            if (r != 0 || g != 0 || b != 0) { allZero = false; nonZeroCount++; }
            if (Math.Abs(a - 1f) > 0.01f) allAlphaOne = false;
        }

        int n = samples.Count;
        double avgR = sumR / n, avgG = sumG / n, avgB = sumB / n, avgA = sumA / n;

        bool isGrayscale = Math.Abs(avgR - avgG) < 0.02 && Math.Abs(avgG - avgB) < 0.02;
        bool isHdr = maxVal > 1.05;
        bool hasAlphaVariation = !allAlphaOne;
        bool isConstant = Math.Abs(maxVal - minVal) < 0.01;

        // Classify
        string type;
        double confidence;
        string reason;
        bool suggestEdit;

        if (allZero)
        {
            type = "opacity"; confidence = 0.9; reason = "all values zero"; suggestEdit = false;
        }
        else if (isGrayscale && !isHdr)
        {
            type = "opacity"; confidence = 0.85; reason = "grayscale non-HDR"; suggestEdit = false;
        }
        else if (isHdr && !isGrayscale)
        {
            type = "emission"; confidence = 0.8; reason = "HDR color values"; suggestEdit = true;
        }
        else
        {
            type = "color"; confidence = 0.7; reason = "standard color"; suggestEdit = true;
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
            ["maxValue"] = maxVal,
            ["minValue"] = minVal,
        };
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
