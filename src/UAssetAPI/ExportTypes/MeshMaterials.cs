using System;
using System.Collections.Generic;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace UAssetAPI.ExportTypes
{
    /// <summary>
    /// UV channel density info for a mesh material.
    /// </summary>
    public class FMeshUVChannelInfo
    {
        public bool bInitialized;
        public bool bOverrideDensities;
        public float[] LocalUVDensities; // 4 floats

        public FMeshUVChannelInfo()
        {
            LocalUVDensities = new float[4];
        }

        public void Read(AssetBinaryReader reader)
        {
            // Per TECHNICAL_ANALYSIS.md: 1 byte + 1 byte + 2 bytes padding + 16 bytes floats = 20 bytes
            bInitialized = reader.ReadByte() != 0;
            bOverrideDensities = reader.ReadByte() != 0;
            reader.ReadBytes(2); // 2 bytes padding
            LocalUVDensities = new float[4];
            for (int i = 0; i < 4; i++)
            {
                LocalUVDensities[i] = reader.ReadSingle();
            }
        }

        public void Write(AssetBinaryWriter writer)
        {
            // Per TECHNICAL_ANALYSIS.md: 1 byte + 1 byte + 2 bytes padding + 16 bytes floats = 20 bytes
            writer.Write((byte)(bInitialized ? 1 : 0));
            writer.Write((byte)(bOverrideDensities ? 1 : 0));
            writer.Write((short)0); // 2 bytes padding
            for (int i = 0; i < 4; i++)
            {
                writer.Write(LocalUVDensities[i]);
            }
        }

        public static int SerializedSize => 1 + 1 + 2 + (4 * 4); // 20 bytes
    }

    /// <summary>
    /// FSkeletalMaterial - Material slot for skeletal meshes.
    /// Marvel Rivals requires FGameplayTagContainer after each material.
    /// </summary>
    public class FSkeletalMaterial
    {
        public FPackageIndex MaterialInterface;
        public FName MaterialSlotName;
        public FName ImportedMaterialSlotName;
        public FMeshUVChannelInfo UVChannelData;
        
        /// <summary>
        /// FGameplayTagContainer - Marvel Rivals requires this field.
        /// Empty container = just int32 count of 0.
        /// </summary>
        public FName[] GameplayTags;

        public FSkeletalMaterial()
        {
            UVChannelData = new FMeshUVChannelInfo();
            GameplayTags = Array.Empty<FName>();
        }

        public void Read(AssetBinaryReader reader, bool includeGameplayTags = true)
        {
            MaterialInterface = new FPackageIndex(reader.ReadInt32());
            MaterialSlotName = reader.ReadFName();
            ImportedMaterialSlotName = reader.ReadFName();
            UVChannelData = new FMeshUVChannelInfo();
            UVChannelData.Read(reader);
            
            if (includeGameplayTags)
            {
                // Read FGameplayTagContainer
                int tagCount = reader.ReadInt32();
                GameplayTags = new FName[tagCount];
                for (int i = 0; i < tagCount; i++)
                {
                    GameplayTags[i] = reader.ReadFName();
                }
            }
        }

        public void Write(AssetBinaryWriter writer, bool includeGameplayTags = true)
        {
            writer.Write(MaterialInterface.Index);
            writer.Write(MaterialSlotName);
            writer.Write(ImportedMaterialSlotName);
            UVChannelData.Write(writer);
            
            if (includeGameplayTags)
            {
                // Write FGameplayTagContainer
                writer.Write(GameplayTags?.Length ?? 0);
                if (GameplayTags != null)
                {
                    foreach (var tag in GameplayTags)
                    {
                        writer.Write(tag);
                    }
                }
            }
        }

        /// <summary>
        /// Size without FGameplayTagContainer: 4 + 8 + 8 + 20 = 40 bytes
        /// With empty FGameplayTagContainer: 40 + 4 = 44 bytes
        /// </summary>
        public static int LegacySerializedSize => 4 + 8 + 8 + 20; // 40 bytes without tags
    }

    /// <summary>
    /// FStaticMaterial - Material slot for static meshes.
    /// </summary>
    public class FStaticMaterial
    {
        public FPackageIndex MaterialInterface;
        public FName MaterialSlotName;
        public FPackageIndex OverlayMaterialInterface;
        public FMeshUVChannelInfo UVChannelData;

        public FStaticMaterial()
        {
            UVChannelData = new FMeshUVChannelInfo();
        }

        public void Read(AssetBinaryReader reader)
        {
            MaterialInterface = new FPackageIndex(reader.ReadInt32());
            MaterialSlotName = reader.ReadFName();
            OverlayMaterialInterface = new FPackageIndex(reader.ReadInt32());
            UVChannelData = new FMeshUVChannelInfo();
            UVChannelData.Read(reader);
        }

        public void Write(AssetBinaryWriter writer)
        {
            writer.Write(MaterialInterface.Index);
            writer.Write(MaterialSlotName);
            writer.Write(OverlayMaterialInterface.Index);
            UVChannelData.Write(writer);
        }

        /// <summary>
        /// Size: 4 + 8 + 4 + 20 = 36 bytes (per TECHNICAL_ANALYSIS.md)
        /// </summary>
        public static int SerializedSize => 4 + 8 + 4 + 20; // 36 bytes
    }
}
