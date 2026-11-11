using System;
using System.IO;
using System.Text.Json;

namespace Aether.Core;

/// <summary>Small JSON files in Data\ (shelf, clipboard, quota cache…): atomic write, never throws.</summary>
internal static class JsonFile
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string PathOf(string name) => Path.Combine(Paths.DataDir, name);

    public static T Load<T>(string name) where T : new()
    {
        try
        {
            var file = PathOf(name);
            if (File.Exists(file)) return JsonSerializer.Deserialize<T>(File.ReadAllText(file), Options) ?? new T();
        }
        catch (Exception ex) { Log.Error("load " + name, ex); }
        return new T();
    }

    public static void Save<T>(string name, T value)
    {
        try
        {
            Paths.Ensure();
            var file = PathOf(name);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
            File.Move(tmp, file, true);
        }
        catch (Exception ex) { Log.Error("save " + name, ex); }
    }
}
