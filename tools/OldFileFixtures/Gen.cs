// Writes a profile and a global config with EVERY field set to something other than its default, using the released
// RemSound.Core of one version and the exact serializer settings that version's ProfileStore and AppConfig use.
using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using RemSound.Core;

#if GEN_v5_9
const string Version = "v5.9";
#else
const string Version = "v6.0";
#endif
var outDir = args[0];
var tag = Version.Replace(".", "");

var profile = new Profile();
Fill(profile, 0);
profile.Password = RemSoundCrypto.Obfuscate("old-profile-password");
File.WriteAllText(Path.Combine(outDir, $"{Version}-profile.json"), JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));

var config = new AppConfig();
Fill(config, 0);
File.WriteAllText(Path.Combine(outDir, $"{Version}-global-config.json"), JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{Version}: wrote the profile and the global config");

void Fill(object target, int depth)
{
    foreach (var p in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
    {
        if (!p.CanRead || !p.CanWrite || p.GetSetMethod() is null || p.GetIndexParameters().Length > 0) continue;
        if (p.GetCustomAttribute<JsonIgnoreAttribute>() is not null) continue;
        var v = Distinct(p.PropertyType, p.GetValue(target), p.Name, depth);
        if (v is not null) p.SetValue(target, v);
    }
}

object? Distinct(Type t, object? current, string name, int depth)
{
    var u = Nullable.GetUnderlyingType(t);
    if (u is not null)
    {
        if (u == typeof(bool)) return current is true ? false : true;
        return Distinct(u, current ?? Activator.CreateInstance(u), name, depth);
    }
    if (t == typeof(bool)) return !(bool)current!;
    if (t == typeof(int)) return (int)current! + 3;
    if (t == typeof(long)) return (long)current! + 3;
    if (t == typeof(double)) return (double)current! + 1.25;
    if (t == typeof(float)) return (float)current! + 1.25f;
    if (t == typeof(string)) return ((string?)current ?? "") + "-" + tag + "-" + name;
    if (t == typeof(Guid)) return Guid.Parse("0f0e0d0c-0b0a-0908-0706-050403020100");
    if (t == typeof(DateTime)) return new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
    if (t == typeof(DateTimeOffset)) return new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    if (t.IsEnum)
    {
        var values = Enum.GetValues(t).Cast<object>().ToList();
        var i = values.IndexOf(current!);
        return values[(i + 1) % values.Count];
    }
    if (depth > 3) return null;
    if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
    {
        var list = (IList)Activator.CreateInstance(t)!;
        var e = t.GetGenericArguments()[0];
        var item = Distinct(e, e.IsValueType ? Activator.CreateInstance(e) : e == typeof(string) ? "" : TryNew(e), name + "Item", depth + 1);
        if (item is not null) list.Add(item);
        return list;
    }
    if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>))
    {
        var dict = (IDictionary)Activator.CreateInstance(t)!;
        var k = t.GetGenericArguments()[0];
        var e = t.GetGenericArguments()[1];
        var key = k == typeof(string) ? "key-" + tag : Distinct(k, k.IsValueType ? Activator.CreateInstance(k) : null, name + "Key", depth + 1);
        var item = Distinct(e, e.IsValueType ? Activator.CreateInstance(e) : e == typeof(string) ? "" : TryNew(e), name + "Value", depth + 1);
        if (key is not null && item is not null) dict[key] = item;
        return dict;
    }
    if (t.IsClass && TryNew(t) is { } fresh)
    {
        Fill(fresh, depth + 1);
        return fresh;
    }
    return null;
}

static object? TryNew(Type t)
{
    try { return t.GetConstructor(Type.EmptyTypes) is null ? null : Activator.CreateInstance(t); }
    catch { return null; }
}
