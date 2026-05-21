using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Storage;
using Windows.Storage.Pickers;
using WorkSpaceApp.Core.Models;
using WorkSpaceApp.Features.Notes.ViewModels;

namespace WorkSpaceApp.Features.Notes.Views;

public sealed partial class NoteEditorPage : Page
{
    public FileTreeViewModel FileTreeVM { get; } = new();

    public NoteEditorViewModel ViewModel { get; }
    private bool _isEditorReady = false;
    private string _lastReceivedMarkdown = string.Empty;

    public NoteEditorPage()
    {
        ViewModel = App.Services.GetRequiredService<NoteEditorViewModel>();
        InitializeComponent();

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;

        FileTreeVM.FileSelected += async filePath =>
        {
            await ViewModel.LoadFromFileAsync(filePath);
        };
        
        FileTreeVM.FolderSelected += folderPath =>
        {
            // При выборе папки загружаем её содержимое в дерево
            // Это позволит отобразить файлы папки в правой панели
            DispatcherQueue.TryEnqueue(() =>
            {
                // Находим узел папки и раскрываем его
                var node = FindNodeByPath(FileTreeVM.Roots, folderPath);
                if (node != null)
                {
                    node.IsExpanded = true;
                }
            });
        };
        
        // Инициализируем WebView2
        _ = InitializeMarkdownWebViewAsync();
    }

    /// <summary>
    /// Публичный метод для загрузки папки из MainWindow
    /// </summary>
    public void LoadFolderIntoTree(string folderPath)
    {
        FileTreeVM.Roots.Clear();
        FileTreeVM.LoadDirectory(folderPath);
    }

    /// <summary>
    /// Публичный метод для загрузки файлов по тегу из MainWindow
    /// </summary>
    public async Task LoadFilesByTagAsync(string tagName)
    {
        // Получаем список файлов с данным тегом из БД
        var filePaths = await FileTreeVM.GetFilesByTagNameAsync(tagName);
        
        if (filePaths.Count == 0)
        {
            // Если файлов нет, очищаем дерево
            FileTreeVM.Roots.Clear();
            return;
        }

        // Создаем временное дерево с найденными файлами
        // Группируем файлы по папкам для отображения
        var roots = new Dictionary<string, FileTreeNode>();
        
        foreach (var filePath in filePaths)
        {
            var fileInfo = new FileInfo(filePath);
            var dirPath = fileInfo.Directory?.FullName ?? string.Empty;
            
            // Находим или создаем корневую папку
            if (!roots.ContainsKey(dirPath))
            {
                var dirInfo = new DirectoryInfo(dirPath);
                roots[dirPath] = new FileTreeNode
                {
                    Name = dirInfo.Name,
                    FullPath = dirInfo.FullName,
                    IsDirectory = true,
                    IsExpanded = true
                };
            }

            // Добавляем файл в соответствующую папку
            var fileNode = new FileTreeNode
            {
                Name = fileInfo.Name,
                FullPath = fileInfo.FullName,
                IsDirectory = false
            };
            
            roots[dirPath].Children.Add(fileNode);
        }

        // Загружаем дерево
        FileTreeVM.Roots.Clear();
        foreach (var root in roots.Values)
        {
            FileTreeVM.Roots.Add(root);
        }
    }

    private async void PickFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var folderPicker = new Windows.Storage.Pickers.FolderPicker();

