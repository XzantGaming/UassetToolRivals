using System;
using System.Collections.Generic;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.PropertyTypes;

namespace UAssetTool.ZenPackage;

/// <summary>
/// Reads MaterialTagAssetUserData from a SkeletalMesh package and extracts
/// per-slot FGameplayTagContainer data. Used to inject tags into FSkeletalMaterial during
/// Zen conversion when the MaterialTagPlugin was used in the UE Editor.
/// 
/// Safety: Every material slot gets an empty FGameplayTagContainer by default.
/// Only slots explicitly listed in the MaterialTagAssetUserData with non-empty tags
/// will have their containers populated.
/// </summary>
public static class MaterialTagReader
{
    /// <summary>
    /// Result of reading material tags from a package.
    /// Maps material slot name (string) to a list of tag name strings.
    /// </summary>
    public class MaterialTagResult
    {
        /// <summary>
        /// Per-slot tag assignments. Key = MaterialSlotName, Value = list of tag strings.
        /// Only slots with actual tags are present here.
        /// </summary>
        public Dictionary<string, List<string>> SlotTags { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Whether a MaterialTagAssetUserData export was found in the package.
        /// </summary>
        public bool FoundUserData { get; set; }

        /// <summary>
        /// Total number of tag entries found across all slots.
        /// </summary>
        public int TotalTagCount { get; set; }

        /// <summary>
        /// Diagnostic messages for logging.
        /// </summary>
        public List<string> Diagnostics { get; } = new();
    }

    /// <summary>
    /// Scan a UAsset for a MaterialTagAssetUserData export and extract per-slot tag assignments.
    /// Returns a MaterialTagResult with the slot-to-tags mapping.
    /// </summary>
    public static MaterialTagResult ReadFromAsset(UAsset asset)
    {
        var result = new MaterialTagResult();

        if (asset == null)
        {
            result.Diagnostics.Add("Asset is null");
            return result;
        }

        // Step 1: Find the MaterialTagAssetUserData export by class name
        NormalExport? tagDataExport = null;
        for (int i = 0; i < asset.Exports.Count; i++)
        {
            var export = asset.Exports[i];
            string? className = GetExportClassName(export);
            if (className != null && className.Contains("MaterialTagAssetUserData", StringComparison.OrdinalIgnoreCase))
            {
                tagDataExport = export as NormalExport;
                if (tagDataExport != null)
                {
                    result.FoundUserData = true;
                    result.Diagnostics.Add($"Found MaterialTagAssetUserData at export index {i} (class: {className})");
                    break;
                }
                else
                {
                    result.Diagnostics.Add($"Export index {i} has class {className} but is not a NormalExport (type: {export.GetType().Name})");
                }
            }
        }

        if (!result.FoundUserData || tagDataExport == null)
        {
            result.Diagnostics.Add("No MaterialTagAssetUserData export found in asset");
            return result;
        }

        // Step 2: Find the "MaterialSlotTags" array property
        if (tagDataExport.Data == null || tagDataExport.Data.Count == 0)
        {
            result.Diagnostics.Add("MaterialTagAssetUserData export has no properties");
            return result;
        }

        ArrayPropertyData? materialSlotTagsArray = null;
        foreach (var prop in tagDataExport.Data)
        {
            if (prop is ArrayPropertyData arrayProp &&
                prop.Name?.Value?.Value != null &&
                prop.Name.Value.Value.Equals("MaterialSlotTags", StringComparison.OrdinalIgnoreCase))
            {
                materialSlotTagsArray = arrayProp;
                break;
            }
        }

        if (materialSlotTagsArray == null)
        {
            result.Diagnostics.Add("MaterialSlotTags property not found in MaterialTagAssetUserData");
            return result;
        }

        if (materialSlotTagsArray.Value == null || materialSlotTagsArray.Value.Length == 0)
        {
            result.Diagnostics.Add("MaterialSlotTags array is empty");
            return result;
        }

        result.Diagnostics.Add($"MaterialSlotTags array has {materialSlotTagsArray.Value.Length} entries");

        // Step 3: Iterate each FMaterialSlotTagEntry struct
        foreach (var entry in materialSlotTagsArray.Value)
        {
            if (entry is not StructPropertyData entryStruct)
            {
                result.Diagnostics.Add($"Skipping non-struct entry: {entry?.GetType().Name}");
                continue;
            }

            if (entryStruct.Value == null)
                continue;

            // Extract MaterialSlotName
            string? slotName = null;
            StructPropertyData? gameplayTagsStruct = null;
            ArrayPropertyData? gameplayTagsArray = null;

            foreach (var field in entryStruct.Value)
            {
                string? fieldName = field?.Name?.Value?.Value;
                if (fieldName == null) continue;

                if (fieldName.Equals("MaterialSlotName", StringComparison.OrdinalIgnoreCase) && field is NamePropertyData nameProp)
                {
                    // Use ToString() to include FName Number suffix (e.g., MI_Equip with Number=13 -> MI_Equip_12)
                    slotName = nameProp.Value?.ToString();
                }
                else if (fieldName.Equals("GameplayTags", StringComparison.OrdinalIgnoreCase))
                {
                    // Old format: FGameplayTagContainer serialized as StructPropertyData
                    if (field is StructPropertyData structProp)
                        gameplayTagsStruct = structProp;
                    // New format: TArray<FGameplayTag> serialized as ArrayPropertyData
                    else if (field is ArrayPropertyData arrayProp)
                        gameplayTagsArray = arrayProp;
                }
            }

            if (string.IsNullOrEmpty(slotName))
            {
                result.Diagnostics.Add("Skipping entry with null/empty MaterialSlotName");
                continue;
            }

            // Extract tags from either format
            var tags = gameplayTagsArray != null
                ? ExtractTagsFromArray(gameplayTagsArray, result)
                : ExtractTagsFromContainer(gameplayTagsStruct, result);

            if (tags.Count > 0)
            {
                result.SlotTags[slotName] = tags;
                result.TotalTagCount += tags.Count;
                result.Diagnostics.Add($"  Slot '{slotName}': {tags.Count} tag(s) [{string.Join(", ", tags)}]");
            }
            else
            {
                result.Diagnostics.Add($"  Slot '{slotName}': no tags (will use empty container)");
            }
        }

        result.Diagnostics.Add($"Total: {result.SlotTags.Count} slot(s) with tags, {result.TotalTagCount} tag(s) total");
        return result;
    }

