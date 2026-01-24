using System;
using System.Collections.Generic;
using System.IO;
using UAssetAPI.UnrealTypes;

namespace UAssetAPI.ExportTypes
{
    /// <summary>
    /// Export for SkeletalMesh assets with comprehensive extra data parsing.
    /// Parses FStripDataFlags, FBoxSphereBounds, FSkeletalMaterial[], FReferenceSkeleton, and LOD data.
    /// Handles FGameplayTagContainer padding for Marvel Rivals compatibility.
    /// </summary>
    public class SkeletalMeshExport : NormalExport
    {
        #region Parsed Extra Data Fields

        /// <summary>
        /// Strip data flags indicating what data was stripped during cooking.
        /// </summary>
        public FStripDataFlags StripFlags;

        /// <summary>
        /// Imported bounding box and sphere for the mesh.
        /// </summary>
        public FBoxSphereBounds ImportedBounds;

        /// <summary>
        /// Parsed materials from the mesh. If null, materials weren't found/parsed.
        /// </summary>
        public List<FSkeletalMaterial> Materials;

        /// <summary>
        /// Reference skeleton containing bone hierarchy and reference pose.
        /// </summary>
        public FReferenceSkeleton ReferenceSkeleton;

        /// <summary>
        /// Whether the mesh is cooked (has render data).
        /// </summary>
        public bool bCooked;

        /// <summary>
        /// Number of LOD models in the mesh.
        /// </summary>
        public int LODCount;

        /// <summary>
        /// Remaining unparsed data after the known structures.
        /// This contains LOD render data which is complex and version-dependent.
        /// </summary>
        public byte[] RemainingExtraData;

        #endregion

        #region Configuration

        /// <summary>
        /// Whether to include FGameplayTagContainer when writing materials.
        /// Set to true for Marvel Rivals compatibility.
        /// </summary>
        public bool IncludeGameplayTags = true;

        /// <summary>
        /// Whether extra data was successfully parsed.
        /// </summary>
        public bool ExtraDataParsed { get; private set; } = false;

        #endregion

        #region Internal State

        private int _materialsOffset = -1;
        private int _originalMaterialsByteLength = 0;
        private int _parsedDataEndOffset = 0;

        #endregion

        #region Constructors

        public SkeletalMeshExport(Export super) : base(super)
        {
        }

        public SkeletalMeshExport(UAsset asset, byte[] extras) : base(asset, extras)
        {
        }

        public SkeletalMeshExport()
        {
        }

        #endregion

        #region Read/Write

        public override void Read(AssetBinaryReader reader, int nextStarting)
        {
            base.Read(reader, nextStarting);
            
            // After base.Read(), Extras contains the remaining binary data
            if (Extras != null && Extras.Length > 0)
            {
                TryParseExtraData();
            }
        }

