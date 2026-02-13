using UAssetAPI.Unversioned;
var usmap = new Usmap(@"C:\Users\NIkolas\AppData\Roaming\RepakGuiRevamped\Usmap\5.3.2-2994263+++depot_marvel+S6.5_release-Marvel.usmap");
Console.WriteLine("Total schemas: " + usmap.Schemas.Count);
foreach (var kv in usmap.Schemas.Where(s => s.Key.Contains("LightingChannel")).OrderBy(s => s.Key)) {
    var s = kv.Value;
    var propList = string.Join(", ", s.Properties.OrderBy(p => p.Key).Select(p => $"{p.Key}:{p.Value.Name}"));
    Console.WriteLine($"{kv.Key}: PropCount={s.PropCount}, SuperType='{s.SuperType}', Props=[{propList}]");
}
Console.WriteLine("---");
foreach (var kv in usmap.Schemas.Where(s => s.Key.Contains("ReplaceMaterial")).OrderBy(s => s.Key)) {
    var s = kv.Value;
    var propList = string.Join(", ", s.Properties.OrderBy(p => p.Key).Select(p => $"{p.Key}:{p.Value.Name}"));
    Console.WriteLine($"{kv.Key}: PropCount={s.PropCount}, SuperType='{s.SuperType}', Props=[{propList}]");
}