    /// <summary>
    /// Extract tag name strings from a GameplayTagContainer struct property (old format).
    /// UAssetAPI serializes this as:
    ///   StructPropertyData "GameplayTags" (StructType: GameplayTagContainer)
    ///     └─ GameplayTagContainerPropertyData "GameplayTags" (Value: FName[])
    /// </summary>
    private static List<string> ExtractTagsFromContainer(StructPropertyData? containerStruct, MaterialTagResult result)
    {
        var tags = new List<string>();

        if (containerStruct?.Value == null || containerStruct.Value.Count == 0)
            return tags;

        // Find the GameplayTagContainerPropertyData inside the struct
        foreach (var field in containerStruct.Value)
        {
            if (field is GameplayTagContainerPropertyData tagContainerProp)
            {
                if (tagContainerProp.Value != null)
                {
                    foreach (var tagFName in tagContainerProp.Value)
                    {
                        string? tagValue = tagFName?.Value?.Value;
                        if (!string.IsNullOrEmpty(tagValue))
                        {
                            tags.Add(tagValue);
                        }
                    }
                }
                break;
            }
        }

        return tags;
    }

    /// <summary>
    /// Extract tag name strings from an array property. Handles multiple formats:
    /// 1. TArray&lt;FName&gt;: ArrayPropertyData of NamePropertyData
    /// 2. TArray&lt;FGameplayTag&gt;: ArrayPropertyData of StructPropertyData with "TagName" field
    /// 3. TArray&lt;FGameplayTagEntry&gt;: ArrayPropertyData of StructPropertyData with "Tag" sub-struct containing "TagName"
    /// </summary>
    private static List<string> ExtractTagsFromArray(ArrayPropertyData? tagsArray, MaterialTagResult result)
    {
        var tags = new List<string>();

        if (tagsArray?.Value == null || tagsArray.Value.Length == 0)
            return tags;

        foreach (var element in tagsArray.Value)
        {
            // TArray<FName>: each element is a NamePropertyData directly
            if (element is NamePropertyData directName)
            {
                string? tagValue = directName.Value?.Value?.Value;
                if (!string.IsNullOrEmpty(tagValue))
                    tags.Add(tagValue);
            }
            // TArray<FGameplayTag> or TArray<FGameplayTagEntry>: struct element
            else if (element is StructPropertyData tagStruct && tagStruct.Value != null)
            {
                foreach (var field in tagStruct.Value)
                {
                    string? fieldName = field?.Name?.Value?.Value;
                    if (fieldName == null) continue;

                    // Direct "TagName" field (FGameplayTag)
                    if (fieldName.Equals("TagName", StringComparison.OrdinalIgnoreCase) && field is NamePropertyData nameProp)
                    {
                        string? tagValue = nameProp.Value?.Value?.Value;
                        if (!string.IsNullOrEmpty(tagValue))
                            tags.Add(tagValue);
                    }
                    // "Tag" sub-struct field (FGameplayTagEntry wrapper)
                    else if (fieldName.Equals("Tag", StringComparison.OrdinalIgnoreCase) && field is StructPropertyData innerStruct && innerStruct.Value != null)
                    {
                        foreach (var innerField in innerStruct.Value)
                        {
                            string? innerName = innerField?.Name?.Value?.Value;
                            if (innerName != null && innerName.Equals("TagName", StringComparison.OrdinalIgnoreCase) && innerField is NamePropertyData innerNameProp)
                            {
                                string? tagValue = innerNameProp.Value?.Value?.Value;
                                if (!string.IsNullOrEmpty(tagValue))
                                    tags.Add(tagValue);
                            }
                        }
                    }
                }
            }
        }

        return tags;
    }