        /// <summary>
        /// Parse the extra data section which contains mesh-specific binary data.
        /// Structure (per CUE4Parse USkeletalMesh.cs):
        /// 1. FStripDataFlags (2 bytes)
        /// 2. FBoxSphereBounds (28 or 56 bytes depending on LWC)
        /// 3. FSkeletalMaterial[] array
        /// 4. FReferenceSkeleton
        /// 5. LOD data (complex, version-dependent)
        /// </summary>
        private void TryParseExtraData()
        {
            try
            {
                using var ms = new MemoryStream(Extras);
                using var extraReader = new AssetBinaryReader(ms, Asset);

                // 1. Read FStripDataFlags
                StripFlags = new FStripDataFlags(extraReader);

                // 2. Read FBoxSphereBounds
                ImportedBounds = new FBoxSphereBounds(extraReader);

                // 3. Read FSkeletalMaterial array
                _materialsOffset = (int)extraReader.BaseStream.Position;
                int materialCount = extraReader.ReadInt32();
                
                if (materialCount > 0 && materialCount <= 100)
                {
                    Materials = new List<FSkeletalMaterial>(materialCount);
                    for (int i = 0; i < materialCount; i++)
                    {
                        var mat = new FSkeletalMaterial();
                        mat.Read(extraReader, includeGameplayTags: false); // Legacy format without tags
                        Materials.Add(mat);
                    }
                    _originalMaterialsByteLength = (int)extraReader.BaseStream.Position - _materialsOffset;
                }
                else
                {
                    // Invalid material count, reset and try pattern matching
                    extraReader.BaseStream.Position = _materialsOffset;
                    TryParseMaterialsByPattern();
                    if (Materials != null && Materials.Count > 0)
                    {
                        // Skip past materials we found
                        extraReader.BaseStream.Position = _materialsOffset + _originalMaterialsByteLength;
                    }
                }

                // 4. Read FReferenceSkeleton
                int skeletonStartPos = (int)extraReader.BaseStream.Position;
                try
                {
                    ReferenceSkeleton = new FReferenceSkeleton(extraReader);
                }
                catch
                {
                    // Failed to parse skeleton, reset position
                    extraReader.BaseStream.Position = skeletonStartPos;
                    ReferenceSkeleton = null;
                }

                // 5. Check for cooked LOD data
                if (extraReader.BaseStream.Position < extraReader.BaseStream.Length - 4)
                {
                    // Try to read bCooked flag and LOD count
                    long posBeforeLOD = extraReader.BaseStream.Position;
                    try
                    {
                        bCooked = extraReader.ReadInt32() != 0;
                        if (bCooked && extraReader.BaseStream.Position < extraReader.BaseStream.Length - 4)
                        {
                            LODCount = extraReader.ReadInt32();
                            if (LODCount < 0 || LODCount > 10)
                            {
                                // Invalid LOD count, probably misread
                                LODCount = 0;
                                extraReader.BaseStream.Position = posBeforeLOD;
                            }
                        }
                    }
                    catch
                    {
                        extraReader.BaseStream.Position = posBeforeLOD;
                    }
                }

                // Store remaining unparsed data
                _parsedDataEndOffset = (int)extraReader.BaseStream.Position;
                int remainingLength = Extras.Length - _parsedDataEndOffset;
                if (remainingLength > 0)
                {
                    RemainingExtraData = new byte[remainingLength];
                    Array.Copy(Extras, _parsedDataEndOffset, RemainingExtraData, 0, remainingLength);
                }
                else
                {
                    RemainingExtraData = Array.Empty<byte>();
                }

                ExtraDataParsed = true;
            }
            catch (Exception)
            {
                // If structured parsing fails, fall back to pattern matching for materials only
                ExtraDataParsed = false;
                TryParseMaterialsByPattern();
            }
        }

        /// <summary>
        /// Fallback method to find and parse materials by pattern matching.
        /// Used when structured parsing fails.
        /// </summary>
        private void TryParseMaterialsByPattern()
        {
            const int MAX_MATERIAL_COUNT = 50;
            const int MATERIAL_STRUCT_SIZE = 40;
            
            for (int i = 4; i < Extras.Length - (MATERIAL_STRUCT_SIZE * 2); i++)
            {
                int potentialCount = BitConverter.ToInt32(Extras, i);
                if (potentialCount < 1 || potentialCount > MAX_MATERIAL_COUNT)
                    continue;
                
                int firstPkgIdx = BitConverter.ToInt32(Extras, i + 4);
                if (firstPkgIdx >= 0 || firstPkgIdx < -1000)
                    continue;
                
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
                
                _materialsOffset = i;
                int materialCount = potentialCount;
                _originalMaterialsByteLength = 4 + (materialCount * MATERIAL_STRUCT_SIZE);
                
                Materials = new List<FSkeletalMaterial>();
                using var ms = new MemoryStream(Extras, i + 4, Extras.Length - i - 4);
                using var matReader = new AssetBinaryReader(ms, Asset);
                
                for (int m = 0; m < materialCount; m++)
                {
                    var mat = new FSkeletalMaterial();
                    mat.Read(matReader, includeGameplayTags: false);
                    Materials.Add(mat);
                }
                
                break;
            }
        }

        public override void Write(AssetBinaryWriter writer)
        {
            base.Write(writer);
            
            // If we successfully parsed extra data, reconstruct it
            if (ExtraDataParsed && Materials != null && Materials.Count > 0)
            {
                ReconstructExtrasFromParsedData();
            }
            else if (Materials != null && Materials.Count > 0 && _materialsOffset >= 0)
            {
                // Fallback: just patch materials in place
                ReconstructExtrasWithMaterialsOnly();
            }
        }

