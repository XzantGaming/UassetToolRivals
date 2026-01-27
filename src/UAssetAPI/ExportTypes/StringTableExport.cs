using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;
using UAssetAPI.UnrealTypes;
using UAssetAPI.ExportTypes;

namespace UAssetAPI.ExportTypes
{
    /// <summary>
    /// A string table entry with Key, Value, and FGameplayTagContainer.
    /// </summary>
    public class FStringTableEntry
    {
        public FString Key;
        public FString Value;
        public FGameplayTagContainer GameplayTagContainer;

        public FStringTableEntry()
        {
            GameplayTagContainer = new FGameplayTagContainer();
        }

        public FStringTableEntry(FString key, FString value)
        {
            Key = key;
            Value = value;
            GameplayTagContainer = new FGameplayTagContainer();
        }
    }

    /// <summary>
    /// A string table. Holds Key->SourceString pairs of text with FGameplayTagContainer per entry.
    /// </summary>
    public class FStringTable
    {
        [JsonProperty]
        public FString TableNamespace;

        [JsonProperty]
        public List<FStringTableEntry> Entries;

        /// <summary>
        /// Trailing FGameplayTagContainer after all entries.
        /// </summary>
        [JsonProperty]
        public FGameplayTagContainer TrailingTagContainer;

        public FStringTable(FString tableNamespace)
        {
            TableNamespace = tableNamespace;
            Entries = new List<FStringTableEntry>();
            TrailingTagContainer = new FGameplayTagContainer();
        }

        public FStringTable()
        {
            Entries = new List<FStringTableEntry>();
            TrailingTagContainer = new FGameplayTagContainer();
        }

        public int Count => Entries?.Count ?? 0;

        public void Add(FString key, FString value)
        {
            Entries.Add(new FStringTableEntry(key, value));
        }
    }

    /// <summary>
    /// Export data for a string table. See <see cref="FStringTable"/>.
    /// </summary>
    public class StringTableExport : NormalExport
    {
        [JsonProperty]
        public FStringTable Table;

        public StringTableExport(Export super) : base(super)
        {

        }

        public StringTableExport(FStringTable data, UAsset asset, byte[] extras) : base(asset, extras)
        {
            Table = data;
        }

        public StringTableExport()
        {

        }

        public override void Read(AssetBinaryReader reader, int nextStarting)
        {
            base.Read(reader, nextStarting);

            Table = new FStringTable(reader.ReadFString());

            int numEntries = reader.ReadInt32();
            for (int i = 0; i < numEntries; i++)
            {
                var entry = new FStringTableEntry();
                entry.Key = reader.ReadFString();
                entry.Value = reader.ReadFString();
                entry.GameplayTagContainer = new FGameplayTagContainer(reader);
                Table.Entries.Add(entry);
            }
            // Read trailing FGameplayTagContainer
            Table.TrailingTagContainer = new FGameplayTagContainer(reader);
        }

        public override void Write(AssetBinaryWriter writer)
        {
            base.Write(writer);

            writer.Write(Table.TableNamespace);
            writer.Write(Table.Count);
            foreach (var entry in Table.Entries)
            {
                writer.Write(entry.Key);
                writer.Write(entry.Value);
                // Write FGameplayTagContainer (empty = 4 bytes)
                if (entry.GameplayTagContainer == null)
                {
                    entry.GameplayTagContainer = new FGameplayTagContainer();
                }
                entry.GameplayTagContainer.Write(writer);
            }
            // Write trailing FGameplayTagContainer
            if (Table.TrailingTagContainer == null)
            {
                Table.TrailingTagContainer = new FGameplayTagContainer();
            }
            Table.TrailingTagContainer.Write(writer);
        }
    }
}
