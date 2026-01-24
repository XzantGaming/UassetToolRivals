# Technical Analysis: UAssetTool Internals

This document provides a detailed technical analysis of the processes and algorithms used in UAssetTool for Marvel Rivals asset conversion.

## Table of Contents

1. [Overview](#overview)
2. [PAK File Extraction](#pak-file-extraction)
3. [Mipmap Stripping](#mipmap-stripping)
4. [SkeletalMesh Processing](#skeletalmesh-processing)
5. [StaticMesh Processing](#staticmesh-processing)
6. [NiagaraSystem Color Editing](#niagarasystem-color-editing)
7. [Zen Package Conversion](#zen-package-conversion)
8. [IoStore Container Format](#iostore-container-format)
9. [Export Map Building](#export-map-building)
10. [Import Resolution](#import-resolution)
11. [Script Objects Database](#script-objects-database)

---

## Overview

UAssetTool converts legacy Unreal Engine assets (`.uasset`/`.uexp`/`.ubulk`) to the Zen package format used by UE5.3+ games like Marvel Rivals. The conversion process involves:

1. Parsing the legacy asset structure
2. Applying game-specific patches (material padding, mipmap stripping)
3. Building the Zen package header with correct offsets
4. Creating IoStore containers (`.utoc`/`.ucas`) for game injection

---

## PAK File Extraction

### Overview

**Location:** `PakReader.cs`

PAK files are Unreal Engine's legacy archive format for packaging game assets. UAssetTool supports extracting assets from PAK v11 (UE5) files with various compression methods.

### PAK File Structure

```
┌─────────────────────────────────────────┐
│ Entry 0: FPakEntry Header + Data        │
├─────────────────────────────────────────┤
│ Entry 1: FPakEntry Header + Data        │
├─────────────────────────────────────────┤
│ ... more entries ...                    │
├─────────────────────────────────────────┤
│ Path Hash Index (optional)              │
├─────────────────────────────────────────┤
│ Full Directory Index                    │
├─────────────────────────────────────────┤
│ Primary Index                           │
├─────────────────────────────────────────┤
│ FPakInfo Footer (221 bytes for v11)     │
└─────────────────────────────────────────┘
```

### FPakInfo Footer Structure

```
Offset  Size  Field
──────  ────  ─────────────────────────────
+0      16    EncryptionKeyGuid
+16     1     bEncryptedIndex
+17     4     Magic (0x5A6F12E1)
+21     4     Version (11 for UE5)
+25     8     IndexOffset
+33     8     IndexSize
+41     20    IndexHash (SHA1)
+61     160   CompressionMethods (5 × 32 bytes)
──────  ────  ─────────────────────────────
Total:  221 bytes
```

### FPakEntry Header Structure

```
Offset  Size  Field
──────  ────  ─────────────────────────────
+0      8     Offset (within PAK file)
+8      8     CompressedSize
+16     8     UncompressedSize
+24     4     CompressionMethod index
+28     20    Hash (SHA1)
+48     4     BlockCount (if compressed)
+52     16×N  CompressionBlocks[N]
              - CompressedStart (8 bytes)
              - CompressedEnd (8 bytes)
```

### Encoded Entry Flags (Index)

The PAK index stores entries in an encoded format to save space:

```csharp
uint flags = reader.ReadUInt32();

// Bit layout:
// Bits 0-5:   CompressionBlockSize (raw value, shift << 11 for bytes)
// Bits 6-21:  CompressionBlocksCount
// Bit 22:     IsEncrypted
// Bits 23-28: CompressionSlot (method index)
// Bit 29:     IsSizeSafe (use 32-bit size)
// Bit 30:     IsUncompressedSizeSafe (use 32-bit size)
// Bit 31:     IsOffsetSafe (use 32-bit offset)
```

### Block Size Calculation

**Critical:** The compression block size is stored as a 6-bit value that must be shifted left by **11** (not 10):

```csharp
// Per CUE4Parse FPakEntry.cs:
compressionBlockSize = (bitfield & 0x3f) << 11;

// Example: 32 << 11 = 65536 (64KB standard block size)
```

### Block Offset Handling

Compression block offsets in FPakEntry are **relative to the entry start**:

```csharp
// Block offsets are relative to entry position
long absoluteBlockStart = entryOffset + block.CompressedStart;
long absoluteBlockEnd = entryOffset + block.CompressedEnd;
```

### Decompression Process

```
For each entry:
1. Read FPakEntry header at entry.Offset
2. If compressed:
   a. Read block count and block info
   b. For each block:
      - Seek to entryOffset + blockStart
      - Read compressed block data
      - Decompress using appropriate method
      - Append to output buffer
3. If encrypted:
   - Decrypt data using AES-256-ECB
   - Apply UE4's 4-byte chunk reversal pattern
```

### Supported Compression Methods

| Method | Description |
|--------|-------------|
| None | Uncompressed data |
| Zlib | Standard zlib compression |
| Gzip | Gzip compression |
| Oodle | Oodle Kraken/Leviathan/Mermaid |
| LZ4 | LZ4 fast compression |
| Zstd | Zstandard compression |

### AES Decryption

PAK files use AES-256-ECB with a custom byte-swapping pattern:

```csharp
// For each 16-byte block:
// 1. Reverse each 4-byte chunk BEFORE decryption
// 2. Decrypt with AES-ECB
// 3. Reverse each 4-byte chunk AFTER decryption

private static void ReverseChunks(byte[] block)
{
    for (int i = 0; i < 16; i += 4)
    {
        (block[i], block[i + 3]) = (block[i + 3], block[i]);
        (block[i + 1], block[i + 2]) = (block[i + 2], block[i + 1]);
    }
}
```

---

## Mipmap Stripping

### Purpose
Texture mods often only include the highest resolution mipmap. The game expects texture data to match the `NumMips` property, so we strip lower mipmaps and update metadata accordingly.

### Process

**Location:** `ZenConverter.cs` - `StripMipmapsFromTextureData()`

```
Original Texture Structure:
┌─────────────────────────────────┐
│ FTexturePlatformData Header     │
│ - SizeX, SizeY                  │
│ - NumSlices                     │
│ - PixelFormat                   │
│ - NumMips (e.g., 12)            │
├─────────────────────────────────┤
│ Mip 0 (4096x4096) - KEEP        │
├─────────────────────────────────┤
│ Mip 1 (2048x2048) - STRIP       │
├─────────────────────────────────┤
│ Mip 2 (1024x1024) - STRIP       │
├─────────────────────────────────┤
│ ... more mips - STRIP           │
└─────────────────────────────────┘
```

### Algorithm

1. **Detect texture data** by searching for pixel format signatures
2. **Read mip count** from FTexturePlatformData header
3. **Calculate mip 0 size** based on dimensions and pixel format
4. **Truncate data** to only include mip 0
5. **Update NumMips** property to 1
6. **Recalculate SerialSize** for the export

### Pixel Format Sizes

| Format | Bits Per Pixel | Block Size |
|--------|---------------|------------|
| DXT1/BC1 | 4 | 4x4 |
| DXT5/BC3 | 8 | 4x4 |
| BC7 | 8 | 4x4 |
| RGBA8 | 32 | 1x1 |

### Size Calculation
```csharp
int blockSize = 4; // For BC formats
int blocksX = (width + blockSize - 1) / blockSize;
int blocksY = (height + blockSize - 1) / blockSize;
int mipSize = blocksX * blocksY * bytesPerBlock;
```

---

## SkeletalMesh Processing

### FGameplayTagContainer Padding

**Location:** `ZenConverter.cs` - `PatchSkeletalMeshMaterialSlots()`

Marvel Rivals expects an `FGameplayTagContainer` after each `FSkeletalMaterial` in the materials array. Legacy assets don't have this field, so we inject it during conversion.

### FSkeletalMaterial Structure (Legacy)

```
Offset  Size  Field
──────  ────  ─────────────────────────────
+0      4     MaterialInterface (FPackageIndex)
+4      8     MaterialSlotName (FName)
+12     8     ImportedMaterialSlotName (FName)
+20     20    FMeshUVChannelInfo
              - bInitialized (1 byte)
              - bOverrideDensities (1 byte)
              - padding (2 bytes)
              - LocalUVDensities[4] (16 bytes)
──────  ────  ─────────────────────────────
Total:  40 bytes
```

### FSkeletalMaterial Structure (Marvel Rivals Zen)

```
Offset  Size  Field
──────  ────  ─────────────────────────────
+0      4     MaterialInterface (FPackageIndex)
+4      8     MaterialSlotName (FName)
+12     8     ImportedMaterialSlotName (FName)
+20     20    FMeshUVChannelInfo
+40     4     FGameplayTagContainer (count=0)
──────  ────  ─────────────────────────────
Total:  44 bytes
```

### Patching Algorithm

1. **Find material array** by searching for pattern:
   - `int32` count (1-50)
   - Followed by negative `FPackageIndex` values spaced 40 bytes apart

2. **Validate pattern** by checking multiple consecutive materials

3. **Create patched buffer** with size = original + (materialCount × 4)

4. **For each material:**
   - Copy 40 bytes of material data
   - Insert 4 bytes of zeros (empty FGameplayTagContainer)

5. **Copy remaining data** after material array

### Detection Pattern
```csharp
// Search for: count + materials at 40-byte intervals
for (int i = 4; i < dataLength - 80; i++)
{
    int count = BitConverter.ToInt32(data, i);
    if (count < 1 || count > 50) continue;
    
    int firstPkgIdx = BitConverter.ToInt32(data, i + 4);
    if (firstPkgIdx >= 0 || firstPkgIdx < -100) continue;
    
    // Verify subsequent materials are 40 bytes apart
    bool valid = true;
    for (int m = 1; m < count; m++)
    {
        int pkgIdx = BitConverter.ToInt32(data, i + 4 + (m * 40));
        if (pkgIdx >= 0 || pkgIdx < -100) { valid = false; break; }
    }
    
    if (valid) return (offset: i, count: count);
}
```

---

## StaticMesh Processing

### FStaticMaterial Structure

StaticMesh uses a different material structure than SkeletalMesh. For Marvel Rivals, the struct is 36 bytes without padding.

```
Offset  Size  Field
──────  ────  ─────────────────────────────
+0      4     MaterialInterface (FPackageIndex)
+4      8     MaterialSlotName (FName)
+12     4     OverlayMaterialInterface (FPackageIndex)
+16     20    FMeshUVChannelInfo
──────  ────  ─────────────────────────────
Total:  36 bytes
```

### Note on StaticMesh Padding

Unlike SkeletalMesh, StaticMesh in Marvel Rivals does **not** require FGameplayTagContainer padding. The materials are serialized at 36-byte intervals without additional fields.

---

## NiagaraSystem Color Editing

### Overview

**Location:** `ColorModifier.cs`, `NiagaraService.cs`

NiagaraSystem assets (`NS_*.uasset`) contain UE5 particle system definitions. Colors are stored in `NiagaraDataInterfaceColorCurve` exports within the `ShaderLUT` property.

### ShaderLUT Structure

**Critical Discovery:** ShaderLUT is stored as a **flat float array**, NOT as an array of LinearColor structs:

```
ShaderLUT: ArrayPropertyData<FloatProperty>
├── [0]  R0 (float32)
├── [1]  G0 (float32)
├── [2]  B0 (float32)
├── [3]  A0 (float32)
├── [4]  R1 (float32)
├── [5]  G1 (float32)
├── [6]  B1 (float32)
├── [7]  A1 (float32)
└── ... (typically 256-1024 floats = 64-256 colors)
```

### Binary Layout

```
Offset  Size  Field
──────  ────  ─────────────────────────────
+0      4     ArrayType name index
+4      4     Array count (number of floats)
+8      4     Color 0 - R (float32)
+12     4     Color 0 - G (float32)
+16     4     Color 0 - B (float32)
+20     4     Color 0 - A (float32)
+24     4     Color 1 - R (float32)
...
```

### HDR Color Values

Colors use **HDR (High Dynamic Range)** values:

| Value | Appearance |
|-------|------------|
| 0.0 | Black/transparent |
| 1.0 | Standard brightness |
| 2.0-5.0 | Bright/glowing |
| 10.0+ | Very bright bloom |

### Systematic Parsing Approach

```csharp
// Process ShaderLUT as groups of 4 floats (RGBA)
if (prop.Name?.Value?.Value == "ShaderLUT" && prop is ArrayPropertyData lutArray)
{
    for (int i = 0; i + 3 < lutArray.Value.Length; i += 4)
    {
        var rProp = lutArray.Value[i] as FloatPropertyData;
        var gProp = lutArray.Value[i + 1] as FloatPropertyData;
        var bProp = lutArray.Value[i + 2] as FloatPropertyData;
        var aProp = lutArray.Value[i + 3] as FloatPropertyData;
        
        rProp.Value = targetColor.R;
        gProp.Value = targetColor.G;
        bProp.Value = targetColor.B;
        aProp.Value = targetColor.A;
    }
}
```

### Why Not LinearColorPropertyData?

The USMAP mappings define ShaderLUT as `Array<FloatProperty>` for GPU efficiency. The baked LUT is a raw float buffer that shaders sample directly, not structured LinearColor objects.

### Structured Export Type

Like `SkeletalMeshExport` and `StaticMeshExport`, we implement a dedicated export class for consistent handling:

**`NiagaraDataInterfaceColorCurveExport`** - Dedicated export type for color curve data interfaces.

```csharp
public class NiagaraDataInterfaceColorCurveExport : NormalExport
{
    public FShaderLUT ShaderLUT { get; set; }
    
    public override void Read(AssetBinaryReader reader, int nextStarting)
    {
        base.Read(reader, nextStarting);
        ParseShaderLUT(); // Extract structured colors from properties
    }
    
    public override void Write(AssetBinaryWriter writer)
    {
        SyncShaderLUTToProperties(); // Sync back before writing
        base.Write(writer);
    }
    
    public void SetAllColors(float r, float g, float b, float a);
    public void SetColor(int index, float r, float g, float b, float a);
    public FShaderLUTColor? GetColor(int index);
    public int ColorCount { get; }
}
```

### FShaderLUT Struct

```csharp
public struct FShaderLUTColor
{
    public float R, G, B, A;
    public static int SerializedSize => 16; // 4 floats × 4 bytes
}

public class FShaderLUT
{
    public List<FShaderLUTColor> Colors;
    public int FloatCount => Colors.Count * 4;
    
    public void SetAllColors(float r, float g, float b, float a);
    public void SetColor(int index, float r, float g, float b, float a);
}
```

### Export Type Resolution

Added to `UAsset.cs` alongside other structured exports:

```csharp
else if (exportClassType == "NiagaraDataInterfaceColorCurve" || 
         exportClassType.EndsWith("DataInterfaceColorCurve"))
{
    Exports[i] = Exports[i].ConvertToChildExport<NiagaraDataInterfaceColorCurveExport>();
}
```

### Understanding ShaderLUT Contents

**Important:** Not all ShaderLUT arrays control visible colors. A typical NiagaraSystem file contains multiple `NiagaraDataInterfaceColorCurve` exports, each with its own ShaderLUT. These can control:

| LUT Type | Typical Export Name Pattern | What It Controls |
|----------|----------------------------|------------------|
| **Color** | `*Color*`, `*Glow*`, `*Emissive*` | Actual visible particle colors |
| **Alpha/Opacity** | `*Alpha*`, `*Opacity*`, `*Fade*` | Transparency over lifetime |
| **Scale** | `*Scale*`, `*Size*` | Particle size multipliers |
| **Velocity** | `*Speed*`, `*Velocity*` | Movement parameters |
| **Timing** | `*Lifetime*`, `*Spawn*` | Timing curves |

**Best Practice:** Use `niagara_details` to inspect export names before editing, then use `--export-name` to target only color-related LUTs.

### ShaderLUT Memory Layout

```
ShaderLUT Array (128 colors = 512 floats):
┌─────────────────────────────────────────────────────────┐
│ Color 0: [R₀, G₀, B₀, A₀]  (indices 0-3)               │
│ Color 1: [R₁, G₁, B₁, A₁]  (indices 4-7)               │
│ Color 2: [R₂, G₂, B₂, A₂]  (indices 8-11)              │
│ ...                                                     │
│ Color 127: [R₁₂₇, G₁₂₇, B₁₂₇, A₁₂₇]  (indices 508-511)│
└─────────────────────────────────────────────────────────┘
```

- **Color Index** = Float Index ÷ 4
- **Typical LUT Size**: 128 colors (512 floats) for smooth gradients
- **HDR Values**: Color values can exceed 1.0 for HDR/bloom effects (e.g., 10.0 for bright glow)

### Selective Editing Strategy

When modifying particle effects, consider:

1. **Preserve Alpha Gradients**: Use `--channels rgb` to keep original alpha/fade curves
2. **Target Specific LUTs**: Use `--export-name` to only modify color-related exports
3. **Gradient Preservation**: Use `--color-range` to modify only part of a gradient
4. **Single Color Edits**: Use `--color-index` for precise control

**Example Workflow:**
```bash
# 1. Inspect the file to see all LUTs
UAssetTool niagara_details NS_Effect.uasset mappings.usmap

# Output shows:
# Export 3: "NiagaraDataInterfaceColorCurve_Glow" - 128 colors
# Export 5: "NiagaraDataInterfaceColorCurve_Alpha" - 128 colors  
# Export 7: "NiagaraDataInterfaceColorCurve_Scale" - 64 colors

# 2. Only modify the Glow LUT, preserve RGB only
UAssetTool niagara_edit NS_Effect.uasset 0 10 0 1 --export-name Glow --channels rgb
```

### Frontend API

`NiagaraService.cs` provides JSON-based methods for GUI integration:

| Method | Purpose |
|--------|---------|
| `ListNiagaraFiles()` | List NS files with color curve counts |
| `GetNiagaraDetails()` | Get color curves with sample colors |
| `EditNiagaraColors()` | Modify colors with selective targeting |

### ColorEditRequest Options

```csharp
public class ColorEditRequest
{
    public string AssetPath { get; set; }
    public float R { get; set; }
    public float G { get; set; }
    public float B { get; set; }
    public float A { get; set; } = 1.0f;
    
    // Selective targeting
    public int? ExportIndex { get; set; }        // Target by export index
    public string? ExportNameFilter { get; set; } // Target by name pattern
    public int? ColorIndex { get; set; }          // Single color index
    public int? ColorIndexStart { get; set; }     // Range start (inclusive)
    public int? ColorIndexEnd { get; set; }       // Range end (inclusive)
    
    // Per-channel control
    public bool? ModifyR { get; set; }  // Default: true
    public bool? ModifyG { get; set; }  // Default: true
    public bool? ModifyB { get; set; }  // Default: true
    public bool? ModifyA { get; set; }  // Default: true
}
```

### Implementation Files

| File | Purpose |
|------|---------|
| `NiagaraStructs.cs` | `FShaderLUT`, `FShaderLUTColor`, `FRichCurve` structs |
| `NiagaraDataInterfaceColorCurveExport.cs` | Structured export type |
| `ColorModifier.cs` | Uses structured export for color modification |
| `NiagaraService.cs` | Frontend JSON API |

---

## Zen Package Conversion

### Overview

**Location:** `ZenConverter.cs` - `WriteZenPackage()`

The Zen package format is UE5's optimized asset format for IoStore containers. Converting from legacy involves:

1. Building the Zen header with all required sections
2. Transforming import/export maps to Zen format
3. Creating export bundles for dependency ordering
4. Writing the combined package data

### Zen Package Structure

```
┌─────────────────────────────────────────┐
│ FZenPackageSummary (variable size)      │
│ - Magic, HeaderSize, Name, Flags        │
│ - Section offsets                       │
├─────────────────────────────────────────┤
│ Name Map (FNameEntrySerialized[])       │
│ - Hashes and string data                │
├─────────────────────────────────────────┤
│ Imported Public Export Hashes           │
│ - uint64[] for each public import       │
├─────────────────────────────────────────┤
│ Import Map (FPackageObjectIndex[])      │
│ - Type + Hash pairs for imports         │
├─────────────────────────────────────────┤
│ Export Map (FExportMapEntry[])          │
│ - CookedSerialOffset, CookedSerialSize  │
│ - ObjectFlags, PublicExportHash         │
│ - Outer/Class/Super/Template indices    │
├─────────────────────────────────────────┤
│ Export Bundle Entries                   │
│ - CommandType (Create/Serialize)        │
│ - LocalExportIndex                      │
├─────────────────────────────────────────┤
│ Dependency Bundle Headers               │
│ - FirstEntryIndex, EntryCount           │
├─────────────────────────────────────────┤
│ Dependency Bundle Entries               │
│ - LocalImportOrExportIndex              │
├─────────────────────────────────────────┤
│ Imported Package Names                  │
│ - FName references to imported packages │
├─────────────────────────────────────────┤
│ [Preload Data - between Header and      │
│  CookedHeaderSize]                      │
├─────────────────────────────────────────┤
│ Export Data (serialized exports)        │
│ - Actual asset data                     │
└─────────────────────────────────────────┘
```

### Key Calculations

#### HeaderSize vs CookedHeaderSize

```
HeaderSize = Size of Zen header (up to end of imported package names)
CookedHeaderSize = HeaderSize + PreloadSize

Preload data sits between HeaderSize and CookedHeaderSize.
Export serial offsets are relative to CookedHeaderSize.
```

#### Export Serial Size Calculation

```csharp
// For each export, calculate actual serialized size
long actualSize = exportEnd - exportStart;

// Add material padding for SkeletalMesh (last export)
if (isSkeletalMesh && isLastExport)
    actualSize += materialCount * 4;

export.CookedSerialSize = actualSize;
```

---

## IoStore Container Format

### Overview

IoStore is UE5's container format consisting of:
- `.utoc` - Table of Contents (chunk metadata)
- `.ucas` - Container Archive Store (actual data)
- `.pak` - Companion PAK with chunk names (for mod loading)

### UTOC Structure

```
┌─────────────────────────────────────────┐
│ FIoStoreTocHeader                       │
│ - Magic (0x5F3F3F5F)                    │
│ - Version                               │
│ - ChunkCount, CompressedBlockCount      │
│ - DirectoryIndexSize                    │
├─────────────────────────────────────────┤
│ Chunk IDs (FIoChunkId[])                │
│ - 12 bytes each: ID + Type              │
├─────────────────────────────────────────┤
│ Chunk Offsets (FIoOffsetAndLength[])    │
│ - Offset into .ucas + Length            │
├─────────────────────────────────────────┤
│ Compression Blocks                      │
│ - Block metadata for decompression      │
├─────────────────────────────────────────┤
│ Directory Index                         │
│ - Path to chunk mapping                 │
├─────────────────────────────────────────┤
│ Chunk Perfect Hash Seeds                │
│ - For fast chunk lookup                 │
└─────────────────────────────────────────┘
```

### Chunk Types

| Type | Value | Description |
|------|-------|-------------|
| ExportBundleData | 0 | Package export data |
| BulkData | 1 | Bulk data (textures, etc.) |
| OptionalBulkData | 2 | Optional bulk data |
| MemoryMappedBulkData | 3 | Memory-mapped data |
| ScriptObjects | 4 | Script object database |
| ContainerHeader | 5 | Container metadata |
| ExternalFile | 6 | External file reference |
| ShaderCodeLibrary | 7 | Shader code |
| ShaderCode | 8 | Individual shaders |
| PackageStoreEntry | 9 | Package store entry |

### Chunk ID Calculation

```csharp
// Package chunk ID from package path
ulong packageId = CityHash64(packagePath.ToLower());
FIoChunkId chunkId = new FIoChunkId(packageId, 0, EIoChunkType.ExportBundleData);

// Bulk data chunk ID
FIoChunkId bulkChunkId = new FIoChunkId(packageId, 0, EIoChunkType.BulkData);
```

### Container Header Chunk

The Container Header chunk (type 5) contains metadata about all packages in the container. It is written as the last chunk in the UCAS file.

**Critical:** The Container Header must NOT be compressed. Compressing it causes the game to fail loading mods with 4+ packages.

```csharp
// In IoStoreWriter.Complete()
if (_containerHeaderVersion.HasValue && _packageStoreEntries.Count > 0)
{
    byte[] containerHeaderData = BuildContainerHeader();
    var headerChunkId = FIoChunkId.Create(_containerId.Value, 0, EIoChunkType.ContainerHeader);
    WriteChunkUncompressed(headerChunkId, containerHeaderData);  // NO compression!
}
```

### Container Header Structure

```
┌─────────────────────────────────────────┐
│ Magic: "IoCn" (0x496F436E)              │  4 bytes
│ Version: uint32                         │  4 bytes
│ Container ID: uint64                    │  8 bytes
├─────────────────────────────────────────┤
│ Package Count: uint32                   │  4 bytes
│ Package IDs: FPackageId[]               │  8 bytes each
├─────────────────────────────────────────┤
│ Store Entries Buffer Length: uint32     │  4 bytes
│ Store Entries Buffer:                   │
│   - Fixed entries (16 bytes each)       │
│   - Array data (imported packages)      │
└─────────────────────────────────────────┘
```

### Store Entry Serialization

Each package has a store entry with imported package references. The serialization uses a two-pass approach to avoid buffer corruption:

```csharp
// Pass 1: Calculate offsets for all array data
int fixedDataSize = packages.Count * entrySize;  // 16 bytes per entry
int currentArrayOffset = fixedDataSize;

foreach (var entry in packages)
{
    arrayDataOffsets.Add(currentArrayOffset);
    currentArrayOffset += entry.ImportedPackages.Count * 8;  // FPackageId = 8 bytes
}

// Pass 2: Write fixed entries, then array data sequentially
// Fixed entries contain relative offsets to their array data
// Array data is written contiguously after all fixed entries
```

**Bug Fixed:** The original implementation used buffer position jumping which corrupted data when there were 4+ packages with imported packages. The two-pass approach writes all fixed entries first, then all array data sequentially.

---

## Export Map Building

### Overview

**Location:** `ZenConverter.cs` - `BuildExportMapWithRecalculatedSizes()`

The export map defines each export's location and metadata in the Zen package.

### FExportMapEntry Structure (UE5.3+)

```
Offset  Size  Field
──────  ────  ─────────────────────────────
+0      8     CookedSerialOffset
+8      8     CookedSerialSize
+16     4     ObjectName (FMappedName)
+20     8     OuterIndex (FPackageObjectIndex)
+28     8     ClassIndex (FPackageObjectIndex)
+36     8     SuperIndex (FPackageObjectIndex)
+44     8     TemplateIndex (FPackageObjectIndex)
+52     8     PublicExportHash
+60     4     ObjectFlags
+64     1     FilterFlags
+65     3     Padding
──────  ────  ─────────────────────────────
Total:  72 bytes
```

### Serial Size Calculation

```csharp
// Calculate from legacy export positions
for (int i = 0; i < exports.Count; i++)
{
    long start = exports[i].SerialOffset - firstExportOffset;
    long end = (i + 1 < exports.Count) 
        ? exports[i + 1].SerialOffset - firstExportOffset
        : totalDataLength;
    
    long size = end - start;
    
    // Add padding for mesh exports
    if (isLastExport && materialPadding > 0)
        size += materialPadding;
    
    zenExport.CookedSerialSize = size;
}
```

### Public Export Hash

Public exports (accessible from other packages) need a hash for lookup:

```csharp
if (export.IsPublic)
{
    string fullPath = $"{packageName}.{exportName}";
    ulong hash = CityHash64(fullPath.ToLower());
    zenExport.PublicExportHash = hash;
}
```

---

## Import Resolution

### Overview

**Location:** `ZenConverter.cs` - `BuildImportMap()`

Imports reference objects from other packages. In Zen format, imports are resolved via script object hashes.

### Script Object Database

The game's `global.utoc` contains a script objects database mapping class paths to hashes:

```
/Script/Engine.StaticMesh -> 0x407CDE1A593E47CF
/Script/Engine.SkeletalMesh -> 0x6623523DEF01A2F7
/Script/Engine.Texture2D -> 0x...
```

### Import Types

1. **Script Objects** - Engine classes (StaticMesh, Texture2D, etc.)
   - Resolved via script object hash lookup
   - Type = ScriptImport

2. **Package Imports** - Assets from other packages
   - Resolved via package ID + export hash
   - Type = PackageImport

### FPackageObjectIndex Structure

```csharp
// 8 bytes total
enum EType : uint { Export = 0, ScriptImport = 1, PackageImport = 2, Null = 3 }

// Encoding:
// Bits 0-61: Value (hash or index)
// Bits 62-63: Type
ulong encoded = (value & 0x3FFFFFFFFFFFFFFF) | ((ulong)type << 62);
```

### Hash Lookup Process

```csharp
// For script imports (e.g., /Script/Engine.StaticMesh)
string fullPath = $"/Script/{packageName}.{className}";
ulong hash = ScriptObjectsDatabase.GetHash(fullPath);

// For package imports (e.g., MI_Character_Body)
string packagePath = import.PackagePath;
ulong packageId = CityHash64(packagePath.ToLower());
ulong exportHash = CityHash64($"{packagePath}.{exportName}".ToLower());
```

---

## Export Bundle Ordering

### Purpose

Export bundles define the order in which exports are created and serialized. Dependencies must be created before dependents.

### Bundle Entry Types

| Type | Value | Description |
|------|-------|-------------|
| ExportCommandType_Create | 0 | Create the export object |
| ExportCommandType_Serialize | 1 | Serialize the export data |

### Ordering Algorithm

```csharp
// 1. Build dependency graph
foreach (export in exports)
{
    if (export.OuterIndex > 0)
        dependencies[export].Add(exports[export.OuterIndex - 1]);
}

// 2. Topological sort for Create commands
List<int> createOrder = TopologicalSort(exports, dependencies);

// 3. Serialize in same order
List<ExportBundleEntry> entries = new();
foreach (int idx in createOrder)
    entries.Add(new ExportBundleEntry(idx, Create));
foreach (int idx in createOrder)
    entries.Add(new ExportBundleEntry(idx, Serialize));
```

---

## PackageGuid Handling

### Issue

Cooked assets in UE5 expect `PackageGuid` to be all zeros. Non-zero GUIDs cause loading failures.

### Solution

```csharp
// In WriteZenPackage, ensure GUID is zeroed
zenPackage.Summary.SavedHash = new FMD5Hash(); // All zeros
```

---

## Preload Data

### Purpose

Preload data contains dependency information that the engine reads before deserializing exports.

### Calculation

```csharp
// Preload size = dependency count * 4 bytes + header overhead
int preloadSize = preloadDependencyCount * 4;
if (preloadSize > 0)
    preloadSize += 37; // Header overhead

// CookedHeaderSize includes preload
int cookedHeaderSize = zenHeaderSize + preloadSize;
```

### Structure

```
┌─────────────────────────────────────────┐
│ Per-export dependency counts (4 bytes)  │
├─────────────────────────────────────────┤
│ Dependency indices (4 bytes each)       │
└─────────────────────────────────────────┘
```

---

## Summary

The conversion process handles numerous UE5-specific requirements:

1. **Mipmap Stripping** - Reduces texture size while maintaining compatibility
2. **Material Padding** - Adds FGameplayTagContainer for Marvel Rivals SkeletalMesh
3. **Export Map** - Recalculates serial sizes including padding
4. **Import Resolution** - Maps legacy imports to Zen script object hashes
5. **Export Bundling** - Orders exports by dependency for correct loading
6. **PackageGuid** - Zeros GUID for cooked asset compatibility
7. **Preload Data** - Calculates correct CookedHeaderSize

Each step is critical for the game to successfully load modded assets.

---

## Script Objects Database

### Overview

**Location:** `ScriptObjectsDatabase.cs`

The ScriptObjects database provides global name resolution for engine classes referenced by hash in Zen packages.

### Name Batch String Storage

**Critical Fix:** Names are stored **consecutively without alignment padding**. An earlier bug incorrectly added 2-byte alignment for UTF-16 strings, causing off-by-one truncation:

```
WRONG: "iagaraEmitterN" (first char skipped)
RIGHT: "NiagaraEmitter"
```

### Correct Parsing

```csharp
// Names stored consecutively WITHOUT alignment padding
int currentOffset = 0;
for (int i = 0; i < nameCount; i++)
{
    if (rawLengths[i] < 0)
    {
        // UTF-16 string
        int charCount = Math.Abs(short.MinValue - rawLengths[i]);
        nameByteLengths[i] = charCount * 2;
        // NO alignment padding in serialized format
    }
    else
    {
        // ASCII string
        nameByteLengths[i] = rawLengths[i];
    }
    nameOffsets[i] = currentOffset;
    currentOffset += nameByteLengths[i];
}
```

### FPackageObjectIndex Resolution

```csharp
public FName? GetName(FPackageObjectIndex index)
{
    if (!index.IsScriptImport) return null;
    
    uint typeAndId = index.TypeAndId;
    if (_scriptObjectMap.TryGetValue(typeAndId, out int nameIndex))
    {
        return _names[nameIndex];
    }
    return null;
}
```

### Impact on NiagaraSystem

This fix was essential for NiagaraSystem editing - truncated class names like `iagaraDataInterfaceColorCurve` prevented proper export type detection.