        // Специфика WinUI 3 для работы диалогов:
        // Используем корректное имя свойства MainWindowInstance, которое есть в вашем классе App
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance);

        if (hwnd == IntPtr.Zero) return; // Защита от непредвиденного сбоя дескриптора

        WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, hwnd);

        folderPicker.FileTypeFilter.Add("*");
        var folder = await folderPicker.PickSingleFolderAsync();

        if (folder != null)
        {
            // Очищаем старые корни дерева и строим новое
            FileTreeVM.Roots.Clear();

            // Создаем дерево через наш новый метод
            FileTreeVM.LoadDirectory(folder.Path);
        }
    }

    private async Task InitializeMarkdownWebViewAsync()
    {
        try
        {
            QuillEditor.DefaultBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            await QuillEditor.EnsureCoreWebView2Async();

            var settings = QuillEditor.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;

            LoadMarkdownEditor();

            try
            {
                await ViewModel.LoadNoteAsync();
            }
            catch (Microsoft.EntityFrameworkCore.DbUpdateException dbEx)
            {
                System.Diagnostics.Debug.WriteLine($"=== DbUpdateException ===");
                System.Diagnostics.Debug.WriteLine($"Message: {dbEx.Message}");
                System.Diagnostics.Debug.WriteLine($"Inner: {dbEx.InnerException?.Message}");
                System.Diagnostics.Debug.WriteLine($"Inner-Inner: {dbEx.InnerException?.InnerException?.Message}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WebView2 init failed: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"Inner: {ex.InnerException?.Message}");
        }
    }

    private void LoadMarkdownEditor()
    {
        // В CSS добавлен принудительный фокус и фикс для темной темы WinUI
        string htmlTemplate = """
        <!DOCTYPE html>
        <html>
        <head>
            <meta charset="utf-8" />
            <link rel="stylesheet" href="https://unpkg.com/easymde/dist/easymde.min.css">
            <style>
                html, body { height: 100%; margin: 0; padding: 0; background: transparent; overflow: hidden; }
                .EasyMDEContainer { height: 100vh; display: flex; flex-direction: column; }
                .CodeMirror { flex: 1; height: 100% !important; min-height: 100% !important; font-family: 'Segoe UI Variable Text', sans-serif; font-size: 14px; background: transparent !important; }
                
                /* Фикс фокуса: делаем область ввода явно кликабельной */
                .CodeMirror-wrap { cursor: text; }

                @media (prefers-color-scheme: dark) {
                    .CodeMirror { color: #fff; background: #202020 !important; }
                    .editor-toolbar { background: #2d2d2d; border-color: #444; }
                    .editor-toolbar button { color: #fff !important; }
                    .editor-toolbar button.active, .editor-toolbar button:hover { background: #444 !important; }
                    .CodeMirror-cursor { border-left: 2px solid white !important; }
                }
            </style>
        </head>
        <body>
            <textarea id="markdown-editor"></textarea>
            <script src="https://unpkg.com/easymde/dist/easymde.min.js"></script>
            <script>
                var easyMDE;
                
                function initializeEditor(initialText) {
                    easyMDE = new EasyMDE({
                        element: document.getElementById('markdown-editor'),
                        autoDownloadFontAwesome: true,
                        spellChecker: false,
                        status: false,
                        initialValue: initialText,
                        placeholder: "Type markdown here...",
                        toolbar: ["bold", "italic", "heading", "|", "quote", "unordered-list", "ordered-list", "|", "link", "image", "|", "preview", "side-by-side", "fullscreen"]
                    });
                    
                    easyMDE.codemirror.on('change', () => {
                        window.chrome.webview.postMessage(easyMDE.value());
                    });
                    
                    setTimeout(() => { easyMDE.codemirror.refresh(); }, 150);
                }

                function updateMarkdown(text) {
                    if (easyMDE && easyMDE.value() !== text) {
                        easyMDE.value(text);
                    }
                }
            </script>
        </body>
        </html>
        """;

        QuillEditor.NavigateToString(htmlTemplate);
    }

    private void QuillEditor_NavigationCompleted(WebView2 sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs args)
    {
            if (!args.IsSuccess) return;

            _isEditorReady = true;

            // ФИКС 4: Принудительный фокус сразу после навигации —
            // до вызова JS, чтобы Chromium уже держал фокус при initializeEditor
            QuillEditor.Focus(FocusState.Programmatic);

            string safeMarkdown = EscapeForJs(ViewModel.ContentMarkdown);
            _ = sender.ExecuteScriptAsync($"initializeEditor('{safeMarkdown}');");
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Обновляем JS только если изменения пришли извне (например, загрузилась другая заметка)
        if (e.PropertyName == nameof(ViewModel.ContentMarkdown) && _isEditorReady && QuillEditor.CoreWebView2 != null)
        {
            if (ViewModel.ContentMarkdown != _lastReceivedMarkdown)
            {
                DispatcherQueue.TryEnqueue(() =>  // ← обернуть в dispatcher
                {
                    string safeMarkdown = EscapeForJs(ViewModel.ContentMarkdown);
                    _ = QuillEditor.ExecuteScriptAsync($"updateMarkdown('{safeMarkdown}');");
                });
            }
        }
    }

    private void QuillEditor_WebMessageReceived(WebView2 sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs args)
    {
        string message = args.TryGetWebMessageAsString();

        // ФИКС 5: Убираем __FOCUS_REQUEST__ — он создавал бесполезный цикл.
        // WebView2 и так держит фокус после клика; дополнительный
        // Focus(Programmatic) вырывает фокус из Chromium обратно в WinUI.
        if (message == "__FOCUS_REQUEST__") return; // просто игнорируем

        _lastReceivedMarkdown = message;
        ViewModel.ContentMarkdown = message;
        _ = ViewModel.SaveAsync();
    }

    private async void SaveAsButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var savePicker = new FileSavePicker();

        // Безопасное извлечение дескриптора окна (hWnd) без Window.Current
        IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance);
        if (hWnd == IntPtr.Zero) return;

        WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hWnd);

        savePicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        savePicker.FileTypeChoices.Add("Markdown", new[] { ".md" });
        savePicker.FileTypeChoices.Add("Text", new[] { ".txt" });
        savePicker.SuggestedFileName = ViewModel.NoteTitle;

        var file = await savePicker.PickSaveFileAsync();
        if (file != null)
        {
            await FileIO.WriteTextAsync(file, ViewModel.ContentMarkdown);
        }
    }

    private string EscapeForJs(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value
            .Replace("\\", "\\\\")
            .Replace("'", "\\'")
            .Replace("\"", "\\\"")
            .Replace("\r", "")
            .Replace("\n", "\\n");
    }

    private FileTreeNode? FindNodeByPath(System.Collections.ObjectModel.ObservableCollection<FileTreeNode> nodes, string path)
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

    private void NewTagBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
            _ = ViewModel.AddTagAsync();
    }
}