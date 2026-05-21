// ============================================================
// Features/Notes/ViewModels/FileTreeViewModel.cs
// Сканирует корневую папку и строит ObservableCollection узлов.
// ============================================================
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using WorkSpaceApp.Core.Models;
using WorkSpaceApp.Infrastructure.Data;

namespace WorkSpaceApp.Features.Notes.ViewModels;

public partial class FileTreeViewModel : ObservableObject
{
    private readonly AppDbContext _db;
    
    public ObservableCollection<FileTreeNode> Roots { get; } = [];

    [ObservableProperty]
    private FileTreeNode? _selectedNode;

    // Событие — Page подписывается и открывает файл в редакторе
    public event Action<string>? FileSelected;
    
    // Событие — Page подписывается и отображает содержимое папки в правой панели
    public event Action<string>? FolderSelected;

    public FileTreeViewModel(AppDbContext db)
    {
        _db = db;
    }

    public FileTreeViewModel() : this(new AppDbContext(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={AppDbContext.GetDatabaseFilePath()}")
            .Options))
    {
    }

    // ── Загрузка дерева ───────────────────────────────────────

    public async void LoadDirectory(string rootPath)
    {
        Roots.Clear();
        if (!Directory.Exists(rootPath)) return;

        var root = BuildNode(new DirectoryInfo(rootPath));
        root.IsExpanded = true;
        Roots.Add(root);
        
        // Загружаем теги для всех файлов в дереве
        await LoadTagsForTreeAsync(root);
    }

    private async Task LoadTagsForTreeAsync(FileTreeNode node)
    {
        if (!node.IsDirectory && !string.IsNullOrEmpty(node.FullPath))
        {
            await LoadTagsForNodeAsync(node);
        }

        foreach (var child in node.Children)
        {
            await LoadTagsForTreeAsync(child);
        }
    }

    private static FileTreeNode BuildNode(DirectoryInfo dir)
    {
        var node = new FileTreeNode
        {
            Name = dir.Name,
            FullPath = dir.FullName,
            IsDirectory = true,
            IsExpanded = false
        };

        // Сначала папки, потом .md файлы
        foreach (var sub in dir.GetDirectories()
                               .OrderBy(d => d.Name))
            node.Children.Add(BuildNode(sub));

        foreach (var file in dir.GetFiles("*.md")
                                .OrderBy(f => f.Name))
        {
            var fileNode = new FileTreeNode
            {
                Name = file.Name,
                FullPath = file.FullName,
                IsDirectory = false
            };
            node.Children.Add(fileNode);
        }

        return node;
    }

    // ── Команды ───────────────────────────────────────────────

    [RelayCommand]
    public void NodeClicked(FileTreeNode node)
    {
        if (node.IsDirectory)
        {
            node.IsExpanded = !node.IsExpanded;
            FolderSelected?.Invoke(node.FullPath);
            return;
        }

        // Это .md файл — сообщаем странице
        SelectedNode = node;
        FileSelected?.Invoke(node.FullPath);
    }

    // Открыть диалог выбора папки (вызывается из code-behind)
    public void SetRootPath(string path) => LoadDirectory(path);

    // ── Метод для переключения тега файла ─────────────────────
    public async Task ToggleFileTagAsync(string filePath, Tag tag)
    {
        if (string.IsNullOrEmpty(filePath)) return;

        // Находим существующий тег в БД или создаем новый
        var existingTag = await _db.Tags.FirstOrDefaultAsync(t => t.Name == tag.Name);
        if (existingTag == null)
        {
            existingTag = tag;
            _db.Tags.Add(existingTag);
            await _db.SaveChangesAsync();
        }

        // Проверяем, есть ли уже связь этого файла с тегом
        var existingFileTag = await _db.FileTags
            .FirstOrDefaultAsync(ft => ft.FilePath == filePath && ft.TagId == existingTag.Id);

        if (existingFileTag != null)
        {
            // Удаляем тег
            _db.FileTags.Remove(existingFileTag);
        }
        else
        {
            // Добавляем тег
            _db.FileTags.Add(new FileTag
            {
                FilePath = filePath,
                TagId = existingTag.Id
            });
        }

        await _db.SaveChangesAsync();

        // Обновляем узел в UI
        var node = FindNodeByPath(Roots, filePath);
        if (node != null)
        {
            await LoadTagsForNodeAsync(node);
        }
    }

    // ── Загрузка тегов для узла ───────────────────────────────
    public async Task LoadTagsForNodeAsync(FileTreeNode node)
    {
        if (node.IsDirectory || string.IsNullOrEmpty(node.FullPath)) return;

        var fileTags = await _db.FileTags
            .Include(ft => ft.Tag)
            .Where(ft => ft.FilePath == node.FullPath)
            .ToListAsync();

        node.Tags.Clear();
        foreach (var ft in fileTags)
        {
            node.Tags.Add(ft.Tag);
        }
    }

    // ── Поиск узла по пути ────────────────────────────────────
    private FileTreeNode? FindNodeByPath(ObservableCollection<FileTreeNode> nodes, string path)
    {
        foreach (var node in nodes)
        {
            if (node.FullPath == path)
                return node;
            
            var found = FindNodeByPath(node.Children, path);
            if (found != null)
                return found;
        }
        return null;
    }

    // ── Получение файлов по тегу ──────────────────────────────
    public async Task<List<string>> GetFilesByTagNameAsync(string tagName)
    {
        var fileTags = await _db.FileTags
            .Include(ft => ft.Tag)
            .Where(ft => ft.Tag.Name == tagName)
            .ToListAsync();

        return fileTags.Select(ft => ft.FilePath).ToList();
    }
}