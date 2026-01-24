using System;
using System.Collections.Generic;
using System.IO;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.Unversioned;
using UAssetAPI.UnrealTypes;

namespace UAssetTool;

/// <summary>
/// Systematic color modification for Niagara and other assets using UAssetAPI structured parsing.
/// This replaces fragile binary patching with proper property-level modifications.
/// </summary>
public static class ColorModifier
{
    /// <summary>
    /// Modify all color values in an asset using UAssetAPI's structured parsing
    /// </summary>
    public static int ModifyColors(string assetPath, string usmapPath, float r, float g, float b, float a = 1.0f)
    {
        try
        {
            // Load mappings for proper property parsing
            Usmap? mappings = null;
            if (!string.IsNullOrEmpty(usmapPath) && File.Exists(usmapPath))
            {
                mappings = new Usmap(usmapPath);
            }

            // Load asset with UAssetAPI
            var asset = new UAsset(assetPath, EngineVersion.VER_UE5_3, mappings);
            
            int modifiedCount = 0;
            FLinearColor targetColor = new FLinearColor(r, g, b, a);
            
            // Process all exports
            foreach (var export in asset.Exports)
            {
                // Use structured NiagaraDataInterfaceColorCurveExport if available
                if (export is NiagaraDataInterfaceColorCurveExport colorCurveExport)
                {
                    if (colorCurveExport.ShaderLUT != null)
                    {
                        colorCurveExport.SetAllColors(r, g, b, a);
                        modifiedCount += colorCurveExport.ColorCount;
                    }
                }
                else if (export is NormalExport normalExport && normalExport.Data != null)
                {
                    string className = export.GetExportClassType()?.Value?.Value ?? "";
                    
                    // Fallback: NiagaraDataInterfaceColorCurve not parsed as structured export
                    if (className.Contains("ColorCurve"))
                    {
                        modifiedCount += ModifyShaderLUT(normalExport.Data, targetColor);
                    }
                    
                    // Generic recursive search for LinearColor properties in any export
                    modifiedCount += ModifyColorsRecursive(normalExport.Data, targetColor);
                }
            }
            
            if (modifiedCount > 0)
            {
                asset.Write(assetPath);
            }
            
            Console.WriteLine($"Modified {modifiedCount} color values in {Path.GetFileName(assetPath)}");
            return modifiedCount;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error modifying {assetPath}: {ex.Message}");
            return -1;
        }
    }
    
    /// <summary>
    /// Modify ShaderLUT array in NiagaraDataInterfaceColorCurve.
    /// ShaderLUT is stored as a flat float array: [R0, G0, B0, A0, R1, G1, B1, A1, ...]
    /// Each group of 4 floats represents one LinearColor.
    /// </summary>
    private static int ModifyShaderLUT(List<PropertyData> properties, FLinearColor targetColor)
    {
        int count = 0;
        
        foreach (var prop in properties)
        {
            if (prop.Name?.Value?.Value != "ShaderLUT") continue;
            
            if (prop is ArrayPropertyData lutArray)
            {
                // ShaderLUT is a flat float array - colors are stored as sequential RGBA floats
                // Process in groups of 4: [R, G, B, A]
                for (int i = 0; i + 3 < lutArray.Value.Length; i += 4)
                {
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
        }
        
        return count;
    }
    
    /// <summary>
    /// Modify a LinearColor struct's R, G, B, A float properties
    /// </summary>
    private static int ModifyLinearColorStruct(List<PropertyData> structData, FLinearColor targetColor)
    {
        int count = 0;
        if (structData == null) return 0;
        
        foreach (var field in structData)
        {
            if (field is FloatPropertyData floatProp)
            {
                string fieldName = field.Name?.Value?.Value ?? "";
                float? newVal = fieldName switch
                {
                    "R" => targetColor.R,
                    "G" => targetColor.G,
                    "B" => targetColor.B,
                    "A" => targetColor.A,
                    _ => null
                };
                
                if (newVal.HasValue && floatProp.Value != newVal.Value)
                {
                    floatProp.Value = newVal.Value;
                    count++;
                }
            }
        }
        
        return count;
    }
    
    /// <summary>
    /// Recursively search for and modify LinearColor properties in any property list
    /// </summary>
    private static int ModifyColorsRecursive(List<PropertyData> properties, FLinearColor targetColor)
    {
        int count = 0;
        if (properties == null) return 0;
        
        foreach (var prop in properties)
        {
            // Direct LinearColorPropertyData
            if (prop is LinearColorPropertyData linearColor)
            {
                linearColor.Value = (FLinearColor)targetColor.Clone();
                count++;
            }
            // StructPropertyData that is a LinearColor
            else if (prop is StructPropertyData structProp)
            {
                string structType = structProp.StructType?.Value?.Value ?? "";
                
                if (structType == "LinearColor" || structType == "Color")
                {
                    count += ModifyLinearColorStruct(structProp.Value, targetColor);
                }
                else if (structProp.Value != null)
                {
                    // Recurse into other structs
                    count += ModifyColorsRecursive(structProp.Value, targetColor);
                }
            }
            // ArrayPropertyData - check elements
            else if (prop is ArrayPropertyData arrayProp)
            {
                foreach (var elem in arrayProp.Value)
                {
                    if (elem is LinearColorPropertyData linearElem)
                    {
                        linearElem.Value = (FLinearColor)targetColor.Clone();
                        count++;
                    }
                    else if (elem is StructPropertyData structElem)
                    {
                        string structType = structElem.StructType?.Value?.Value ?? "";
                        if (structType == "LinearColor" || structType == "Color")
                        {
                            count += ModifyLinearColorStruct(structElem.Value, targetColor);
                        }
                        else if (structElem.Value != null)
                        {
                            count += ModifyColorsRecursive(structElem.Value, targetColor);
                        }
                    }
                }
            }
            // MapPropertyData - check values
            else if (prop is MapPropertyData mapProp)
            {
                foreach (var kvp in mapProp.Value)
                {
                    if (kvp.Value is StructPropertyData valStruct && valStruct.Value != null)
                    {
                        count += ModifyColorsRecursive(valStruct.Value, targetColor);
                    }
                }
            }
        }
        
        return count;
    }
}
