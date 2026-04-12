using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace UAssetTool;

/// <summary>
/// Produces a compact, CUE4Parse-style JSON representation of a UAsset.
/// Read-only output format — not designed for roundtrip deserialization.
/// Strips all internal serialization metadata ($type, ArrayIndex, IsZero,
/// PropertyTagFlags, OriginalUnversionedHeader, etc.) and flattens
/// properties into direct key:value pairs.
/// </summary>
public static class CompactJsonSerializer
{
    public static string Serialize(UAsset asset, bool indented = true)
    {
        var options = new JsonWriterOptions
        {
            Indented = indented,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, options))
        {
            writer.WriteStartArray();
            foreach (var export in asset.Exports)
            {
                WriteExport(writer, export, asset);
            }
            writer.WriteEndArray();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteExport(Utf8JsonWriter writer, Export export, UAsset asset)
    {
        writer.WriteStartObject();

        // Basic export metadata
        string exportType = ResolveExportClassName(export, asset);
        writer.WriteString("Type", exportType);
        writer.WriteString("Name", export.ObjectName?.ToString() ?? "");
        writer.WriteString("Flags", export.ObjectFlags.ToString().Replace(", ", " | "));

        // Resolve class reference
        if (export.ClassIndex != null && !export.ClassIndex.IsNull())
        {
            writer.WriteString("Class", ResolvePackageIndex(export.ClassIndex, asset));
        }

        // Handle specific export types
        switch (export)
        {
            case DataTableExport dt:
                WriteDataTableExport(writer, dt, asset);
                break;
            case StringTableExport st:
                WriteStringTableExport(writer, st, asset);
                break;
            case NormalExport ne:
                WriteNormalExportProperties(writer, ne, asset);
                break;
            default:
                // RawExport or base Export — no properties to write
                break;
        }

        writer.WriteEndObject();
    }

    private static void WriteDataTableExport(Utf8JsonWriter writer, DataTableExport dt, UAsset asset)
    {
        // Write the export's own properties (e.g., RowStruct)
        if (dt.Data != null && dt.Data.Count > 0)
        {
            writer.WritePropertyName("Properties");
            WritePropertyListAsObject(writer, dt.Data, asset);
        }

        // Write the DataTable rows
        if (dt.Table?.Data != null)
        {
            writer.WritePropertyName("Rows");
            writer.WriteStartObject();
            foreach (var row in dt.Table.Data)
            {
                string rowName = row.Name?.ToString() ?? "Unknown";
                writer.WritePropertyName(rowName);
                if (row.Value != null)
                {
                    WritePropertyListAsObject(writer, row.Value, asset);
                }
                else
                {
                    writer.WriteStartObject();
                    writer.WriteEndObject();
                }
            }
            writer.WriteEndObject();
        }
    }

    private static void WriteStringTableExport(Utf8JsonWriter writer, StringTableExport st, UAsset asset)
    {
        // Write any normal properties
        if (st.Data != null && st.Data.Count > 0)
        {
            writer.WritePropertyName("Properties");
            WritePropertyListAsObject(writer, st.Data, asset);
        }

        if (st.Table != null)
        {
            writer.WriteString("TableNamespace", st.Table.TableNamespace?.ToString() ?? "");

            writer.WritePropertyName("StringTable");
            writer.WriteStartObject();
            foreach (var kv in st.Table)
            {
                writer.WriteString(kv.Key?.ToString() ?? "", kv.Value?.ToString() ?? "");
            }
            writer.WriteEndObject();
        }
    }

    private static void WriteNormalExportProperties(Utf8JsonWriter writer, NormalExport ne, UAsset asset)
    {
        if (ne.Data != null && ne.Data.Count > 0)
        {
            writer.WritePropertyName("Properties");
            WritePropertyListAsObject(writer, ne.Data, asset);
        }
    }

    /// <summary>
    /// Write a list of PropertyData as a JSON object with key:value pairs.
    /// </summary>
    private static void WritePropertyListAsObject(Utf8JsonWriter writer, List<PropertyData> properties, UAsset asset)
    {
        writer.WriteStartObject();
        foreach (var prop in properties)
        {
            string propName = prop.Name?.ToString() ?? "Unknown";
            writer.WritePropertyName(propName);
            WritePropertyValue(writer, prop, asset);
        }
        writer.WriteEndObject();
    }

    /// <summary>
    /// Write the value of a single PropertyData as a JSON value.
    /// Dispatches based on the concrete property type.
    /// </summary>
    private static void WritePropertyValue(Utf8JsonWriter writer, PropertyData prop, UAsset asset)
    {
        if (prop == null)
        {
            writer.WriteNullValue();
            return;
        }

        switch (prop)
        {
            // === Primitives ===
            case BoolPropertyData boolProp:
                writer.WriteBooleanValue(boolProp.Value);
                break;

            case IntPropertyData intProp:
                writer.WriteNumberValue(intProp.Value);
                break;

            case Int8PropertyData i8Prop:
                writer.WriteNumberValue(i8Prop.Value);
                break;

            case Int16PropertyData i16Prop:
                writer.WriteNumberValue(i16Prop.Value);
                break;

            case Int64PropertyData i64Prop:
                writer.WriteNumberValue(i64Prop.Value);
                break;

            case UInt16PropertyData u16Prop:
                writer.WriteNumberValue(u16Prop.Value);
                break;

            case UInt32PropertyData u32Prop:
                writer.WriteNumberValue(u32Prop.Value);
                break;

            case UInt64PropertyData u64Prop:
                writer.WriteNumberValue(u64Prop.Value);
                break;

            case FloatPropertyData floatProp:
                writer.WriteNumberValue(floatProp.Value);
                break;

            case DoublePropertyData doubleProp:
                writer.WriteNumberValue(doubleProp.Value);
                break;

            // === Strings ===
            case StrPropertyData strProp:
                writer.WriteStringValue(strProp.Value?.ToString());
                break;

            case NamePropertyData nameProp:
                writer.WriteStringValue(nameProp.Value?.ToString());
                break;

            // === Enum ===
            case EnumPropertyData enumProp:
                WriteEnumValue(writer, enumProp);
                break;

            // === Byte (can be enum or raw byte) ===
            case BytePropertyData byteProp:
                if (byteProp.ByteType == BytePropertyType.FName)
                {
                    writer.WriteStringValue(byteProp.EnumValue?.ToString());
                }
                else
                {
                    writer.WriteNumberValue(byteProp.Value);
                }
                break;

            // === Text ===
            case TextPropertyData textProp:
                WriteTextProperty(writer, textProp);
                break;

            // === Object references (subclasses before parent) ===
            case InterfacePropertyData ifaceProp:
                WriteObjectReference(writer, ifaceProp.Value, asset);
                break;

            case WeakObjectPropertyData weakProp:
                WriteObjectReference(writer, weakProp.Value, asset);
                break;

            case ObjectPropertyData objProp:
                WriteObjectReference(writer, objProp.Value, asset);
                break;

            case SoftObjectPropertyData softProp:
                WriteSoftObjectPath(writer, softProp.Value);
                break;

            case AssetObjectPropertyData assetObjProp:
                writer.WriteStringValue(assetObjProp.Value?.ToString());
                break;

            // === Struct (recursive) ===
            case StructPropertyData structProp:
                WriteStructProperty(writer, structProp, asset);
                break;

            // === Set (subclass of Array, must come first) ===
            case SetPropertyData setProp:
                WriteSetProperty(writer, setProp, asset);
                break;

            // === Array ===
            case ArrayPropertyData arrayProp:
                WriteArrayProperty(writer, arrayProp, asset);
                break;

            // === Map ===
            case MapPropertyData mapProp:
                WriteMapProperty(writer, mapProp, asset);
                break;

            // === Delegate / MulticastDelegate ===
            case DelegatePropertyData delProp:
                writer.WriteStartObject();
                writer.WriteString("Object", ResolvePackageIndex(delProp.Value.Object, asset));
                writer.WriteString("FunctionName", delProp.Value.Delegate?.ToString());
                writer.WriteEndObject();
                break;

            case MulticastDelegatePropertyData mDelProp:
                writer.WriteStartArray();
                if (mDelProp.Value != null)
                {
                    foreach (var del in mDelProp.Value)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("Object", ResolvePackageIndex(del.Object, asset));
                        writer.WriteString("FunctionName", del.Delegate?.ToString());
                        writer.WriteEndObject();
                    }
                }
                writer.WriteEndArray();
                break;

            // === FieldPath ===
            case FieldPathPropertyData fpProp:
                writer.WriteStartObject();
                if (fpProp.Value.Path != null)
                {
                    writer.WritePropertyName("Path");
                    writer.WriteStartArray();
                    foreach (var segment in fpProp.Value.Path)
                    {
                        writer.WriteStringValue(segment?.ToString());
                    }
                    writer.WriteEndArray();
                }
                writer.WriteString("ResolvedOwner", ResolvePackageIndex(fpProp.Value.ResolvedOwner, asset));
                writer.WriteEndObject();
                break;

            // === Specialized struct types ===
            case LinearColorPropertyData colorProp:
                WriteLinearColor(writer, colorProp.Value);
                break;

            case ColorPropertyData srgbColorProp:
                writer.WriteStartObject();
                writer.WriteNumber("R", srgbColorProp.Value.R);
                writer.WriteNumber("G", srgbColorProp.Value.G);
                writer.WriteNumber("B", srgbColorProp.Value.B);
                writer.WriteNumber("A", srgbColorProp.Value.A);
                writer.WriteEndObject();
                break;

            case VectorPropertyData vecProp:
                WriteVector(writer, vecProp.Value);
                break;

            case Vector2DPropertyData vec2Prop:
                writer.WriteStartObject();
                writer.WriteNumber("X", vec2Prop.Value.X);
                writer.WriteNumber("Y", vec2Prop.Value.Y);
                writer.WriteEndObject();
                break;

            case Vector4PropertyData vec4Prop:
                writer.WriteStartObject();
                writer.WriteNumber("X", vec4Prop.Value.X);
                writer.WriteNumber("Y", vec4Prop.Value.Y);
                writer.WriteNumber("Z", vec4Prop.Value.Z);
                writer.WriteNumber("W", vec4Prop.Value.W);
                writer.WriteEndObject();
                break;

            case RotatorPropertyData rotProp:
                writer.WriteStartObject();
                writer.WriteNumber("Pitch", rotProp.Value.Pitch);
                writer.WriteNumber("Yaw", rotProp.Value.Yaw);
                writer.WriteNumber("Roll", rotProp.Value.Roll);
                writer.WriteEndObject();
                break;

            case QuatPropertyData quatProp:
                writer.WriteStartObject();
                writer.WriteNumber("X", quatProp.Value.X);
                writer.WriteNumber("Y", quatProp.Value.Y);
                writer.WriteNumber("Z", quatProp.Value.Z);
                writer.WriteNumber("W", quatProp.Value.W);
                writer.WriteEndObject();
                break;

            case PlanePropertyData planeProp:
                writer.WriteStartObject();
                writer.WriteNumber("X", planeProp.Value.X);
                writer.WriteNumber("Y", planeProp.Value.Y);
                writer.WriteNumber("Z", planeProp.Value.Z);
                writer.WriteNumber("W", planeProp.Value.W);
                writer.WriteEndObject();
                break;

            case BoxPropertyData boxProp:
                writer.WriteStartObject();
                writer.WritePropertyName("Min");
                WriteVector(writer, boxProp.Value.Min);
                writer.WritePropertyName("Max");
                WriteVector(writer, boxProp.Value.Max);
                writer.WriteBoolean("IsValid", boxProp.Value.IsValid != 0);
                writer.WriteEndObject();
                break;

            case IntPointPropertyData ipProp:
                writer.WriteStartObject();
                writer.WriteNumber("X", ipProp.Value[0]);
                writer.WriteNumber("Y", ipProp.Value[1]);
                writer.WriteEndObject();
                break;

            case GuidPropertyData guidProp:
                writer.WriteStringValue(guidProp.Value.ToString());
                break;

            case DateTimePropertyData dtProp:
                writer.WriteNumberValue(dtProp.Value.Ticks);
                break;

            case TimespanPropertyData tsProp:
                writer.WriteNumberValue(tsProp.Value.Ticks);
                break;

            case GameplayTagContainerPropertyData tagProp:
                writer.WriteStartArray();
                if (tagProp.Value != null)
                {
                    foreach (var tag in tagProp.Value)
                    {
                        writer.WriteStringValue(tag?.ToString());
                    }
                }
                writer.WriteEndArray();
                break;

            // === Raw/Unknown — base64 fallback ===
            case RawStructPropertyData rawStruct:
                if (rawStruct.Value != null && rawStruct.Value.Length > 0)
                {
                    writer.WriteStringValue("base64:" + Convert.ToBase64String(rawStruct.Value));
                }
                else
                {
                    writer.WriteNullValue();
                }
                break;

            case UnknownPropertyData unknownProp:
                if (unknownProp.Value != null && unknownProp.Value.Length > 0)
                {
                    writer.WriteStringValue("base64:" + Convert.ToBase64String(unknownProp.Value));
                }
                else
                {
                    writer.WriteNullValue();
                }
                break;

            // === Fallback ===
            default:
                // Try to get the raw value
                var raw = prop.RawValue;
                if (raw is byte[] bytes)
                {
                    writer.WriteStringValue("base64:" + Convert.ToBase64String(bytes));
                }
                else if (raw != null)
                {
                    writer.WriteStringValue(raw.ToString());
                }
                else
                {
                    writer.WriteNullValue();
                }
                break;
        }
    }

