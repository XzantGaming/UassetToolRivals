using UAssetAPI.Unversioned;
var usmap = new Usmap(@"C:\Users\NIkolas\AppData\Roaming\RepakGuiRevamped\Usmap\5.3.2-2994263+++depot_marvel+S6.5_release-Marvel.usmap");
foreach (var name in new[] { "ReplaceMaterialInfo", "LightingChannels", "Vector2f", "CustomPrimitiveData", "PostProcessSettings" }) {
    if (usmap.Schemas.TryGetValue(name, out var s)) {
        Console.WriteLine($"{name}: PropCount={s.PropCount}, SuperType={s.SuperType ?? "null"}, Props=[{string.Join(", ", s.Properties.OrderBy(p=>p.Key).Select(p=>$"{p.Key}:{p.Value.Name}"))}]");
    } else {
        Console.WriteLine($"{name}: NOT FOUND");
    }
}
