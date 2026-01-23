using System;
using System.Collections.Generic;
using System.IO;
using UAssetAPI.UnrealTypes;

namespace UAssetAPI.ExportTypes
{
    /// <summary>
    /// Export for SkeletalMesh assets with proper FSkeletalMaterial parsing.
    /// Handles FGameplayTagContainer padding for Marvel Rivals compatibility.
    /// </summary>
    public class SkeletalMeshExport : NormalExport
    {
        /// <summary>
        /// Parsed materials from the mesh. If null, materials weren't found/parsed.
        /// </summary>
        public List<FSkeletalMaterial> Materials;
        
        /// <summary>
        /// Whether to include FGameplayTagContainer when writing materials.
        /// Set to true for Marvel Rivals compatibility.
        /// </summary>
        public bool IncludeGameplayTags = true;
        
        /// <summary>
        /// Offset in Extras where materials array starts (for reconstruction).
        /// </summary>
        private int _materialsOffset = -1;
        
        /// <summary>
        /// Original materials byte length before parsing.
        /// </summary>
        private int _originalMaterialsByteLength = 0;

        public SkeletalMeshExport(Export super) : base(super)
        {
        }

        public SkeletalMeshExport(UAsset asset, byte[] extras) : base(asset, extras)
        {
        }

        public SkeletalMeshExport()
        {
        }

        public override void Read(AssetBinaryReader reader, int nextStarting)
        {
            base.Read(reader, nextStarting);
            
            // After base.Read(), Extras contains the remaining binary data
            // Try to parse materials from Extras
            if (Extras != null && Extras.Length > 0)
            {
                TryParseMaterials();
            }
        }

        /// <summary>
        /// Try to find and parse the FSkeletalMaterial array from Extras.
        /// </summary>
        private void TryParseMaterials()
        {
            const int MAX_MATERIAL_COUNT = 50;
            const int MATERIAL_STRUCT_SIZE = 40; // Without FGameplayTagContainer (per TECHNICAL_ANALYSIS.md)
            
            // Search for material array pattern: count followed by FPackageIndex imports
            for (int i = 4; i < Extras.Length - (MATERIAL_STRUCT_SIZE * 2); i++)
            {
                int potentialCount = BitConverter.ToInt32(Extras, i);
                if (potentialCount < 1 || potentialCount > MAX_MATERIAL_COUNT)
                    continue;
                
                // Check if next bytes look like an FPackageIndex (negative value for import)
                int firstPkgIdx = BitConverter.ToInt32(Extras, i + 4);
                if (firstPkgIdx >= 0 || firstPkgIdx < -1000)
                    continue;
                
                // Verify by checking subsequent materials
                bool validPattern = true;
                for (int m = 1; m < Math.Min(potentialCount, 5); m++)
                {
                    int matOffset = i + 4 + (m * MATERIAL_STRUCT_SIZE);
                    if (matOffset + 4 > Extras.Length)
                    {
                        validPattern = false;
                        break;
                    }
                    
                    int pkgIdx = BitConverter.ToInt32(Extras, matOffset);
                    if (pkgIdx >= 0 || pkgIdx < -1000)
                    {
                        validPattern = false;
                        break;
                    }
                }
                
                if (!validPattern)
                    continue;
                
                // Found materials - parse them
                _materialsOffset = i;
                int materialCount = potentialCount;
                _originalMaterialsByteLength = 4 + (materialCount * MATERIAL_STRUCT_SIZE);
                
                Materials = new List<FSkeletalMaterial>();
                using var ms = new MemoryStream(Extras, i + 4, Extras.Length - i - 4);
                using var matReader = new AssetBinaryReader(ms, Asset);
                
                for (int m = 0; m < materialCount; m++)
                {
                    var mat = new FSkeletalMaterial();
                    mat.Read(matReader, includeGameplayTags: false); // Original doesn't have tags
                    Materials.Add(mat);
                }
                
                break;
            }
        }

        public override void Write(AssetBinaryWriter writer)
        {
            base.Write(writer);
            
            // If we parsed materials, reconstruct Extras with proper serialization
            if (Materials != null && Materials.Count > 0 && _materialsOffset >= 0)
            {
                // Calculate new materials size with FGameplayTagContainer
                int newMaterialSize = IncludeGameplayTags ? 44 : 40; // 40 + 4 for empty container
                int newMaterialsByteLength = 4 + (Materials.Count * newMaterialSize);
                int sizeDiff = newMaterialsByteLength - _originalMaterialsByteLength;
                
                // Create new Extras with updated size
                byte[] newExtras = new byte[Extras.Length + sizeDiff];
                
                // Copy data before materials
                Array.Copy(Extras, 0, newExtras, 0, _materialsOffset);
                
                // Write materials with proper serialization
                using var ms = new MemoryStream(newExtras, _materialsOffset, newMaterialsByteLength);
                using var matWriter = new AssetBinaryWriter(ms, Asset);
                
                matWriter.Write(Materials.Count);
                foreach (var mat in Materials)
                {
                    mat.Write(matWriter, IncludeGameplayTags);
                }
                
                // Copy data after original materials
                int afterMaterialsOffset = _materialsOffset + _originalMaterialsByteLength;
                int afterMaterialsNewOffset = _materialsOffset + newMaterialsByteLength;
                int remainingBytes = Extras.Length - afterMaterialsOffset;
                if (remainingBytes > 0)
                {
                    Array.Copy(Extras, afterMaterialsOffset, newExtras, afterMaterialsNewOffset, remainingBytes);
                }
                
                Extras = newExtras;
            }
        }
    }
}
