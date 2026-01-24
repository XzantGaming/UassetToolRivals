using System;
using System.Collections.Generic;
using System.IO;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace UAssetAPI.ExportTypes
{
    /// <summary>
    /// Export type for NiagaraDataInterfaceColorCurve assets.
    /// Provides structured access to ShaderLUT color data for particle effects.
    /// 
    /// The ShaderLUT contains pre-baked color values sampled from FRichCurve data.
    /// Colors are stored as a flat float array for GPU efficiency.
    /// </summary>
    public class NiagaraDataInterfaceColorCurveExport : NormalExport
    {
        /// <summary>
        /// The parsed ShaderLUT containing color values.
        /// Null if ShaderLUT property wasn't found.
        /// </summary>
        public FShaderLUT ShaderLUT { get; set; }

        /// <summary>
        /// Index of the ShaderLUT property in Data list (for reconstruction).
        /// </summary>
        private int _shaderLUTPropertyIndex = -1;

        public NiagaraDataInterfaceColorCurveExport(Export super) : base(super)
        {
        }

        public NiagaraDataInterfaceColorCurveExport(UAsset asset, byte[] extras) : base(asset, extras)
        {
        }

        public NiagaraDataInterfaceColorCurveExport()
        {
        }

        public override void Read(AssetBinaryReader reader, int nextStarting)
        {
            base.Read(reader, nextStarting);
            
            // After base.Read(), parse ShaderLUT from Data properties
            ParseShaderLUT();
        }

        /// <summary>
        /// Parse ShaderLUT from the Data properties into structured form.
        /// </summary>
        private void ParseShaderLUT()
        {
            if (Data == null) return;

            for (int i = 0; i < Data.Count; i++)
            {
                var prop = Data[i];
                if (prop.Name?.Value?.Value != "ShaderLUT") continue;
                
                if (prop is ArrayPropertyData arrayProp)
                {
                    _shaderLUTPropertyIndex = i;
                    ShaderLUT = new FShaderLUT();

                    // Parse float array into structured colors
                    for (int j = 0; j + 3 < arrayProp.Value.Length; j += 4)
                    {
                        if (arrayProp.Value[j] is FloatPropertyData rProp &&
                            arrayProp.Value[j + 1] is FloatPropertyData gProp &&
                            arrayProp.Value[j + 2] is FloatPropertyData bProp &&
                            arrayProp.Value[j + 3] is FloatPropertyData aProp)
                        {
                            ShaderLUT.Colors.Add(new FShaderLUTColor(
                                rProp.Value, gProp.Value, bProp.Value, aProp.Value));
                        }
                    }
                    
                    break;
                }
            }
        }

        /// <summary>
        /// Sync ShaderLUT back to Data properties before writing.
        /// </summary>
        private void SyncShaderLUTToProperties()
        {
            if (ShaderLUT == null || _shaderLUTPropertyIndex < 0 || Data == null)
                return;

            if (Data[_shaderLUTPropertyIndex] is not ArrayPropertyData arrayProp)
                return;

            // Update float values from structured ShaderLUT
            int floatIndex = 0;
            foreach (var color in ShaderLUT.Colors)
            {
                if (floatIndex + 3 >= arrayProp.Value.Length) break;

                if (arrayProp.Value[floatIndex] is FloatPropertyData rProp)
                    rProp.Value = color.R;
                if (arrayProp.Value[floatIndex + 1] is FloatPropertyData gProp)
                    gProp.Value = color.G;
                if (arrayProp.Value[floatIndex + 2] is FloatPropertyData bProp)
                    bProp.Value = color.B;
                if (arrayProp.Value[floatIndex + 3] is FloatPropertyData aProp)
                    aProp.Value = color.A;

                floatIndex += 4;
            }
        }

        public override void Write(AssetBinaryWriter writer)
        {
            // Sync structured ShaderLUT back to property data
            SyncShaderLUTToProperties();
            
            base.Write(writer);
        }

        /// <summary>
        /// Set all colors in the ShaderLUT to a specific value.
        /// </summary>
        public void SetAllColors(float r, float g, float b, float a)
        {
            if (ShaderLUT != null)
            {
                ShaderLUT.SetAllColors(r, g, b, a);
            }
        }

        /// <summary>
        /// Set a specific color in the ShaderLUT by index.
        /// </summary>
        public void SetColor(int index, float r, float g, float b, float a)
        {
            if (ShaderLUT != null)
            {
                ShaderLUT.SetColor(index, r, g, b, a);
            }
        }

        /// <summary>
        /// Get the number of colors in the ShaderLUT.
        /// </summary>
        public int ColorCount => ShaderLUT?.Colors.Count ?? 0;

        /// <summary>
        /// Get a color by index.
        /// </summary>
        public FShaderLUTColor? GetColor(int index)
        {
            if (ShaderLUT != null && index >= 0 && index < ShaderLUT.Colors.Count)
            {
                return ShaderLUT.Colors[index];
            }
            return null;
        }
    }
}