    private static void WriteStructProperty(Utf8JsonWriter writer, StructPropertyData structProp, UAsset asset)
    {
        if (structProp.Value == null || structProp.Value.Count == 0)
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
            return;
        }

        // Unwrap single-child structs where the child is a specialized type
        // (e.g., struct containing just a LinearColor, Vector2D, etc.)
        if (structProp.Value.Count == 1)
        {
            var child = structProp.Value[0];
            if (child is LinearColorPropertyData || child is VectorPropertyData ||
                child is Vector2DPropertyData || child is Vector4PropertyData ||
                child is RotatorPropertyData || child is QuatPropertyData ||
                child is PlanePropertyData || child is BoxPropertyData ||
                child is IntPointPropertyData || child is GuidPropertyData ||
                child is DateTimePropertyData || child is TimespanPropertyData ||
                child is GameplayTagContainerPropertyData || child is RawStructPropertyData)
            {
                WritePropertyValue(writer, child, asset);
                return;
            }
        }

        WritePropertyListAsObject(writer, structProp.Value, asset);
    }

    private static void WriteArrayProperty(Utf8JsonWriter writer, ArrayPropertyData arrayProp, UAsset asset)
    {
        writer.WriteStartArray();
        if (arrayProp.Value != null)
        {
            foreach (var item in arrayProp.Value)
            {
                WritePropertyValue(writer, item, asset);
            }
        }
        writer.WriteEndArray();
    }

    private static void WriteSetProperty(Utf8JsonWriter writer, SetPropertyData setProp, UAsset asset)
    {
        writer.WriteStartArray();
        if (setProp.Value != null)
        {
            foreach (var item in setProp.Value)
            {
                WritePropertyValue(writer, item, asset);
            }
        }
        writer.WriteEndArray();
    }

    private static void WriteMapProperty(Utf8JsonWriter writer, MapPropertyData mapProp, UAsset asset)
    {
        writer.WriteStartObject();
        if (mapProp.Value != null)
        {
            foreach (var kv in mapProp.Value)
            {
                // Key must be stringified for JSON object keys
                string key = PropertyValueToString(kv.Key, asset);
                writer.WritePropertyName(key);
                WritePropertyValue(writer, kv.Value, asset);
            }
        }
        writer.WriteEndObject();
    }

    private static void WriteEnumValue(Utf8JsonWriter writer, EnumPropertyData enumProp)
    {
        string? valueName = enumProp.Value?.ToString();
        if (valueName == null)
        {
            writer.WriteNullValue();
            return;
        }

        // If value already contains "::", write as-is
        if (valueName.Contains("::"))
        {
            writer.WriteStringValue(valueName);
            return;
        }

        // Prepend EnumType:: if available (to match CUE4Parse format like "EHeroRole::Tank")
        string? enumType = enumProp.EnumType?.ToString();
        if (!string.IsNullOrEmpty(enumType))
        {
            writer.WriteStringValue($"{enumType}::{valueName}");
        }
        else
        {
            writer.WriteStringValue(valueName);
        }
    }

    private static void WriteTextProperty(Utf8JsonWriter writer, TextPropertyData text)
    {
        writer.WriteStartObject();

        switch (text.HistoryType)
        {
            case TextHistoryType.StringTableEntry:
                writer.WriteString("TableId", text.TableId?.ToString() ?? "");
                writer.WriteString("Key", text.Value?.ToString() ?? "");
                writer.WriteString("SourceString", text.CultureInvariantString?.ToString() ?? "");
                // Namespace can serve as localized context
                if (text.Namespace != null && !string.IsNullOrEmpty(text.Namespace.ToString()))
                    writer.WriteString("Namespace", text.Namespace.ToString());
                break;

            case TextHistoryType.Base:
                writer.WriteString("Namespace", text.Namespace?.ToString() ?? "");
                writer.WriteString("Key", text.Value?.ToString() ?? "");
                writer.WriteString("SourceString", text.CultureInvariantString?.ToString() ?? "");
                break;

            case TextHistoryType.None:
                if (text.CultureInvariantString != null)
                    writer.WriteString("CultureInvariantString", text.CultureInvariantString.ToString());
                break;

            default:
                writer.WriteString("HistoryType", text.HistoryType.ToString());
                if (text.Value != null)
                    writer.WriteString("Value", text.Value.ToString());
                if (text.Namespace != null)
                    writer.WriteString("Namespace", text.Namespace.ToString());
                if (text.CultureInvariantString != null)
                    writer.WriteString("SourceString", text.CultureInvariantString.ToString());
                break;
        }

        writer.WriteEndObject();
    }

    private static void WriteObjectReference(Utf8JsonWriter writer, FPackageIndex index, UAsset asset)
    {
        if (index == null || index.IsNull())
        {
            writer.WriteNullValue();
            return;
        }

        string resolved = ResolvePackageIndex(index, asset);
        writer.WriteStringValue(resolved);
    }

    private static void WriteSoftObjectPath(Utf8JsonWriter writer, FSoftObjectPath path)
    {
        writer.WriteStartObject();

        string assetPath = "";
        if (path.AssetPath.PackageName != null)
            assetPath = path.AssetPath.PackageName.ToString();
        if (path.AssetPath.AssetName != null)
        {
            string assetName = path.AssetPath.AssetName.ToString();
            if (!string.IsNullOrEmpty(assetPath) && !string.IsNullOrEmpty(assetName))
                assetPath += "." + assetName;
            else if (!string.IsNullOrEmpty(assetName))
                assetPath = assetName;
        }

        writer.WriteString("AssetPathName", assetPath);
        writer.WriteString("SubPathString", path.SubPathString?.ToString() ?? "");
        writer.WriteEndObject();
    }

    private static void WriteLinearColor(Utf8JsonWriter writer, FLinearColor color)
    {
        writer.WriteStartObject();
        writer.WriteNumber("R", Math.Round(color.R, 6));
        writer.WriteNumber("G", Math.Round(color.G, 6));
        writer.WriteNumber("B", Math.Round(color.B, 6));
        writer.WriteNumber("A", Math.Round(color.A, 6));

        // Add hex like CUE4Parse
        var srgb = LinearHelpers.Convert(color);
        writer.WriteString("Hex", $"{srgb.R:X2}{srgb.G:X2}{srgb.B:X2}");

        writer.WriteEndObject();
    }

    private static void WriteVector(Utf8JsonWriter writer, FVector vec)
    {
        writer.WriteStartObject();
        writer.WriteNumber("X", vec.X);
        writer.WriteNumber("Y", vec.Y);
        writer.WriteNumber("Z", vec.Z);
        writer.WriteEndObject();
    }

    // === Helper methods ===

    private static string ResolveExportClassName(Export export, UAsset asset)
    {
        if (export.ClassIndex != null && !export.ClassIndex.IsNull())
        {
            try
            {
                if (export.ClassIndex.IsImport())
                {
                    var imp = export.ClassIndex.ToImport(asset);
                    if (imp != null)
                        return imp.ObjectName?.ToString() ?? "Unknown";
                }
                else if (export.ClassIndex.IsExport())
                {
                    var exp = export.ClassIndex.ToExport(asset);
                    if (exp != null)
                        return exp.ObjectName?.ToString() ?? "Unknown";
                }
            }
            catch { }
        }

        // Fallback: use the C# type name
        return export.GetType().Name.Replace("Export", "");
    }

    private static string ResolvePackageIndex(FPackageIndex index, UAsset asset)
    {
        if (index == null || index.IsNull())
            return "None";

        try
        {
            if (index.IsImport())
            {
                var imp = index.ToImport(asset);
                if (imp != null)
                {
                    string className = imp.ClassName?.ToString() ?? "";
                    string objName = imp.ObjectName?.ToString() ?? "";

                    // Build full path by walking outers
                    string path = BuildImportPath(imp, asset);
                    if (!string.IsNullOrEmpty(className))
                        return $"{className}'{path}'";
                    return path;
                }
            }
            else if (index.IsExport())
            {
                var exp = index.ToExport(asset);
                if (exp != null)
                    return exp.ObjectName?.ToString() ?? $"Export[{index.Index - 1}]";
            }
        }
        catch { }

        return $"Index[{index.Index}]";
    }

    private static string BuildImportPath(Import imp, UAsset asset)
    {
        string name = imp.ObjectName?.ToString() ?? "";
        if (imp.OuterIndex != null && !imp.OuterIndex.IsNull())
        {
            try
            {
                if (imp.OuterIndex.IsImport())
                {
                    var outer = imp.OuterIndex.ToImport(asset);
                    if (outer != null)
                    {
                        string outerPath = BuildImportPath(outer, asset);
                        return outerPath + "." + name;
                    }
                }
            }
            catch { }
        }
        return name;
    }

    /// <summary>
    /// Convert a property value to a string (used for map keys).
    /// </summary>
    private static string PropertyValueToString(PropertyData prop, UAsset asset)
    {
        if (prop == null) return "null";

        switch (prop)
        {
            case IntPropertyData ip: return ip.Value.ToString();
            case Int64PropertyData i64p: return i64p.Value.ToString();
            case UInt32PropertyData u32p: return u32p.Value.ToString();
            case UInt64PropertyData u64p: return u64p.Value.ToString();
            case StrPropertyData sp: return sp.Value?.ToString() ?? "";
            case NamePropertyData np: return np.Value?.ToString() ?? "";
            case EnumPropertyData ep: return ep.Value?.ToString() ?? "";
            case ObjectPropertyData op: return ResolvePackageIndex(op.Value, asset);
            case SoftObjectPropertyData sop:
                string path = "";
                if (sop.Value.AssetPath.PackageName != null)
                    path = sop.Value.AssetPath.PackageName.ToString();
                if (sop.Value.AssetPath.AssetName != null)
                    path += "." + sop.Value.AssetPath.AssetName.ToString();
                return path;
            case GuidPropertyData gp: return gp.Value.ToString();
            case StructPropertyData structP:
                // For struct keys, use a hash-like representation
                return structP.Name?.ToString() ?? "struct";
            default:
                var raw = prop.RawValue;
                return raw?.ToString() ?? prop.GetType().Name;
        }
    }
}
