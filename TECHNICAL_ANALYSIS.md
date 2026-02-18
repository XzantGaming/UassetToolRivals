# Technical Analysis: UAssetTool Internals

Technical documentation for UAssetTool's file format handling and data structures.

## Table of Contents

1. [PAK File Format](#pak-file-format)
2. [IoStore Container Format](#iostore-container-format)
3. [Zen Package Format](#zen-package-format)
4. [SkeletalMesh Structure](#skeletalmesh-structure)
5. [StaticMesh Structure](#staticmesh-structure)
6. [NiagaraSystem Data Interfaces](#niagarasystem-data-interfaces)
7. [MaterialTag Injection](#materialtag-injection)
8. [Legacy-to-Zen Conversion: Critical Fixes](#legacy-to-zen-conversion-critical-fixes)
9. [FString UTF-16LE Encoding](#fstring-utf-16le-encoding)
10. [IoStore Recompression](#iostore-recompression)
11. [Script Objects Database](#script-objects-database)
12. [JSON Serialization](#json-serialization)

---

## PAK File Format

**Implementation:** `IoStore/PakReader.cs`, `IoStore/PakWriter.cs`

PAK files are Unreal Engine's archive format for packaging game assets. UAssetTool supports PAK v11 (UE5) with encryption and compression.

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

### Supported Compression Methods

| Method | Description |
|--------|-------------|
| None | Uncompressed data |
| Zlib | Standard zlib compression |
| Gzip | Gzip compression |
| Oodle | Oodle Kraken/Leviathan/Mermaid |
| LZ4 | LZ4 fast compression |
| Zstd | Zstandard compression |

### AES Encryption

PAK files use AES-256-ECB with partial encryption. Only a portion of each file is encrypted based on a path hash calculation. The encryption uses a 4-byte chunk reversal pattern before and after decryption.

---

## SkeletalMesh Structure

**Implementation:** `UAssetAPI/ExportTypes/SkeletalMeshExport.cs`, `SkeletalMeshStructs.cs`

SkeletalMesh assets contain binary data in the "Extra Data" section after tagged properties.

### Extra Data Structure

The extra data section follows this order (per CUE4Parse USkeletalMesh.cs):

```
┌─────────────────────────────────────────┐
│ FStripDataFlags (2 bytes)               │
├─────────────────────────────────────────┤
│ FBoxSphereBounds (28 or 56 bytes)       │
│ - Origin (FVector)                      │
│ - BoxExtent (FVector)                   │
│ - SphereRadius (float/double)           │
├─────────────────────────────────────────┤
│ FSkeletalMaterial[] array               │
│ - int32 count                           │
│ - FSkeletalMaterial × count             │
├─────────────────────────────────────────┤
│ FReferenceSkeleton                      │
│ - FMeshBoneInfo[] (bone hierarchy)      │
│ - FTransform[] (reference pose)         │
│ - TMap<FName, int32> (name to index)    │
├─────────────────────────────────────────┤
│ bCooked (int32 as bool)                 │
├─────────────────────────────────────────┤
│ LOD Count (int32)                       │
├─────────────────────────────────────────┤
│ FSkeletalMeshLODRenderData[] (complex)  │
└─────────────────────────────────────────┘
```

### FStripDataFlags Structure

```
Offset  Size  Field
──────  ────  ─────────────────────────────
+0      1     GlobalStripFlags
+1      1     ClassStripFlags
──────  ────  ─────────────────────────────
Total:  2 bytes

Flags:
- Bit 0: Editor data stripped
- Bit 1: Data stripped for server
```

### FBoxSphereBounds Structure

```
Offset  Size  Field (non-LWC / LWC)
──────  ────  ─────────────────────────────
+0      12/24 Origin (FVector: 3 floats or 3 doubles)
+12/24  12/24 BoxExtent (FVector)
+24/48  4/8   SphereRadius (float or double)
──────  ────  ─────────────────────────────
Total:  28 bytes (float) or 56 bytes (double with LWC)
```

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

### FReferenceSkeleton Structure

The reference skeleton contains the bone hierarchy and reference pose:

```
┌─────────────────────────────────────────┐
│ RefBoneInfo Array                       │
│ - int32 count                           │
│ - FMeshBoneInfo × count                 │
│   - FName BoneName (8 bytes)            │
│   - int32 ParentIndex (4 bytes)         │
├─────────────────────────────────────────┤
│ RefBonePose Array                       │
│ - int32 count                           │
│ - FTransform × count                    │
│   - FQuat Rotation (16/32 bytes)        │
│   - FVector Translation (12/24 bytes)   │
│   - FVector Scale3D (12/24 bytes)       │
├─────────────────────────────────────────┤
│ NameToIndexMap (TMap<FName, int32>)     │
│ - int32 count                           │
│ - (FName, int32) × count                │
└─────────────────────────────────────────┘
```

### FMeshBoneInfo Structure

```
Offset  Size  Field
──────  ────  ─────────────────────────────
+0      8     BoneName (FName)
+8      4     ParentIndex (int32, -1 for root)
──────  ────  ─────────────────────────────
Total:  12 bytes
```

### FGameplayTagContainer Structure

**Implementation:** `UAssetAPI/UnrealTypes/FGameplayTagContainer.cs`

Marvel Rivals expects an `FGameplayTagContainer` after each `FSkeletalMaterial` in the materials array.

```
FGameplayTagContainer Binary Format:
──────────────────────────────────────────────────────
int32           GameplayTags count
FGameplayTag[]  GameplayTags array (each tag = FName = 8 bytes)
──────────────────────────────────────────────────────
Empty container = 4 bytes (count = 0)
```

### Implementation Files

| File | Purpose |
|------|---------|
| `SkeletalMeshStructs.cs` | FStripDataFlags, FBoxSphereBounds, FMeshBoneInfo, FReferenceSkeleton |
| `SkeletalMeshExport.cs` | Main export class with parsing and serialization |
| `MeshMaterials.cs` | FSkeletalMaterial, FStaticMaterial, FMeshUVChannelInfo |

---

## StaticMesh Structure

**Implementation:** `UAssetAPI/ExportTypes/StaticMeshExport.cs`

StaticMesh uses a different material structure than SkeletalMesh. For Marvel Rivals, the struct is 36 bytes without FGameplayTagContainer padding.

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

---

## NiagaraSystem Data Interfaces

**Implementation:** `UAssetAPI/ExportTypes/Niagara/`, `NiagaraService.cs`

NiagaraSystem assets (`NS_*.uasset`) contain particle system definitions. Colors are stored in specialized data interface exports.

### ShaderLUT Structure

ShaderLUT is stored as a **flat float array**, not as an array of LinearColor structs:

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

### Supported Data Interface Types

#### ShaderLUT-Based Curve Types

These types store pre-baked gradient data in a `ShaderLUT` float array for GPU sampling:

| Type | Data Format | Color Relevance | Use Case |
|------|-------------|-----------------|----------|
| **NiagaraDataInterfaceColorCurve** | RGBA (4 floats) | ✅ Direct RGBA | Color gradients over lifetime |
| **NiagaraDataInterfaceVectorCurve** | XYZ (3 floats) | ✅ RGB (no alpha) | RGB color curves, 3D directions |
| **NiagaraDataInterfaceCurve** | Single float | Indirect | Opacity, scale, speed curves |
| **NiagaraDataInterfaceVector2DCurve** | XY (2 floats) | Indirect | UV offsets, 2D positions |

### Array-Based Data Interface Types

These types store direct value arrays in properties (`ColorData`, `FloatData`, `VectorData`):

| Type | Property | Data Format | Color Relevance |
|------|----------|-------------|-----------------|
| **NiagaraDataInterfaceArrayColor** | `ColorData` | LinearColor[] | ✅ Direct RGBA |
| **NiagaraDataInterfaceArrayFloat3** | `VectorData` | Vector3[] | ✅ RGB (no alpha) |
| **NiagaraDataInterfaceArrayFloat** | `FloatData` | float[] | Indirect (opacity) |
| **NiagaraDataInterfaceArrayInt32** | `IntData` | int[] | Not color-related |

### ShaderLUT Memory Layouts

**ColorCurve (RGBA - 4 floats per entry):**
```
[R₀, G₀, B₀, A₀, R₁, G₁, B₁, A₁, R₂, G₂, B₂, A₂, ...]
 └─ Color 0 ─┘  └─ Color 1 ─┘  └─ Color 2 ─┘
```

**VectorCurve (XYZ/RGB - 3 floats per entry):**
```
[X₀, Y₀, Z₀, X₁, Y₁, Z₁, X₂, Y₂, Z₂, ...]
 └ Vec 0 ┘  └ Vec 1 ┘  └ Vec 2 ┘
```

**Curve (single float per entry):**
```
[V₀, V₁, V₂, V₃, ...]
```

**Vector2DCurve (XY - 2 floats per entry):**
```
[X₀, Y₀, X₁, Y₁, X₂, Y₂, ...]
 └Vec0┘  └Vec1┘  └Vec2┘
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
| `NiagaraStructs.cs` | `FShaderLUT`, `FShaderLUTColor`, `FShaderLUTFloat`, `FShaderLUTVector2D`, `FShaderLUTVector3` structs |
| `NiagaraDataInterfaceColorCurveExport.cs` | ColorCurve export (RGBA ShaderLUT) |
| `NiagaraDataInterfaceCurveExport.cs` | Curve export (float ShaderLUT) |
| `NiagaraDataInterfaceVector2DCurveExport.cs` | Vector2DCurve export (XY ShaderLUT) |
| `NiagaraDataInterfaceVectorCurveExport.cs` | VectorCurve export (XYZ/RGB ShaderLUT) |
| `NiagaraDataInterfaceArrayExports.cs` | ArrayColor, ArrayFloat, ArrayFloat3, ArrayInt32 exports |
| `ColorModifier.cs` | Uses structured exports for color modification |
| `NiagaraService.cs` | Frontend JSON API with all curve type support |

### NiagaraDetailsResult Structure

The `niagara_details` command returns comprehensive information about all data interfaces:

```json
{
  "success": true,
  "totalExports": 269,
  // ShaderLUT-based curves
  "colorCurveCount": 12,
  "totalColorCount": 1536,
  "colorCurves": [...],
  "floatCurveCount": 40,
  "totalFloatCount": 2120,
  "floatCurves": [...],
  "vector2DCurveCount": 0,
  "totalVector2DCount": 0,
  "vector2DCurves": [],
  "vector3CurveCount": 8,
  "totalVector3Count": 1024,
  "vector3Curves": [...],
  // Array-based data interfaces
  "arrayColorCount": 4,
  "totalArrayColorValues": 12,
  "arrayColors": [...],
  "arrayFloatCount": 10,
  "totalArrayFloatValues": 30,
  "arrayFloats": [...],
  "arrayFloat3Count": 0,
  "totalArrayFloat3Values": 0,
  "arrayFloat3s": []
}
```

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
CookedHeaderSize = Legacy .uasset file size (NOT zenHeaderSize + preloadSize)

CRITICAL: CookedHeaderSize must equal the original legacy .uasset file size.
This is what retoc uses and what the engine expects for serial offset calculations.
Using zenHeaderSize + preloadSize produces wrong offsets and breaks loading.

Export serial offsets are relative to CookedHeaderSize.
```

```csharp
// CORRECT:
int cookedHeaderSize = (int)new FileInfo(asset.FilePath).Length;

// WRONG (old buggy formula):
// int cookedHeaderSize = zenHeaderSize + preloadSize;
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

## IoStore Obfuscation (--obfuscate)

### Overview

**Location:** `Program.cs` - `CliCreateModIoStore()`, `IoStoreWriter.cs`

The `--obfuscate` flag enables mod protection by encrypting the IoStore with the game's AES key while setting a mismatched `EncryptionKeyGuid`. This prevents extraction tools like FModel from decrypting the mod while the game can still load it.

### How It Works

1. **Encryption with Game's AES Key**: The mod data is encrypted using Marvel Rivals' AES-256 key
2. **Zeroed EncryptionKeyGuid**: The UTOC header's `EncryptionKeyGuid` is set to all zeros instead of the game's actual GUID
3. **Encrypted Flag Set**: The `EIoContainerFlags.Encrypted` bit is set in the container flags

### Why It Works

**FModel/Extraction Tools:**
- Check `EncryptionKeyGuid` in UTOC header to find matching AES key from their key database
- All-zeros GUID doesn't match any known key → decryption fails
- Tools report "encrypted" but can't decrypt

**Game Engine:**
- Ignores `EncryptionKeyGuid` field
- Tries its hardcoded AES key directly on encrypted data
- Decryption succeeds → mod loads normally

### UTOC Header Structure (Encryption Fields)

```
Offset  Size  Field
──────  ────  ─────────────────────────────
+0x40   16    EncryptionKeyGuid (set to zeros for obfuscation)
+0x50   1     ContainerFlags (bit 1 = Encrypted)
```

### Usage

**CLI:**
```bash
UAssetTool create_mod_iostore output input_assets --obfuscate
```

**JSON Mode:**
```json
{"action": "create_mod_iostore", "output_path": "...", "input_dir": "...", "obfuscate": true}
```

### Implementation

```csharp
// In IoStoreWriter constructor
private byte[] _encryptionKeyGuid = new byte[16];  // All zeros by default

// When --obfuscate is used:
enableEncryption = true;
aesKey = MARVEL_RIVALS_AES_KEY;  // Game's actual key
// _encryptionKeyGuid stays as zeros → GUID mismatch

// In Complete():
if (_enableEncryption)
    containerFlags |= EIoContainerFlags.Encrypted;
header.EncryptionKeyGuid = _encryptionKeyGuid;  // Zeros, not game's GUID
```

### Limitations

- Only works for games where the engine ignores `EncryptionKeyGuid` (like Marvel Rivals)
- Does not protect against reverse-engineering by someone with the AES key who manually decrypts
- Provides obfuscation, not true DRM

---

## Mod Extraction (--mod argument)

### Overview

**Location:** `Program.cs` - `CliExtractIoStoreLegacy()`, `FZenPackageContext.cs`

The `--mod` argument enables extracting assets from modded IoStore containers while using the game's pak files for import resolution. This is essential for reverse-engineering mods or extracting modified assets back to legacy format.

### Use Cases

1. **Mod Analysis** - Extract modded assets to inspect changes
2. **Mod Porting** - Convert mod assets to legacy format for editing
3. **Dependency Resolution** - Extract mod assets with their game dependencies

### Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                     FZenPackageContext                          │
├─────────────────────────────────────────────────────────────────┤
│  Game Containers (encrypted, AES key required)                  │
│  ├── global.utoc (script objects, loaded first)                 │
│  ├── pakchunk0-WindowsClient.utoc                               │
│  ├── pakchunk1-WindowsClient.utoc                               │
│  └── ... (import resolution source)                             │
├─────────────────────────────────────────────────────────────────┤
│  Mod Containers (unencrypted, loaded with PRIORITY)             │
│  ├── my_mod_P.utoc                                              │
│  └── ... (extraction source, overrides game packages)           │
└─────────────────────────────────────────────────────────────────┘
```

### Priority Loading

Mod containers are loaded with `LoadContainerWithPriority()` which:

1. **Overrides existing packages** - If a mod contains a package that also exists in game files, the mod version takes precedence
2. **Clears cached data** - Any previously cached package data is invalidated when overridden
3. **No encryption** - Mod containers are loaded without AES key (mods are not encrypted)

```csharp
// Game containers use AES key
context.LoadContainer(gameUtocPath);  // Uses _aesKey

// Mod containers skip encryption
context.LoadContainerWithPriority(modUtocPath);  // Uses null for AES key
```

### Extraction Modes

#### Mode 1: Extract All Mod Packages
When `--mod` is specified without `--filter`, extracts all packages from mod containers:

```bash
UAssetTool extract_iostore_legacy "C:/Game/Paks" output --mod "C:/Mods/my_mod.utoc"
```

#### Mode 2: Filtered Extraction from Mods
When both `--mod` and `--filter` are specified, filters apply to mod packages:

```bash
UAssetTool extract_iostore_legacy "C:/Game/Paks" output --mod "C:/Mods/" --filter SK_1014
```

#### Mode 3: With Dependencies
When `--with-deps` is added, dependencies are resolved from game containers:

```bash
UAssetTool extract_iostore_legacy "C:/Game/Paks" output --mod "C:/Mods/my_mod.utoc" --with-deps
```

### Path Handling

The `--mod` argument accepts:

| Input Type | Behavior |
|------------|----------|
| Single `.utoc` file | Loads that specific container |
| Directory path | Loads all `.utoc` files in the directory |
| Multiple paths | Loads each path (files or directories) |

```bash
# Single utoc file
--mod "C:/Mods/my_mod_P.utoc"

# Directory (loads all .utoc files)
--mod "C:/Mods/MyModFolder/"

# Multiple paths
--mod "C:/Mods/mod1.utoc" "C:/Mods/mod2.utoc" "C:/Mods/SharedAssets/"
```

### Container Index Tracking

The extraction process tracks which container indices are mod containers:

```csharp
HashSet<int> modContainerIndices = new();

foreach (var modPath in modPaths)
{
    int containerIdx = context.ContainerCount;
    context.LoadContainerWithPriority(modPath);
    modContainerIndices.Add(containerIdx);
}

// Get packages only from mod containers
foreach (var containerIdx in modContainerIndices)
{
    foreach (var pkgId in context.GetPackageIdsFromContainer(containerIdx))
    {
        packageIds.Add(pkgId);
    }
}
```

### Import Resolution Flow

```
1. Load game containers (with AES key)
   └── Packages indexed, available for import resolution

2. Load mod containers (no encryption, with priority)
   └── Mod packages override game packages in index

3. For each mod package to extract:
   a. Read package data from mod container
   b. Parse Zen header and export map
   c. Resolve imports:
      - Script imports → ScriptObjects database (from global.utoc)
      - Package imports → Game containers (original assets)
   d. Convert to legacy .uasset/.uexp format
   e. Write output files

4. If --with-deps:
   a. Collect imported package IDs from converted packages
   b. Extract dependencies from game containers
   c. Repeat until no new dependencies
```

### Example Workflow

```bash
# 1. Extract a character skin mod with dependencies
UAssetTool extract_iostore_legacy ^
    "C:/MarvelRivals/Paks" ^
    "./extracted" ^
    --mod "C:/Mods/CustomSkin_P.utoc" ^
    --with-deps

# Output:
# Loading game containers from: C:/MarvelRivals/Paks
#   Loading global.utoc...
#   Loading 15 game containers...
# Loading mod containers...
# [Context] Loaded container [PRIORITY]: CustomSkin_P (3 new packages, 0 overridden)
# Extracting all 3 packages from mod container(s)
# Converted: /Game/Marvel/Characters/1014/Skins/Custom/SK_1014_Custom
# Converted: /Game/Marvel/Characters/1014/Skins/Custom/MI_1014_Custom_Body
# [DEP] Converted: /Game/Marvel/Characters/1014/Meshes/SK_1014
# Extraction complete: 3 converted, 0 failed, 0 skipped
```

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

Public exports (accessible from other packages) need a hash for lookup. **Critical:** The export name must include the FName number suffix when `Number > 0`.

```csharp
if (export.IsPublic)
{
    string exportName = export.ObjectName.Value;
    int nameNumber = export.ObjectName.Number;
    
    // UE FName with Number > 0 displays as "BaseName_N" where N = Number - 1
    if (nameNumber > 0)
        exportName = exportName + "_" + (nameNumber - 1);
    
    string fullPath = $"{packageName}.{exportName}";
    ulong hash = CityHash64(fullPath.ToLower());
    zenExport.PublicExportHash = hash;
}
```

**Example:** An FName `{Value="LODSettings", Number=304}` hashes as `"LODSettings_303"`, not `"LODSettings"`.

#### PublicExportHash Dual Meaning (UE5.3+ NoExportInfo)

In `NoExportInfo` version, `FExportMapEntry.PublicExportHash` stores a `GlobalImportIndex` (`FPackageObjectIndex`), **not** a CityHash64 hash. However, the `ImportedPublicExportHashes` array in the Zen header does contain CityHash64 values. During extraction, compare by computing CityHash64 of each export's display name.

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

## MaterialTag Injection

### Overview

**Implementation:** `ZenPackage/MaterialTagReader.cs`, `ZenPackage/ZenConverter.cs`

MaterialTag injection reads `MaterialTagAssetUserData` exports from SkeletalMesh `.uasset` files and injects per-slot `FGameplayTagContainer` data into `FSkeletalMaterial` during Zen conversion. This is enabled by default and can be disabled with `--no-material-tags`.

Marvel Rivals uses these tags for material visibility control (e.g., hiding weapons, toggling equipment).

### Pipeline

```
1. Load legacy .uasset with full export parsing
2. Find SkeletalMeshExport, parse extra data (materials, skeleton, LODs)
3. Find MaterialTagAssetUserData export by class name
4. Read MaterialSlotTags array → per-slot tag assignments
5. Inject tags into FSkeletalMaterial.GameplayTagContainer
6. Re-serialize with IncludeGameplayTags=true
7. Remap /Script/MaterialTagPlugin imports → /Script/Engine.AssetUserData
8. Build Zen package (stripped export never appears in final output)
```

### MaterialTagAssetUserData Structure

The UE plugin stores tag data as a `UAssetUserData` subclass attached to the SkeletalMesh:

```
UMaterialTagAssetUserData (export)
└─ MaterialSlotTags: TArray<FMaterialSlotTagEntry>
     └─ [i] FMaterialSlotTagEntry (struct)
          ├─ MaterialSlotName: FName
          └─ GameplayTags: TArray<FGameplayTagEntry>
               └─ [j] FGameplayTagEntry (struct)
                    └─ Tag: FGameplayTag
                         └─ TagName: FName
```

`MaterialTagReader.cs` handles multiple serialization formats:
- `StructPropertyData` → `GameplayTagContainerPropertyData` (old format)
- `ArrayPropertyData` → `StructPropertyData` with `Tag.TagName` (new format)
- `ArrayPropertyData` → `NamePropertyData` (direct FName array)

### Import Remapping

The game doesn't know about `/Script/MaterialTagPlugin`. During `BuildImportMap()`, all imports referencing `MaterialTagPlugin` are remapped:

| Original Import | Remapped To |
|----------------|-------------|
| `/Script/MaterialTagPlugin` | `/Script/Engine` |
| `/Script/MaterialTagPlugin.MaterialTagAssetUserData` | `/Script/Engine.AssetUserData` |
| `/Script/MaterialTagPlugin.Default__MaterialTagAssetUserData` | `/Script/Engine.Default__AssetUserData` |

This allows the export to remain in the package (can't strip it — Extras raw binary has `FPackageIndex` values referencing MorphTargets by export index) while preventing an Access Violation crash.

### Companion UE Plugin

The `MaterialTagPlugin` (UE 5.3 editor plugin) provides the authoring UI:
- Drag-and-drop tag assignment from preset reference data
- Per-slot tag display with material slot indices
- Preset INI system (`<Project>/Config/MaterialTagPresets.ini`) generated by `MaterialSlotViewer`
- Array guard preventing accidental slot add/delete

### Implementation Files

| File | Purpose |
|------|---------|
| `MaterialTagReader.cs` | Reads `MaterialTagAssetUserData` exports, extracts per-slot tags |
| `ZenConverter.cs` | Injection point + import remapping in `BuildImportMap()` |
| `MeshMaterials.cs` | `FSkeletalMaterial.GameplayTagContainer` field |
| `FGameplayTagContainer.cs` | Binary serialization of tag containers |

---

## Legacy-to-Zen Conversion: Critical Fixes

### Overview

These fixes were identified by comparing our tool's output byte-for-byte against `retoc`'s output (which produces known-working in-game mods). All fixes apply to both `ZenConverter.cs` and `AnimBlueprintZenConverter.cs`.

### 1. CookedHeaderSize

**See:** [HeaderSize vs CookedHeaderSize](#headersize-vs-cookedheadersize)

Must be the legacy `.uasset` file size. Using `zenHeaderSize + preloadSize` produces incorrect serial offsets.

### 2. FilterFlags Preservation

The engine uses `FilterFlags` to control export visibility per platform. Our tool was forcing `FilterFlags = None` for all exports. `retoc` preserves the original flags from legacy exports.

```csharp
// CORRECT: preserve from legacy export
EExportFilterFlags filterFlags = EExportFilterFlags.None;
if (export.bNotForClient)
    filterFlags = EExportFilterFlags.NotForClient;
else if (export.bNotForServer)
    filterFlags = EExportFilterFlags.NotForServer;

// WRONG: forcing None for all exports
// filterFlags = EExportFilterFlags.None;
```

| Flag | Value | Meaning |
|------|-------|---------|
| `None` | 0 | Available on all platforms |
| `NotForClient` | 1 | Server-only export |
| `NotForServer` | 2 | Client-only export |

### 3. Export Data: PACKAGE_FILE_TAG Preservation

Legacy `.uexp` files end with a 4-byte tag `PACKAGE_FILE_TAG` (`0x9E2A83C1`). Our tool was stripping this tag when writing export data. `retoc` preserves the full `.uexp` contents including the tag.

```csharp
// CORRECT: write full .uexp data
writer.Write(uexpData, 0, uexpData.Length);

// WRONG: stripping trailing 4 bytes
// writer.Write(uexpData, 0, uexpData.Length - 4);
```

### 4. PublicExportHash: FName Number Suffix

**See:** [Public Export Hash](#public-export-hash)

Must include the FName number suffix (`_N` where `N = Number - 1`) in the hash calculation.

### 5. Name Map ASCII Encoding

The Zen name map must use **ASCII encoding** for all names, matching the game's behavior. Non-ASCII characters are replaced with `?` (`0x3F`). Using UTF-16LE for non-ASCII names causes 18-byte size differences per affected name.

```csharp
// Name map encoding: always ASCII
// Hashes use CityHash64 of lowercase ASCII name
// Headers use big-endian i16 (positive = ASCII byte length)
// String data is consecutive ASCII bytes

foreach (var name in names)
{
    string lower = name.ToLowerInvariant();
    byte[] bytes = Encoding.ASCII.GetBytes(lower);
    ulong hash = CityHash64(bytes, 0, bytes.Length);
    writer.Write(hash);
}
```

### Verification Method

```bash
# 1. Generate mod with our tool
UAssetTool create_mod_iostore output_base input_dir --usmap mappings.usmap

# 2. Generate mod with retoc from same input
retoc create output_retoc input_dir

# 3. Extract raw chunks from both
retoc unpack-raw our_output.utoc our_raw_chunks/
retoc unpack-raw retoc_output.utoc retoc_raw_chunks/

# 4. Compare chunk-by-chunk
# Check CookedHeaderSize, FilterFlags, ExportDataSize, PublicExportHash
```

---

## FString UTF-16LE Encoding

### Overview

UE's `FString` serialization uses a length-prefixed format where negative length indicates UTF-16LE encoding.

### Format

```
Positive length N: N bytes of UTF-8/ASCII string + null terminator
Negative length N: |N| UTF-16LE characters + null terminator (2 bytes)
```

```csharp
static bool NeedsUtf16(string s)
{
    foreach (char c in s)
        if (c > 127) return true;
    return false;
}

// Writing:
if (NeedsUtf16(str))
{
    byte[] utf16 = Encoding.Unicode.GetBytes(str);
    writer.Write(-(str.Length + 1));  // Negative = UTF-16LE
    writer.Write(utf16);
    writer.Write((short)0);          // Null terminator (2 bytes)
}
else
{
    byte[] ascii = Encoding.UTF8.GetBytes(str);
    writer.Write(str.Length + 1);     // Positive = ASCII/UTF-8
    writer.Write(ascii);
    writer.Write((byte)0);           // Null terminator (1 byte)
}
```

**Affected characters:** Em Dash (—), fullwidth parentheses (（）), CJK characters (世界偏移大小), etc.

---

## IoStore Recompression

### Overview

**Implementation:** `IoStore/IoStoreRecompressor.cs`

Recompresses an uncompressed IoStore container with Oodle Kraken compression.

### Algorithm

```
1. Read ALL chunks into memory from original .utoc/.ucas
2. Separate the ContainerHeader chunk (type 6) from regular chunks
3. Dispose the reader (release file locks)
4. Create a new IoStoreWriter with:
   - containerHeaderVersion = null (don't generate new header)
   - enableCompression = true
5. Write all regular chunks compressed via WriteChunk()
6. Write original ContainerHeader uncompressed via WriteChunkUncompressed()
7. Call Complete() to finalize TOC
8. Replace original files with recompressed ones
```

### Critical Design Decisions

- **Container header pass-through:** The original container header is preserved byte-for-byte and written uncompressed. Generating a new container header would require parsing the original to extract store entries (imported packages, export counts, etc.), which is complex and error-prone.
- **No containerHeaderVersion:** Passing `null` prevents the writer from generating a duplicate container header chunk.
- **Reader scoping:** The `IoStoreReader` must be disposed before attempting to delete/replace the original files, otherwise file locks prevent the swap.
- **Temp directory:** Uses a temp subdirectory (not `.tmp` extension) because `IoStoreWriter` derives the `.ucas` path from `Path.ChangeExtension(tocPath, ".ucas")`.

### Container Header Versions

| Version | Value | Entry Size | Fields |
|---------|-------|------------|--------|
| `Initial` | 0 | 32 bytes | ExportBundlesSize, ExportCount, ExportBundleCount, LoadOrder |
| `LocalizedPackages` | 1 | 24 bytes | ExportCount, ExportBundleCount |
| `OptionalSegmentPackages` | 2 | 24 bytes | ExportCount, ExportBundleCount |
| `NoExportInfo` | 3 | 16 bytes | ImportedPackages only |
| `SoftPackageReferences` | 4 | 16 bytes | ImportedPackages + soft refs flag |

**Marvel Rivals uses `NoExportInfo` (version 3).** The recompressor must preserve this version, not hardcode a different one.

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

// NOTE: CookedHeaderSize is NOT zenHeaderSize + preloadSize.
// CookedHeaderSize = legacy .uasset file size (see Key Calculations section).
// Preload data fills the gap between the Zen header end and CookedHeaderSize.
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
3. **MaterialTag Injection** - Reads per-slot gameplay tags from MaterialTagAssetUserData and injects into FSkeletalMaterial
4. **Import Remapping** - Remaps /Script/MaterialTagPlugin imports to /Script/Engine.AssetUserData
5. **Export Map** - Recalculates serial sizes including padding
6. **Import Resolution** - Maps legacy imports to Zen script object hashes
7. **Export Bundling** - Orders exports by dependency for correct loading
8. **PackageGuid** - Zeros GUID for cooked asset compatibility
9. **Preload Data** - Calculates correct CookedHeaderSize

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

---

## JSON Serialization

**Implementation:** `UAssetAPI/UAsset.cs`

UAssetTool supports full bidirectional JSON serialization of uasset files, enabling easy inspection and modification of asset data without binary editing.

### Capabilities

| Direction | Method | Description |
|-----------|--------|-------------|
| Export | `SerializeJson()` | Converts UAsset to JSON string |
| Import | `DeserializeJson()` | Parses JSON string back to UAsset |

### CLI Usage

```bash
# Convert uasset to JSON (single file)
UAssetTool to_json <uasset_path> [usmap_path] [output_dir]

# Convert uasset to JSON (batch - all .uasset files in directory)
UAssetTool to_json <directory> [usmap_path] [output_dir]

# Convert JSON back to uasset
UAssetTool from_json <json_path> <output_uasset_path> [usmap_path]
```

**`to_json` Arguments:**
- `<path>` - Path to a `.uasset` file or directory containing `.uasset` files
- `[usmap_path]` - Optional `.usmap` mappings file for better property parsing
- `[output_dir]` - Optional output directory (default: same location as input)

**Batch Mode:** When given a directory, recursively processes all `.uasset` files and preserves relative directory structure in output.

### Interactive JSON API

```json
{"action": "export_to_json", "file_path": "path/to/asset.uasset", "usmap_path": "path/to/mappings.usmap"}
{"action": "import_from_json", "file_path": "path/to/output.uasset", "usmap_path": "...", "json_data": "..."}
```

### JSON Structure

The serialized JSON preserves the complete asset structure:

```json
{
  "Info": "Serialized with UAssetAPI x.x.x",
  "PackageGuid": "00000000-0000-0000-0000-000000000000",
  "EngineVersion": "VER_UE5_3",
  "CustomVersionContainer": [...],
  "NameMap": ["None", "ObjectName", ...],
  "Imports": [
    {
      "ClassPackage": "/Script/Engine",
      "ClassName": "StaticMesh",
      "OuterIndex": 0,
      "ObjectName": "SM_MyMesh"
    }
  ],
  "Exports": [
    {
      "ObjectName": "MyExport",
      "ClassIndex": {...},
      "Data": [
        {
          "$type": "UAssetAPI.PropertyTypes.Objects.IntPropertyData",
          "Name": "MyIntProperty",
          "Value": 42
        }
      ]
    }
  ]
}
```

### Property Types

All UAssetAPI property types are serialized with their `$type` discriminator for proper deserialization:

| Property Type | JSON Representation |
|---------------|---------------------|
| `IntPropertyData` | `{"$type": "...IntPropertyData", "Value": 42}` |
| `FloatPropertyData` | `{"$type": "...FloatPropertyData", "Value": 1.5}` |
| `StrPropertyData` | `{"$type": "...StrPropertyData", "Value": "text"}` |
| `ArrayPropertyData` | `{"$type": "...ArrayPropertyData", "Value": [...]}` |
| `StructPropertyData` | `{"$type": "...StructPropertyData", "Value": [...]}` |
| `ObjectPropertyData` | `{"$type": "...ObjectPropertyData", "Value": {...}}` |

### Use Cases

1. **Asset Inspection** - Human-readable view of asset contents
2. **Batch Editing** - Modify properties with text processing tools
3. **Diff/Merge** - Compare asset versions using standard diff tools
4. **Scripted Modifications** - Programmatic asset editing via JSON manipulation
5. **Documentation** - Generate asset structure documentation

### Encoding

JSON files are written with UTF-8 encoding to preserve Unicode characters (Chinese, Korean, Japanese text in asset names).
