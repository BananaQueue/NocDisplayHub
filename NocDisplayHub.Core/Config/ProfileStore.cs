using System.Text.Json;
using NocDisplayHub.Core.Bindings;

namespace NocDisplayHub.Core.Config;

/// <summary>Persists a single profile's layout to a JSON file on disk so bindings survive an app restart.</summary>
public static class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void Save(string path, LayoutManager manager)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(path, JsonSerializer.Serialize(manager.ToSnapshot(), JsonOptions));
    }

    public static LayoutManager Load(string path)
    {
        if (!File.Exists(path))
        {
            return new LayoutManager();
        }
        var data = JsonSerializer.Deserialize<ProfileData>(File.ReadAllText(path)) ?? new ProfileData();
        return LayoutManager.FromSnapshot(data);
    }
}
