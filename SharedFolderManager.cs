// Tracks sync-connected shared folders so both the sidebar (MainWindow)
// and the editor page (NoteEditorPage) share the same state.
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WorkSpaceApp.Core.Services;

public partial class SharedFolderEntry : ObservableObject
{
    public string Token           { get; init; } = "";
    public string Role            { get; init; } = "";   // "Viewer" | "Editor"
    public string RemoteFolderName { get; init; } = "";
    public string ServerUrl       { get; init; } = "";
    public int    Index           { get; init; }

    [ObservableProperty]
    private string _displayName = "";
}

public class SharedFolderManager
{
    private static int _nextIndex;

    public ObservableCollection<SharedFolderEntry> Folders { get; } = [];

    public SharedFolderEntry Add(string token, string remoteFolderName, string role, string serverUrl)
    {
        var idx   = ++_nextIndex;
        var entry = new SharedFolderEntry
        {
            Token            = token,
            RemoteFolderName = remoteFolderName,
            Role             = role,
            ServerUrl        = serverUrl,
            Index            = idx,
            DisplayName      = $"Folder {idx}"
        };
        Folders.Add(entry);
        return entry;
    }

    public void Remove(string token)
    {
        var entry = Folders.FirstOrDefault(f => f.Token == token);
        if (entry is not null) Folders.Remove(entry);
    }

    public SharedFolderEntry? Get(string token) =>
        Folders.FirstOrDefault(f => f.Token == token);
}
