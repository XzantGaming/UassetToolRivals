# UAssetTool

A CLI tool for parsing, editing, and converting Unreal Engine 5 assets. Built on [UAssetAPI](https://github.com/atenfyr/UAssetAPI) with extensions for Zen/IoStore support, texture handling, and NiagaraSystem editing. **Optimized for Marvel Rivals modding.**

## Features

- **IoStore Mod Creation** - Create IoStore mod bundles (`.utoc`/`.ucas`/`.pak`) from legacy assets or PAK files
- **IoStore Extraction** - Extract game assets from IoStore containers to legacy `.uasset`/`.uexp`/`.ubulk`/`.uptnl` format
- **PAK Extraction** - Extract assets from legacy PAK files (Oodle, Zlib, Zstd, AES encryption)
- **JSON Conversion** - Export `.uasset` to JSON and back for easy property editing
- **Texture Injection** - Inject PNG/TGA/DDS images into Texture2D assets with BC1/BC3/BC5/BC7 compression and mipmap generation
- **Texture Extraction** - Extract Texture2D assets to PNG/TGA/DDS/BMP, including full-resolution mips from OptionalBulkData
- **NiagaraSystem Editing** - Modify particle effect colors with structured ShaderLUT and ArrayColor parsing
- **MaterialTag Injection** - Auto-inject per-slot gameplay tags from `MaterialTagAssetUserData` during mod creation
- **StaticMesh Support** - ScreenSize expansion, unversioned property conversion, NavCollision handling
- **Blueprint Analysis** - Scan ChildBP assets for IsEnemy parameter redirects
- **IoStore Inspection** - List packages and chunk types in IoStore containers
- **GUI Backend** - JSON stdin/stdout API for frontend integration

---

## Installation

### Download (Recommended)

Grab the latest self-contained executable from [Releases](https://github.com/XzantGaming/UassetToolRivals/releases). No .NET SDK required.

### Check Version

```bash
UAssetTool version
# or
UAssetTool --version
```

### Build from Source

**Prerequisites:** .NET 8.0 SDK

```bash
git clone https://github.com/XzantGaming/UassetToolRivals.git
cd UassetToolRivals
dotnet build -c Release
```

**Publish self-contained executable:**
```bash
# Windows
dotnet publish src/UAssetTool/UAssetTool.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish

# Linux
dotnet publish src/UAssetTool/UAssetTool.csproj -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o publish
```

---

## Quick Start: Creating a Mod

The most common workflow for Marvel Rivals modding:

```bash
# 1. From a legacy .pak mod file (most common)
UAssetTool create_mod_iostore "output/MyMod_9999999_P" "my_mod.pak" --usmap "game.usmap"

# 2. From loose .uasset files
UAssetTool create_mod_iostore "output/MyMod_9999999_P" \
    "Marvel/Content/Marvel/Characters/1014/Meshes/SK_1014_1014001.uasset" --usmap "game.usmap"

# 3. From a directory of assets
UAssetTool create_mod_iostore "output/MyMod_9999999_P" "Marvel/Content/Marvel/Characters/1014/" --usmap "game.usmap"
```

Then copy the three output files (`.utoc`, `.ucas`, `.pak`) to:
```
MarvelRivals/MarvelGame/Marvel/Content/Paks/~mods/
```

---

## Building a Legacy PAK

If you need to create a legacy `.pak` file (e.g. for distribution or as input to `create_mod_iostore`):

```bash
# Create a PAK from a directory of cooked assets
UAssetTool create_pak <output.pak> <input_directory> [options]

# Create a PAK from specific files
UAssetTool create_pak <output.pak> <file1.uasset> <file2.uasset> ...
```

**Options:**
- `--mount-point <path>` - Mount point prefix (default: `../../../`)
- `--compress` - Enable Oodle compression
- `--no-compress` - No compression (default)

**Important:** Assets inside the PAK must follow the game's directory structure relative to the mount point. For example:
```
Marvel/Content/Marvel/Characters/1014/Meshes/SK_1014_1014001.uasset
Marvel/Content/Marvel/Characters/1014/Meshes/SK_1014_1014001.uexp
Marvel/Content/Marvel/Characters/1014/Meshes/SK_1014_1014001.ubulk
```

---

## IoStore Mod Creation

Create IoStore mod bundles from legacy assets for Marvel Rivals:

```bash
# From a .pak file (extracts and converts automatically)
UAssetTool create_mod_iostore "output/MyMod" "my_legacy_mod.pak"

# From individual assets
UAssetTool create_mod_iostore "output/MyMod" \
    "Content/Marvel/Characters/1014/Meshes/SK_1014_1014001.uasset"

# From a directory (recursively finds all .uasset files)
UAssetTool create_mod_iostore "output/MyMod" "Content/Marvel/Characters/1014/"

# With obfuscation (protects from FModel extraction)
UAssetTool create_mod_iostore "output/MyMod" --obfuscate "my_mod.pak"

# Without compression (faster, larger files)
UAssetTool create_mod_iostore "output/MyMod" --no-compress "my_mod.pak"
```

**Options:**
- `--usmap <path>` - Path to `.usmap` mappings file (needed for StaticMesh unversioned conversion)
- `--mount-point <path>` - Mount point (default: `../../../`)
- `--game-path <prefix>` - Game path prefix (default: `Marvel/Content/`)
- `--compress` / `--no-compress` - Toggle Oodle compression (default: enabled)
- `--obfuscate` - Encrypt with game's AES key to prevent FModel extraction
- `--pak-aes <hex>` - AES key for decrypting encrypted input `.pak` files
- `--no-material-tags` - Disable automatic MaterialTag injection

**Output files** (copy all three to `~mods`):
- `<output>.utoc` - Table of Contents
- `<output>.ucas` - Container Archive Store
- `<output>.pak` - Companion PAK with chunk names

**Marvel Rivals auto-fixes applied during conversion:**
- **SkeletalMesh**: FGameplayTagContainer padding, MaterialTag injection
- **StaticMesh**: ScreenSize expansion (64 → 128 bytes), versioned → unversioned property conversion, NavCollision CookedSerialSize override
- **StringTable**: FGameplayTagContainer padding

---

## IoStore Extraction

Extract game assets from IoStore containers (`.utoc`/`.ucas`) to legacy format:

```bash
# Extract specific assets by filter
UAssetTool extract_iostore_legacy "C:/Game/Paks" "output_dir" --filter SK_1014 SK_1057

# Extract with dependencies
UAssetTool extract_iostore_legacy "C:/Game/Paks" "output_dir" --filter Characters/1057 --with-deps
```

**Options:**
- `--filter <patterns...>` - Extract packages matching patterns (space-separated, OR logic)
- `--with-deps` - Also extract imported/referenced packages
- `--mod <path>` - Extract from mod containers (see below)
- `--script-objects <path>` - Path to ScriptObjects.bin for import resolution
- `--global <path>` - Path to global.utoc for script objects
- `--container <path>` - Additional container for cross-package imports

**Output files per package:**
- `.uasset` - Package header (name map, imports, exports, data resources)
- `.uexp` - Export data (properties + binary extras)
- `.ubulk` - External bulk data (texture mips 1-N, mesh LOD data)
- `.uptnl` - Optional bulk data (highest-resolution texture mips, stored in separate IoStore containers)
- `.m.ubulk` - Memory-mapped bulk data (if present)

### Mod Extraction

```bash
# Extract all packages from a mod
UAssetTool extract_iostore_legacy "C:/Game/Paks" "output_dir" --mod "path/to/mod.utoc"

# Extract from mod with filter
UAssetTool extract_iostore_legacy "C:/Game/Paks" "output_dir" --mod "mod.utoc" --filter SK_1014

# Extract from all mods in a directory
UAssetTool extract_iostore_legacy "C:/Game/Paks" "output_dir" --mod "C:/Mods/"

# Extract mod + game dependencies
UAssetTool extract_iostore_legacy "C:/Game/Paks" "output_dir" --mod "mod.utoc" --with-deps
```

| Input | Behavior |
|-------|----------|
| `--mod "path/to/mod.utoc"` | Loads single mod container |
| `--mod "path/to/mods/"` | Loads all `.utoc` files in directory |
| `--mod "mod1.utoc" "mod2.utoc"` | Loads multiple containers |

Game containers use AES decryption; mod containers are loaded unencrypted.

---

## PAK Extraction

```bash
# Extract from PAK
UAssetTool extract_pak <pak_path> <output_dir>

# List files without extracting
UAssetTool extract_pak <pak_path> <output_dir> --list

# With AES decryption
UAssetTool extract_pak <pak_path> <output_dir> --aes "YOUR_AES_KEY_HEX"

# Filter by pattern
UAssetTool extract_pak mod.pak output --filter SK_1036 MI_Body
```

**Options:**
- `--aes <key>` - AES decryption key (64 hex chars)
- `--filter <patterns...>` - Only extract matching files (OR logic)
- `--list` - List files only

Supports PAK v11 (UE5), Oodle/Zlib/Zstd compression, encrypted and unencrypted.

---

## Texture Handling

### Texture Injection

Inject images into existing Texture2D `.uasset` files:

```bash
# Inject a PNG into a texture asset
UAssetTool inject_texture <base_uasset> <image_file> <output_uasset> [options]

# Examples
UAssetTool inject_texture T_Skin_D.uasset my_skin.png T_Skin_D_modded.uasset
UAssetTool inject_texture T_Skin_D.uasset my_skin.dds output.uasset --format BC1
UAssetTool inject_texture T_Skin_D.uasset my_skin.tga output.uasset --no-mips
```

**Options:**
- `--format <fmt>` - Compression format: `BC7` (default), `BC3`, `BC1`, `BC5`, `BC4`, `BGRA8`
- `--no-mips` - Don't generate mipmaps (single mip only)
- `--usmap <path>` - Usmap mappings for unversioned assets

**Supported input formats:** PNG, TGA, DDS, BMP, JPEG

The injector reads the base `.uasset` to preserve all metadata (pixel format name, texture settings), replaces the pixel data with the new image compressed to the specified format, and generates a full mipchain.

### Texture Extraction

Extract Texture2D assets to common image formats:

```bash
# Extract to PNG (default)
UAssetTool extract_texture <uasset_path> <output_path> [options]

# Examples
UAssetTool extract_texture T_Skin_D.uasset skin.png --usmap game.usmap
UAssetTool extract_texture T_Skin_D.uasset skin.dds --format DDS
UAssetTool extract_texture T_Skin_D.uasset mip2.png --mip 2
```

**Options:**
- `--format <fmt>` - Output format: `PNG` (default), `TGA`, `DDS`, `BMP`
- `--mip <index>` - Mip level to extract (0 = largest, default: 0)
- `--usmap <path>` - Usmap mappings for unversioned assets

**Supported pixel formats:** PF_DXT1 (BC1), PF_DXT5 (BC3), PF_BC5 (BC5), PF_BC7, PF_B8G8R8A8

The extractor reads pixel data from `.uexp` (inline mips), `.ubulk` (external bulk), and `.uptnl` (optional/high-res mips). If the requested mip has no data, it falls back to the next available mip.

### Batch Texture Injection

Inject textures in bulk by matching image filenames to `.uasset` filenames:

```bash
UAssetTool batch_inject_texture <uasset_dir> <image_dir> <output_dir> [options]
```

Place your replacement images (PNG/TGA/DDS/BMP) in a folder using the **same filename stem** as the target `.uasset`. For example:
- `T_1053_Skin_D.png` matches `T_1053_Skin_D.uasset`
- `T_1053_Hair_D.tga` matches `T_1053_Hair_D.uasset`

```bash
# Example: inject all replacement textures
UAssetTool batch_inject_texture extracted/Textures/ my_skins/ output/ --usmap game.usmap --format BC7

# Without mipmaps
UAssetTool batch_inject_texture extracted/Textures/ my_skins/ output/ --usmap game.usmap --no-mips
```

Both directories are searched recursively. The output preserves the directory structure from `uasset_dir`. Images with no matching `.uasset` are reported as skipped.

### Batch Texture Extraction

Extract all Texture2D `.uasset` files in a directory to images:

```bash
UAssetTool batch_extract_texture <uasset_dir> <output_dir> [options]

# Example: extract all textures to PNG
UAssetTool batch_extract_texture extracted/Textures/ png_output/ --usmap game.usmap

# Extract as DDS
UAssetTool batch_extract_texture extracted/Textures/ dds_output/ --format DDS --usmap game.usmap
```

Non-Texture2D `.uasset` files are automatically skipped. Directory structure is preserved in the output.

### Texture Workflow

```bash
# 1. Extract textures from the game
UAssetTool extract_iostore_legacy "C:/Game/Paks" extracted --filter T_1053_Skin_D

# 2. Extract to PNG for editing (full resolution including OptionalBulkData mips)
UAssetTool extract_texture extracted/.../T_1053_Skin_D.uasset original.png --usmap game.usmap

# 3. Edit in your image editor, then inject back (single file)
UAssetTool inject_texture extracted/.../T_1053_Skin_D.uasset edited.png output/T_1053_Skin_D.uasset --usmap game.usmap

# 3b. Or batch inject: put edited PNGs in a folder with matching names
UAssetTool batch_inject_texture extracted/ my_edited_textures/ output/ --usmap game.usmap

# 4. Create mod
UAssetTool create_mod_iostore "mods/MySkin" output/
```

---

## JSON Conversion

```bash
# Single file
UAssetTool to_json <uasset_path> [usmap_path] [output_dir]

# Batch (all .uasset in directory, preserves structure)
UAssetTool to_json <directory> [usmap_path] [output_dir]

# JSON back to uasset
UAssetTool from_json <json_path> <output_uasset_path> [usmap_path]
```

---

## NiagaraSystem Editing

Edit particle effect colors in NiagaraSystem assets:

```bash
# Inspect color curves (outputs JSON)
UAssetTool niagara_details <asset_path> --usmap <usmap_path>

# Edit colors via JSON edits
UAssetTool niagara_edit <asset_path> --usmap <usmap_path> --output <output_path> --edits <edits_json>

# Or load edits from a file
UAssetTool niagara_edit <asset_path> --usmap <usmap_path> --output <output_path> --edits-file <edits.json>

# Deep audit of all color properties in an NS asset
UAssetTool niagara_audit <asset_path> [usmap_path]
```

### Edits JSON Format

The `--edits` parameter accepts a JSON array of edit operations:

```json
[
  {
    "exportIndex": 4,
    "flatLut": [0, 10, 0, 1, 0, 10, 0, 1, ...]
  }
]
```

Each edit targets a specific export by index and provides a flat array of RGBA float values for the color LUT.

### Workflow

```bash
# 1. Extract NS assets from game
UAssetTool extract_iostore_legacy "C:/Game/Paks" "output" --filter "VFX/Particles"

# 2. Inspect color curves
UAssetTool niagara_details "output/.../NS_Effect.uasset" --usmap "mappings.usmap"

# 3. Edit colors (using JSON edits)
UAssetTool niagara_edit "output/.../NS_Effect.uasset" --usmap "mappings.usmap" --output "output/.../NS_Effect.uasset" --edits '[{"exportIndex":4,"flatLut":[0,10,0,1]}]'

# 4. Create mod bundle
UAssetTool create_mod_iostore "mods/GreenFX" "output/.../NS_Effect.uasset"
```

---

## IoStore Inspection

```bash
# List all packages in an IoStore with chunk types
UAssetTool list_iostore <utoc_path_or_dir> [--aes <key>] [--filter <pattern>]
```

Shows each package and its chunk types (ExportBundleData, BulkData, OptionalBulkData, MemoryMappedBulkData). Useful for verifying which packages have external texture data.

---

## Other Commands

```bash
# Asset inspection
UAssetTool detect <uasset_path> [usmap_path]
UAssetTool dump <uasset_path> <usmap_path>
UAssetTool fix <uasset_path> [usmap_path]
UAssetTool batch_detect <directory> [usmap_path]
UAssetTool skeletal_mesh_info <uasset_path> <usmap_path>

# Zen conversion (single file)
UAssetTool to_zen <uasset_path> [usmap_path] [--no-material-tags]

# Zen inspection
UAssetTool inspect_zen <zen_asset_path>

# IoStore utilities
UAssetTool extract_iostore <utoc_path> <output_dir> [--aes <key>] [--package <name>]
UAssetTool extract_script_objects <paks_path> <output_file>
UAssetTool recompress_iostore <utoc_path>
UAssetTool is_iostore_compressed <utoc_path>
UAssetTool is_iostore_encrypted <utoc_path>
UAssetTool clone_mod_iostore <utoc_path> <output_base>
UAssetTool cityhash <path_string>
UAssetTool dump_zen_from_game <paks_path> <package_path> [output_file]

# Additional IoStore bundle creation
UAssetTool create_iostore_bundle <output> <files...>
UAssetTool create_companion_pak <output.pak> <files...>
```

---

## Interactive JSON Mode

Run without arguments for a JSON stdin/stdout API (for GUI frontends):

```bash
UAssetTool
```

<details>
<summary>Available JSON Actions</summary>

**Asset Structure**
```json
{"action": "get_asset_summary", "file_path": "...", "usmap_path": "..."}
{"action": "get_name_map", "file_path": "..."}
{"action": "get_imports", "file_path": "..."}
{"action": "get_exports", "file_path": "..."}
{"action": "get_export_properties", "file_path": "...", "export_index": 0}
{"action": "get_export_raw_data", "file_path": "...", "export_index": 0}
```

**Property Editing**
```json
{"action": "set_property_value", "file_path": "...", "export_index": 0, "property_path": "MyProp", "property_value": 123}
{"action": "add_property", "file_path": "...", "export_index": 0, "property_name": "NewProp", "property_type": "int", "property_value": 42}
{"action": "remove_property", "file_path": "...", "export_index": 0, "property_path": "OldProp"}
```

**Save/Export**
```json
{"action": "save_asset", "file_path": "...", "output_path": "..."}
{"action": "export_to_json", "file_path": "..."}
{"action": "import_from_json", "file_path": "...", "json_data": "..."}
```

**JSON Conversion (single + batch parallel)**
```json
{"action": "to_json", "file_path": "...", "usmap_path": "...", "output_path": "..."}
{"action": "to_json", "file_paths": [...], "usmap_path": "...", "output_path": "output_dir/"}
{"action": "from_json", "file_path": "...", "output_path": "...", "usmap_path": "..."}
{"action": "from_json", "file_paths": [...], "output_path": "output_dir/", "usmap_path": "..."}
```
When `file_paths` (array) is provided instead of `file_path`, processing is automatic parallel via `Parallel.ForEach`. The `batch_to_json` and `batch_from_json` action names are also accepted as aliases.

**Asset Inspection**
```json
{"action": "dump", "file_path": "...", "usmap_path": "..."}
{"action": "cityhash", "file_path": "path/string/to/hash"}
{"action": "inspect_zen", "file_path": "..."}
{"action": "clone_mod_iostore", "file_path": "...", "output_path": "..."}
```

**Texture**
```json
{"action": "get_texture_info", "file_path": "..."}
{"action": "strip_mipmaps_native", "file_path": "...", "usmap_path": "..."}
{"action": "has_inline_texture_data", "file_path": "...", "usmap_path": "..."}
{"action": "batch_strip_mipmaps_native", "file_paths": [...], "usmap_path": "..."}
{"action": "batch_has_inline_texture_data", "file_paths": [...], "usmap_path": "..."}
```

**Detection**
```json
{"action": "detect_texture", "file_path": "..."}
{"action": "detect_static_mesh", "file_path": "..."}
{"action": "detect_skeletal_mesh", "file_path": "..."}
{"action": "detect_blueprint", "file_path": "..."}
```

**Mod Creation**
```json
{"action": "create_mod_iostore", "output_path": "...", "input_dir": "...", "usmap_path": "...", "obfuscate": true}
{"action": "clone_mod_iostore", "file_path": "...", "output_path": "..."}
```

**Mesh**
```json
{"action": "patch_mesh", "file_path": "...", "uexp_path": "..."}
{"action": "get_mesh_info", "file_path": "...", "usmap_path": "..."}
{"action": "fix_serialize_size", "file_path": "...", "usmap_path": "..."}
{"action": "skeletal_mesh_info", "file_path": "...", "usmap_path": "..."}
```

**Zen Conversion**
```json
{"action": "convert_to_zen", "file_path": "...", "usmap_path": "..."}
{"action": "convert_from_zen", "file_path": "...", "usmap_path": "..."}
{"action": "inspect_zen", "file_path": "..."}
```

**IoStore (Additional)**
```json
{"action": "list_iostore_files", "file_path": "...", "aes_key": "..."}
{"action": "extract_iostore", "file_path": "...", "output_path": "...", "aes_key": "..."}
{"action": "is_iostore_encrypted", "file_path": "..."}
{"action": "recompress_iostore", "file_path": "..."}
```

</details>

---

## Project Structure

```
UassetToolRivals/
├── UAssetTool.sln
├── README.md
├── TECHNICAL_ANALYSIS.md     # Detailed format documentation
├── LICENSE
└── src/
    ├── UAssetTool/
    │   ├── Program.cs           # CLI entry point & command routing
    │   ├── UAssetTool.csproj
    │   ├── NiagaraService.cs    # Niagara particle editing
    │   ├── IoStore/             # IoStore/PAK reading & writing
    │   │   ├── IoStoreReader.cs
    │   │   ├── IoStoreWriter.cs
    │   │   ├── PakReader.cs
    │   │   ├── PakWriter.cs
    │   │   └── OodleCompression.cs
    │   ├── Texture/             # Texture injection & extraction
    │   │   ├── TextureInjector.cs   # PNG/TGA/DDS → uasset (BC1-BC7)
    │   │   └── TextureExtractor.cs  # uasset → PNG/TGA/DDS/BMP
    │   └── ZenPackage/          # Zen format conversion & extraction
    │       ├── ZenConverter.cs          # Legacy → Zen conversion
    │       ├── ZenToLegacyConverter.cs  # Zen → Legacy extraction
    │       ├── FZenPackageContext.cs    # Multi-container IoStore context
    │       ├── MaterialTagReader.cs     # MaterialTag auto-injection
    │       └── ScriptObjectsDatabase.cs # Engine class hash resolution
    └── UAssetAPI/               # Core UAsset parsing (modified fork)
        └── ExportTypes/
            └── Texture/         # Texture2D, FByteBulkData
```

## Dependencies

- [UAssetAPI](https://github.com/atenfyr/UAssetAPI) - Core asset parsing (included, modified)
- [Newtonsoft.Json](https://www.nuget.org/packages/Newtonsoft.Json) - JSON serialization
- [Blake3](https://www.nuget.org/packages/Blake3) - Hashing for IoStore
- [ZstdSharp](https://www.nuget.org/packages/ZstdSharp.Port) - Compression
- [BCnEncoder.Net](https://www.nuget.org/packages/BCnEncoder.Net) - BC1/BC3/BC5/BC7 texture encoding & decoding
- [SixLabors.ImageSharp](https://www.nuget.org/packages/SixLabors.ImageSharp) - Image loading, saving (PNG, TGA, BMP)
- [Pfim](https://www.nuget.org/packages/Pfim) - DDS/TGA image loading

## License

MIT License - See [LICENSE](LICENSE) for details.

## Acknowledgments

- [atenfyr/UAssetAPI](https://github.com/atenfyr/UAssetAPI) - Foundation for asset parsing
- [trumank/retoc](https://github.com/trumank/retoc) - Reference for Zen/IoStore format
