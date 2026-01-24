# UAssetTool

A unified command-line tool for parsing, editing, and converting Unreal Engine assets. Built on top of [UAssetAPI](https://github.com/atenfyr/UAssetAPI) with custom extensions for texture handling and Zen/IoStore support. **Specifically optimized for Marvel Rivals modding.**

## Features

- **Asset Detection** - Detect asset types (StaticMesh, SkeletalMesh, Texture2D, Blueprint, MaterialInstance)
- **Property Editing** - Read and modify asset properties via JSON API
- **Texture Operations** - Strip mipmaps, get texture info, detect inline data
- **Mesh Operations** - Fix SerializeSize mismatches, SkeletalMesh/StaticMesh material padding
- **Zen/IoStore Support** - Convert between legacy (.uasset/.uexp) and Zen formats
- **IoStore Creation** - Create IoStore mod bundles (.utoc/.ucas/.pak) for game injection
- **IoStore Extraction** - Extract assets from game IoStore containers with dependency resolution
- **Marvel Rivals Support** - Game-specific fixes for FGameplayTagContainer, material slots, and asset serialization
- **NiagaraSystem Editing** - Modify particle effect colors with structured parsing
- **GUI Backend** - JSON stdin/stdout API for frontend integration

## Installation

### Prerequisites
- .NET 8.0 SDK

### Build from Source
```bash
git clone https://github.com/XzantGaming/UassetToolRivals.git
cd UassetToolRivals
dotnet build -c Release
```

### Publish Single Executable
```bash
# Windows
dotnet publish -c Release -r win-x64 --self-contained

# Linux  
dotnet publish -c Release -r linux-x64 --self-contained

# macOS
dotnet publish -c Release -r osx-x64 --self-contained
```

## Usage

### CLI Mode

```bash
# Detect asset type
UAssetTool detect <uasset_path> [usmap_path]

# Dump asset info
UAssetTool dump <uasset_path> <usmap_path>

# Fix mesh SerializeSize
UAssetTool fix <uasset_path> [usmap_path]

# Batch detect assets
UAssetTool batch_detect <directory> [usmap_path]

# Convert legacy to Zen format
UAssetTool to_zen <uasset_path> [usmap_path]
```

### PAK Extraction

```bash
# Extract assets from legacy PAK file
UAssetTool extract_pak <pak_path> <output_dir> [options]

# List files in PAK without extracting
UAssetTool extract_pak <pak_path> <output_dir> --list

# Extract with AES decryption key
UAssetTool extract_pak <pak_path> <output_dir> --aes "YOUR_AES_KEY"

# Extract files matching patterns (space-separated, OR logic)
UAssetTool extract_pak mod.pak output --filter SK_1036 MI_Body
UAssetTool extract_pak mod.pak output --filter Meshes Textures Materials
```

**Options:**
- `--aes <key>` - AES decryption key (hex string, 64 chars)
- `--filter <patterns...>` - Only extract files matching patterns (space-separated, OR logic)
- `--list` - List files only, don't extract

Supports:
- PAK version 11 (UE5 format)
- Oodle, Zlib, Zstd compression
- Encrypted and unencrypted PAKs
- Multi-block compressed files

### IoStore Commands

```bash
# Extract assets from game IoStore to legacy format
UAssetTool extract_iostore_legacy <utoc_path> <output_dir> [options]

# Create IoStore mod bundle from legacy assets
UAssetTool create_mod_iostore <output_base> [options] <uasset_files...>

# Inspect Zen package structure
UAssetTool inspect_zen <ucas_path>

# Extract script objects database
UAssetTool extract_script_objects <paks_path> <output_file>
```

### Interactive JSON Mode

Run without arguments to enter interactive mode. Send JSON requests via stdin, receive responses via stdout.

```bash
UAssetTool
```

#### Available Actions

**Asset Structure (Read)**
```json
{"action": "get_asset_summary", "file_path": "path/to/asset.uasset", "usmap_path": "path/to/mappings.usmap"}
{"action": "get_name_map", "file_path": "path/to/asset.uasset"}
{"action": "get_imports", "file_path": "path/to/asset.uasset"}
{"action": "get_exports", "file_path": "path/to/asset.uasset"}
{"action": "get_export_properties", "file_path": "path/to/asset.uasset", "export_index": 0}
{"action": "get_export_raw_data", "file_path": "path/to/asset.uasset", "export_index": 0}
```

**Property Editing**
```json
{"action": "set_property_value", "file_path": "...", "export_index": 0, "property_path": "MyProperty", "property_value": 123}
{"action": "add_property", "file_path": "...", "export_index": 0, "property_name": "NewProp", "property_type": "int", "property_value": 42}
{"action": "remove_property", "file_path": "...", "export_index": 0, "property_path": "OldProp"}
```

**Save/Export**
```json
{"action": "save_asset", "file_path": "...", "output_path": "path/to/output.uasset"}
{"action": "export_to_json", "file_path": "..."}
{"action": "import_from_json", "file_path": "...", "json_data": "..."}
```

**Texture Operations**
```json
{"action": "get_texture_info", "file_path": "path/to/texture.uasset"}
{"action": "strip_mipmaps_native", "file_path": "...", "usmap_path": "..."}
{"action": "has_inline_texture_data", "file_path": "...", "usmap_path": "..."}
```

**Detection**
```json
{"action": "detect_texture", "file_path": "..."}
{"action": "detect_static_mesh", "file_path": "..."}
{"action": "detect_skeletal_mesh", "file_path": "..."}
{"action": "detect_blueprint", "file_path": "..."}
```

**Zen Conversion**
```json
{"action": "convert_to_zen", "file_path": "...", "usmap_path": "..."}
{"action": "convert_from_zen", "file_path": "...", "usmap_path": "..."}
```

## IoStore Extraction

Extract assets from UE5 IoStore containers (`.utoc`/`.ucas`) to legacy format:

```bash
# Extract specific assets by filter patterns (space-separated, OR logic)
UAssetTool extract_iostore_legacy "C:/Game/Paks" "output_dir" --filter SK_1014 SK_1057 SK_1036

# Extract with dependencies
UAssetTool extract_iostore_legacy "C:/Game/Paks" "output_dir" --filter Characters/1057 --with-deps

# Extract multiple character meshes
UAssetTool extract_iostore_legacy "C:/Game/Paks" "output_dir" --filter SK_1014_1014001 SK_1057_1057001
```

**Arguments:**
- `<paks_directory>` - Path to game's Paks directory (loads all .utoc files, top-level only)
- `<output_dir>` - Output directory for extracted assets

**Options:**
- `--filter <patterns...>` - Only extract packages matching patterns (space-separated, OR logic)
- `--with-deps` - Also extract imported/referenced packages
- `--mod <path>` - Extract from mod containers (see [Mod Extraction](#mod-extraction) below)
- `--script-objects <path>` - Path to ScriptObjects.bin for import resolution
- `--global <path>` - Path to global.utoc for script objects
- `--container <path>` - Additional container to load for cross-package imports

The tool automatically:
- Loads all game containers for cross-container dependency resolution
- Resolves import names correctly using script objects database
- Handles encrypted containers with AES key
- Extracts `.uasset`, `.uexp`, and `.ubulk` files
- Converts Zen format to legacy format during extraction

### Mod Extraction

Extract assets from modded IoStore containers while using game files for import resolution:

```bash
# Extract all packages from a mod file
UAssetTool extract_iostore_legacy "C:/Game/Paks" "output_dir" --mod "C:/Mods/my_mod.utoc"

# Extract from mod with filter
UAssetTool extract_iostore_legacy "C:/Game/Paks" "output_dir" --mod "C:/Mods/my_mod.utoc" --filter SK_1014

# Extract from all mods in a directory
UAssetTool extract_iostore_legacy "C:/Game/Paks" "output_dir" --mod "C:/Mods/"

# Extract mod assets with their dependencies from game files
UAssetTool extract_iostore_legacy "C:/Game/Paks" "output_dir" --mod "C:/Mods/my_mod.utoc" --with-deps

# Multiple mod paths
UAssetTool extract_iostore_legacy "C:/Game/Paks" "output_dir" --mod "C:/Mods/mod1.utoc" "C:/Mods/mod2.utoc"
```

**How it works:**
1. Game containers are loaded first (encrypted, uses AES key) for import resolution
2. Mod containers are loaded with **priority** (unencrypted) - mod packages override game packages
3. When `--mod` is specified without `--filter`, extracts **all packages** from mod containers
4. When `--with-deps` is used, dependencies are resolved from game containers

**Path handling:**
| Input | Behavior |
|-------|----------|
| `--mod "path/to/mod.utoc"` | Loads single mod container |
| `--mod "path/to/mods/"` | Loads all `.utoc` files in directory |
| `--mod "mod1.utoc" "mod2.utoc"` | Loads multiple specified containers |

**Note:** Mod containers are loaded without encryption (mods are not encrypted), while game containers use the AES key for decryption.

## IoStore Creation (Mod Bundling)

Create IoStore mod bundles from legacy assets for injection into Marvel Rivals:

```bash
# Create mod bundle from single asset
UAssetTool create_mod_iostore "output/MyMod" \
    --usmap "path/to/mappings.usmap" \
    "Content/Marvel/Characters/1014/Meshes/SK_1014_1014001.uasset"

# Create mod bundle from multiple assets
UAssetTool create_mod_iostore "output/MyMod" \
    --usmap "path/to/mappings.usmap" \
    "Content/Marvel/Textures/T_MyTexture.uasset" \
    "Content/Marvel/Materials/MI_MyMaterial.uasset"

# Create without compression (faster, larger files)
UAssetTool create_mod_iostore "output/MyMod" \
    --usmap "path/to/mappings.usmap" \
    --no-compress \
    "Content/Marvel/Characters/1014/Meshes/SK_1014_1014001.uasset"
```

**Options:**
- `--usmap <path>` - Path to .usmap mappings file (required)
- `--aes <key>` - AES encryption key (optional)
- `--no-compress` - Disable Oodle compression (faster creation)

**Output Files:**
- `<output_base>.utoc` - Table of Contents
- `<output_base>.ucas` - Container Archive Store (asset data)
- `<output_base>.pak` - Companion PAK with chunk names

**Installation:**
Copy all three files to your game's `~mods` folder:
```
MarvelRivals/MarvelGame/Marvel/Content/Paks/~mods/
├── MyMod.utoc
├── MyMod.ucas
└── MyMod.pak
```

## NiagaraSystem Editing

Edit particle effect colors in NiagaraSystem assets with frontend-friendly JSON API:

### CLI Commands

```bash
# List all NS files with metadata
UAssetTool niagara_list <directory> [usmap_path]

# Get detailed color curve info for a file (samples only)
UAssetTool niagara_details <asset_path> [usmap_path]

# Get ALL values (not just samples) - useful for frontend editors
UAssetTool niagara_details <asset_path> [usmap_path] --full

# Edit colors (simple mode)
UAssetTool niagara_edit <asset_path> <R> <G> <B> [A] [options...] [usmap_path]

# Edit colors (JSON request mode)
UAssetTool niagara_edit '{"assetPath":"...","r":0,"g":10,"b":0,"a":1}' [usmap_path]

# Batch modify all colors in directory
UAssetTool modify_colors <directory> <usmap_path> [R G B A]
```

### Selective LUT Targeting

NiagaraSystem files contain multiple ShaderLUT arrays that control different aspects of particle effects. Not all LUTs control visible colors - some control timing, scale, opacity gradients, etc. Use selective targeting to modify only the LUTs you want:

**CLI Options:**
```bash
# Only modify exports with "Glow" in the name
UAssetTool niagara_edit asset.uasset 0 10 0 1 --export-name Glow

# Only modify specific export by index
UAssetTool niagara_edit asset.uasset 0 10 0 1 --export-index 5

# Only modify colors 0-10 in the LUT (useful for gradients)
UAssetTool niagara_edit asset.uasset 0 10 0 1 --color-range 0 10

# Only modify a single color at index 0
UAssetTool niagara_edit asset.uasset 0 10 0 1 --color-index 0

# Only modify RGB channels, leave Alpha unchanged
UAssetTool niagara_edit asset.uasset 0 10 0 1 --channels rgb

# Combine multiple filters
UAssetTool niagara_edit asset.uasset 0 10 0 1 --export-name Color --color-range 0 10 --channels rgb
```

**JSON Request Options:**
```json
{
  "assetPath": "path/to/NS_Effect.uasset",
  "r": 0, "g": 10, "b": 0, "a": 1,
  "exportNameFilter": "ColorCurve_Glow",
  "exportIndex": 5,
  "colorIndexStart": 0,
  "colorIndexEnd": 10,
  "modifyR": true,
  "modifyG": true,
  "modifyB": false,
  "modifyA": false
}
```

| Option | CLI Flag | Description |
|--------|----------|-------------|
| `exportIndex` | `--export-index <n>` | Target specific export by index |
| `exportNameFilter` | `--export-name <pattern>` | Filter exports by name (case-insensitive) |
| `colorIndex` | `--color-index <n>` | Modify only this color index |
| `colorIndexStart` | `--color-range <start> <end>` | Start of color range (inclusive) |
| `colorIndexEnd` | (part of --color-range) | End of color range (inclusive) |
| `modifyR/G/B/A` | `--channels <rgba>` | Which channels to modify |

### JSON Response Examples

**List files (`niagara_list`):**
```json
{
  "success": true,
  "totalFiles": 3,
  "files": [
    {
      "path": "E:\\temp\\NS_104821_Hit_01_CB.uasset",
      "fileName": "NS_104821_Hit_01_CB.uasset",
      "colorCurveCount": 20,
      "totalColorCount": 2416
    }
  ]
}
```

**File details (`niagara_details`):**
```json
{
  "success": true,
  "totalExports": 269,
  "colorCurveCount": 12,
  "totalColorCount": 1536,
  "colorCurves": [
    {"exportIndex": 0, "exportName": "NiagaraDataInterfaceColorCurve", "colorCount": 128, "sampleColors": [...]}
  ],
  "floatCurveCount": 40,
  "totalFloatCount": 2120,
  "floatCurves": [
    {"exportIndex": 31, "exportName": "NiagaraDataInterfaceCurve", "valueCount": 106, "sampleValues": [...]}
  ],
  "vector3CurveCount": 8,
  "totalVector3Count": 1024,
  "vector3Curves": [
    {"exportIndex": 22, "exportName": "NiagaraDataInterfaceVectorCurve", "valueCount": 128, "sampleValues": [...]}
  ],
  "arrayColorCount": 4,
  "totalArrayColorValues": 12,
  "arrayColors": [
    {"exportIndex": 0, "exportName": "NiagaraDataInterfaceArrayColor", "colorCount": 3, "sampleColors": [...]}
  ],
  "arrayFloatCount": 10,
  "totalArrayFloatValues": 30,
  "arrayFloats": [...]
}
```

**Edit requests by curve type:**

*ColorCurve (RGBA - 4 components):*
```json
{
  "assetPath": "path/to/NS_Effect.uasset",
  "r": 0, "g": 10, "b": 0, "a": 1,
  "exportIndex": 3,
  "colorIndex": 0,
  "modifyR": true, "modifyG": true, "modifyB": true, "modifyA": true
}
```

*Vector3Curve (XYZ/RGB - 3 components):*
```json
{
  "assetPath": "path/to/NS_Effect.uasset",
  "x": 0, "y": 10, "z": 0,
  "exportIndex": 22,
  "valueIndex": 0,
  "modifyX": true, "modifyY": true, "modifyZ": true
}
```

*Vector2DCurve (XY - 2 components):*
```json
{
  "assetPath": "path/to/NS_Effect.uasset",
  "x": 1.5, "y": 2.0,
  "exportIndex": 79,
  "valueIndexStart": 0, "valueIndexEnd": 31,
  "modifyX": true, "modifyY": false
}
```

*FloatCurve (single value):*
```json
{
  "assetPath": "path/to/NS_Effect.uasset",
  "value": 0.5,
  "exportIndex": 31,
  "valueIndex": 0
}
```

### Workflow Example

```bash
# 1. Extract NS assets from game
UAssetTool extract_iostore_legacy "C:/Game/Paks" "output" --filter "VFX/Particles"

# 2. List available NS files
UAssetTool niagara_list "output/Content" "mappings.usmap"

# 3. Inspect specific file
UAssetTool niagara_details "output/Content/.../NS_Effect.uasset" "mappings.usmap"

# 4. Modify colors to bright green
UAssetTool niagara_edit "output/Content/.../NS_Effect.uasset" 0 10 0 1 "mappings.usmap"

# 5. Create mod bundle
UAssetTool create_mod_iostore "mods/GreenFX" output/Content/.../NS_Effect.uasset
```

See [docs/NIAGARA_EDITING_GUIDE.md](docs/NIAGARA_EDITING_GUIDE.md) for detailed technical documentation.

## Zen Package Inspection

Inspect the internal structure of Zen packages:

```bash
UAssetTool inspect_zen "path/to/file.ucas"
```

Shows:
- Container version and header information
- Package summary (flags, offsets)
- Name map entries
- Export map with serial offsets and sizes
- Export bundle entries
- Dependency information

## Project Structure

```
UAssetToolStandalone/
├── UAssetTool.sln
├── README.md
├── LICENSE
└── src/
    ├── UAssetTool/
    │   ├── Program.cs
    │   ├── UAssetTool.csproj
    │   ├── IoStore/          # IoStore reading/writing
    │   └── ZenPackage/       # Zen format conversion
    └── UAssetAPI/            # Core UAsset parsing (modified)
```

## Custom Extensions

This fork includes custom extensions beyond standard UAssetAPI:

### Texture Handling
- Marvel Rivals specific texture parsing (LightingGuid, bCooked format)
- Mipmap stripping with correct SerialSize calculation
- FTexturePlatformData detailed parsing
- Automatic mipmap removal for texture mods

### Mesh Handling
- **SkeletalMesh**: FGameplayTagContainer padding (Marvel Rivals specific)
- **StaticMesh**: FStaticMaterial struct handling
- SerialSize recalculation for modified meshes
- Material slot padding for game compatibility

### Zen/IoStore
- Full Zen package header parsing (UE5.3+ NoExportInfo version)
- Cross-container import resolution via script objects database
- Legacy to Zen conversion with correct export bundling
- IoStore bundle creation with Oodle compression
- Export bundle dependency ordering
- Public export hash calculation

### Marvel Rivals Specific
- FGameplayTagContainer serialization (empty container = 4 bytes)
- PackageGuid zeroing for cooked assets
- CookedHeaderSize calculation for preload data
- Script object hash lookup using full paths

## Dependencies

- [UAssetAPI](https://github.com/atenfyr/UAssetAPI) - Core UAsset parsing library (included with modifications)
- [Newtonsoft.Json](https://www.nuget.org/packages/Newtonsoft.Json) - JSON serialization
- [Blake3](https://www.nuget.org/packages/Blake3) - Hashing for IoStore
- [ZstdSharp](https://www.nuget.org/packages/ZstdSharp.Port) - Compression

## License

MIT License - See LICENSE file for details.

## Acknowledgments

- [atenfyr/UAssetAPI](https://github.com/atenfyr/UAssetAPI) - The foundation for all asset parsing
- [trumank/retoc](https://github.com/trumank/retoc) - Reference for Zen/IoStore format understanding