    /// <summary>
    /// Apply tag assignments from a MaterialTagResult to a list of FSkeletalMaterial.
    /// Every slot gets an empty FGameplayTagContainer by default.
    /// Only slots with matching entries in the tag result get populated containers.
    /// </summary>
    /// <param name="materials">The parsed FSkeletalMaterial list from SkeletalMeshExport</param>
    /// <param name="tagResult">The tag result from ReadFromAsset</param>
    /// <param name="asset">The UAsset (needed for FName creation / name map)</param>
    /// <returns>Number of slots that had tags injected</returns>
    public static int ApplyTagsToMaterials(
        List<FSkeletalMaterial> materials,
        MaterialTagResult tagResult,
        UAsset asset)
    {
        if (materials == null || materials.Count == 0 || tagResult == null || !tagResult.FoundUserData)
            return 0;

        int injectedCount = 0;

        foreach (var material in materials)
        {
            // Ensure every slot has an empty container by default
            material.GameplayTagContainer ??= new FGameplayTagContainer();

            // Get the slot name as string (include FName Number suffix)
            string? slotName = material.MaterialSlotName?.ToString();
            if (string.IsNullOrEmpty(slotName))
                continue;

            // Check if we have tags for this slot
            if (tagResult.SlotTags.TryGetValue(slotName, out var tagStrings) && tagStrings.Count > 0)
            {
                material.GameplayTagContainer.GameplayTags = new List<FGameplayTag>();

                foreach (var tagString in tagStrings)
                {
                    // Ensure the tag name is in the asset's name map
                    FName tagFName = FName.FromString(asset, tagString);
                    material.GameplayTagContainer.GameplayTags.Add(new FGameplayTag(tagFName));
                }

                injectedCount++;
            }
        }

        return injectedCount;
    }

    /// <summary>
    /// Get the class name of an export by resolving its ClassIndex import.
    /// </summary>
    private static string? GetExportClassName(Export export)
    {
        try
        {
            var classType = export.GetExportClassType();
            return classType?.Value?.Value;
        }
        catch
        {
            return null;
        }
    }
}