        /// <summary>
        /// Reconstruct Extras from fully parsed data.
        /// </summary>
        private void ReconstructExtrasFromParsedData()
        {
            using var ms = new MemoryStream();
            using var extraWriter = new AssetBinaryWriter(ms, Asset);

            // 1. Write FStripDataFlags
            if (StripFlags != null)
            {
                StripFlags.Write(extraWriter);
            }
            else
            {
                new FStripDataFlags().Write(extraWriter);
            }

            // 2. Write FBoxSphereBounds
            if (ImportedBounds != null)
            {
                ImportedBounds.Write(extraWriter);
            }
            else
            {
                new FBoxSphereBounds().Write(extraWriter);
            }

            // 3. Write FSkeletalMaterial array with FGameplayTagContainer
            extraWriter.Write(Materials.Count);
            foreach (var mat in Materials)
            {
                mat.Write(extraWriter, IncludeGameplayTags);
            }

            // 4. Write FReferenceSkeleton
            if (ReferenceSkeleton != null)
            {
                ReferenceSkeleton.Write(extraWriter);
            }

            // 5. Write bCooked and LOD count if we have them
            if (bCooked || LODCount > 0)
            {
                extraWriter.Write(bCooked ? 1 : 0);
                if (bCooked)
                {
                    extraWriter.Write(LODCount);
                }
            }

            // 6. Write remaining unparsed data
            if (RemainingExtraData != null && RemainingExtraData.Length > 0)
            {
                extraWriter.Write(RemainingExtraData);
            }

            Extras = ms.ToArray();
        }

        /// <summary>
        /// Reconstruct Extras with only materials patched (fallback method).
        /// </summary>
        private void ReconstructExtrasWithMaterialsOnly()
        {
            int newMaterialSize = IncludeGameplayTags ? 44 : 40;
            int newMaterialsByteLength = 4 + (Materials.Count * newMaterialSize);
            int sizeDiff = newMaterialsByteLength - _originalMaterialsByteLength;
            
            byte[] newExtras = new byte[Extras.Length + sizeDiff];
            
            Array.Copy(Extras, 0, newExtras, 0, _materialsOffset);
            
            using var ms = new MemoryStream(newExtras, _materialsOffset, newMaterialsByteLength);
            using var matWriter = new AssetBinaryWriter(ms, Asset);
            
            matWriter.Write(Materials.Count);
            foreach (var mat in Materials)
            {
                mat.Write(matWriter, IncludeGameplayTags);
            }
            
            int afterMaterialsOffset = _materialsOffset + _originalMaterialsByteLength;
            int afterMaterialsNewOffset = _materialsOffset + newMaterialsByteLength;
            int remainingBytes = Extras.Length - afterMaterialsOffset;
            if (remainingBytes > 0)
            {
                Array.Copy(Extras, afterMaterialsOffset, newExtras, afterMaterialsNewOffset, remainingBytes);
            }
            
            Extras = newExtras;
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Get the number of bones in the skeleton.
        /// </summary>
        public int GetBoneCount()
        {
            return ReferenceSkeleton?.BoneCount ?? 0;
        }

        /// <summary>
        /// Get bone info by index.
        /// </summary>
        public FMeshBoneInfo GetBone(int index)
        {
            return ReferenceSkeleton?.GetBoneInfo(index);
        }

        /// <summary>
        /// Get bone transform by index.
        /// </summary>
        public FTransform? GetBoneTransform(int index)
        {
            return ReferenceSkeleton?.GetBonePose(index);
        }

        /// <summary>
        /// Find bone index by name.
        /// </summary>
        public int FindBoneIndex(FName boneName)
        {
            return ReferenceSkeleton?.FindBoneIndex(boneName) ?? -1;
        }

        /// <summary>
        /// Get material by index.
        /// </summary>
        public FSkeletalMaterial GetMaterial(int index)
        {
            if (Materials != null && index >= 0 && index < Materials.Count)
            {
                return Materials[index];
            }
            return null;
        }

        /// <summary>
        /// Get the number of materials.
        /// </summary>
        public int GetMaterialCount()
        {
            return Materials?.Count ?? 0;
        }

        #endregion
    }
}
