// ============================================================
// Core/Models/FileTreeNode.cs
// ============================================================
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WorkSpaceApp.Core.Models;

public partial class FileTreeNode : ObservableObject
{
    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }

    /// <summary>Дочерние узлы (только для папок).</summary>
    public ObservableCollection<FileTreeNode> Children { get; } = [];

    /// <summary>Теги, привязанные к файлу (для .md файлов).</summary>
    [ObservableProperty]
    private ObservableCollection<Tag> _tags = [];

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(Icon));
    }

    [ObservableProperty]
    private bool _isExpanded = false;

    public string Icon => IsDirectory
        ? (IsExpanded ? "\uE838" : "\uED41")   // OpenFolder / Folder
        : "\uE8A5";                             // Document

    public static FileTreeNode CreateTree(string directoryPath)
    {
        var di = new DirectoryInfo(directoryPath);
        var root = new FileTreeNode
        {
            Name = di.Name,
            FullPath = di.FullName,
            IsDirectory = true
        };

        try
        {
            // Читаем подпапки
            foreach (var dir in di.GetDirectories())
            {
                // Рекурсивно строим дерево для подпапок
                root.Children.Add(CreateTree(dir.FullName));
            }

            // Читаем файлы (например, .md файлы заметок)
            foreach (var file in di.GetFiles("*.md"))
            {
                root.Children.Add(new FileTreeNode
                {
                    Name = file.Name,
                    FullPath = file.FullName,
                    IsDirectory = false
                });
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Обработка системных папок, к которым нет доступа
        }

        return root;
    }
}