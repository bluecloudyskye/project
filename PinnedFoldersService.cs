using System.Text.Json;

namespace WorkSpaceApp.Core.Services;

public record PinnedFolder(string Name, string Path);

public class PinnedFoldersService
{
    private static readonly string FilePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WorkSpaceApp", "pinned_folders.json");

    public List<PinnedFolder> Load()
    {
        if (!File.Exists(FilePath)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<PinnedFolder>>(
                File.ReadAllText(FilePath)) ?? [];
        }
        catch { return []; }
    }

    public void Add(string name, string path)
    {
        var list = Load();
        if (list.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))) return;
        list.Add(new PinnedFolder(name, path));
        Save(list);
    }

    public void Remove(string path)
    {
        var list = Load();
        list.RemoveAll(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
        Save(list);
    }

    private static void Save(List<PinnedFolder> list)
        => File.WriteAllText(FilePath, JsonSerializer.Serialize(list));
}
