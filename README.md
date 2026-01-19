# UAssetTool

A unified command-line tool for parsing, editing, and converting Unreal Engine assets. Built on top of [UAssetAPI](https://github.com/atenfyr/UAssetAPI) with custom extensions for texture handling and Zen/IoStore support.

## Features

- **Asset Detection** - Detect asset types (StaticMesh, SkeletalMesh, Texture2D, Blueprint, MaterialInstance)
- **Property Editing** - Read and modify asset properties via JSON API
- **Texture Operations** - Strip mipmaps, get texture info, detect inline data
- **Mesh Operations** - Fix SerializeSize mismatches for static meshes
- **Zen/IoStore Support** - Convert between legacy (.uasset/.uexp) and Zen formats
- **IoStore Extraction** - Extract assets from game IoStore containers with dependency resolution
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

# Extract from IoStore with dependencies
UAssetTool extract_iostore_legacy <utoc_path> <output_dir> --filter "Characters/1057" -deps

# Convert legacy to Zen format
UAssetTool to_zen <uasset_path> [usmap_path]

# Create IoStore bundle
UAssetTool create_mod_iostore <output_base> <uasset_files...>
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
# Extract specific folder with all dependencies
UAssetTool extract_iostore_legacy "path/to/pakchunk0-Windows.utoc" "output_dir" --filter "Characters/1057" -deps

# Extract all assets from container
UAssetTool extract_iostore_legacy "path/to/pakchunk0-Windows.utoc" "output_dir"
```

The tool automatically:
- Loads all game containers for cross-container dependency resolution
- Resolves import names correctly
- Handles encrypted containers
- Extracts `.uasset`, `.uexp`, and `.ubulk` files

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

### Zen/IoStore
- Full Zen package header parsing
- Cross-container import resolution
- Legacy to Zen conversion
- IoStore bundle creation

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
