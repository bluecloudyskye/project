using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Storage;
using Windows.Storage.Pickers;
using WorkSpaceApp.Core.Models;
using WorkSpaceApp.Core.Services;
using WorkSpaceApp.Features.Notes.ViewModels;
using WorkSpaceApp.Infrastructure.Data;

namespace WorkSpaceApp.Features.Notes.Views;

public sealed partial class NoteEditorPage : Page
{
    public FileTreeViewModel FileTreeVM { get; } = new();

    public NoteEditorViewModel ViewModel { get; }
    private bool _isEditorReady = false;
    private string _lastReceivedMarkdown = string.Empty;

    // ── Sync state ────────────────────────────────────────────
    private string?          _activeSyncToken;
    private string?          _activeSyncRole;         // "Viewer" | "Editor"
    private string?          _activeSyncServerUrl;
    private string?          _currentRemotePath;      // relative path of open remote file
    private SyncAdminSession? _adminSession;           // non-null when this instance is sharing

    public NoteEditorPage()
    {
        ViewModel = App.Services.GetRequiredService<NoteEditorViewModel>();
        InitializeComponent();

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;

        FileTreeVM.FileSelected += async filePath =>
        {
            await ViewModel.LoadFromFileAsync(filePath);
            _currentRemotePath = null; // local file — clear any remote context
        };

        FileTreeVM.FolderSelected += folderPath =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                var node = FindNodeByPath(FileTreeVM.Roots, folderPath);
                if (node != null) node.IsExpanded = true;
            });
        };

        FileTreeVM.RemoteFileSelected += async relativePath =>
        {
            await LoadRemoteFileAsync(relativePath);
        };

        this.ActualThemeChanged += (_, _) =>
        {
            if (!_isEditorReady || QuillEditor.CoreWebView2 == null) return;
            bool isDark = ActualTheme == ElementTheme.Dark;
            _ = QuillEditor.ExecuteScriptAsync($"setTheme({isDark.ToString().ToLower()});");
        };

        // Инициализируем WebView2
        _ = InitializeMarkdownWebViewAsync();
    }

    // ─────────────────────────────────────────────────────────
    // Sync — Share folder (admin side)
    // ─────────────────────────────────────────────────────────

    /// <summary>Called by FileTreeNodeView when user right-clicks a folder and chooses Share.</summary>
    public async void ShowShareFolderDialog(string folderPath, string folderName)
    {
        var roleBox = new ComboBox
        {
            Header       = "Permission for users",
            PlaceholderText = "Select role",
            SelectedIndex = 1,
            Margin        = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        roleBox.Items.Add("Viewer  (read-only)");
        roleBox.Items.Add("Editor  (can save files back)");
        roleBox.SelectedIndex = 0;

        var serverBox = new TextBox
        {
            Header       = "Server URL  (leave blank for local sharing)",
            Text         = "http://localhost:5000",
            Margin        = new Thickness(0, 8, 0, 0)
        };

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = $"Folder: {folderName}", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(roleBox);
        panel.Children.Add(serverBox);

        var dialog = new ContentDialog
        {
            Title             = "Share Folder",
            Content           = panel,
            PrimaryButtonText = "Create Share Link",
            CloseButtonText   = "Cancel",
            XamlRoot          = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var role      = roleBox.SelectedIndex == 1 ? "Editor" : "Viewer";
        var serverUrl = serverBox.Text.Trim();
        if (string.IsNullOrEmpty(serverUrl)) serverUrl = InProcessSyncServer.DefaultUrl;

        try
        {
            // Auto-start the embedded relay server if using localhost
            if (serverUrl.Contains("localhost") || serverUrl.Contains("127.0.0.1"))
                await InProcessSyncServer.EnsureStartedAsync();

            var sync = App.Services.GetRequiredService<ISyncService>();
            if (!sync.IsConnected)
                await sync.ConnectAsync(serverUrl);

            var hwId  = App.Services.GetRequiredService<IHardwareIdService>().GetHardwareId();
            var token = await sync.CreateShareAsync(folderName, role, hwId);
            var link  = ISyncService.BuildLink(serverUrl, token);

            // Start serving files automatically in background
            _adminSession?.Dispose();
            _adminSession = new SyncAdminSession(sync, folderPath, token, hwId);

            // Track active share so the Disconnect button works from the admin side too
            _activeSyncToken     = token;
            _activeSyncServerUrl = serverUrl;

            // Persist the link in the status bar so it stays visible after the popup
            SyncStatusText.Text      = $"Sharing active  ·  {link}";
            SyncStatusBar.Visibility = Visibility.Visible;

            // Show token to admin
            await ShowShareLinkDialogAsync(link, role, token);
        }
        catch (Exception ex)
        {
            var msg = ex.InnerException is not null
                ? $"{ex.Message}\n\n{ex.InnerException.Message}"
                : ex.Message;
            await ShowInfoDialogAsync("Error", $"Could not create share:\n{msg}");
        }
    }

    private async Task ShowShareLinkDialogAsync(string link, string role, string token)
    {
        var linkBox = new TextBox
        {
            Text         = link,
            IsReadOnly   = true,
            FontFamily   = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code, Consolas"),
            FontSize     = 12
        };

        var copyBtn = new Button
        {
            Content = "Copy to Clipboard",
            Margin  = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        copyBtn.Click += (_, _) =>
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(link);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
        };

        var accentBrush     = TryGetBrush("SystemAccentColorBrush")
                           ?? TryGetBrush("AccentFillColorDefaultBrush")
                           ?? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.SteelBlue);
        var secondaryBrush  = TryGetBrush("TextFillColorSecondaryBrush")
                           ?? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray);

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Text       = $"Share link created  |  Role: {role}",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = accentBrush
        });
        panel.Children.Add(new TextBlock
        {
            Text       = "Send this link to the people you want to invite:",
            FontSize   = 12,
            Margin     = new Thickness(0, 6, 0, 0),
            Foreground = secondaryBrush
        });
        panel.Children.Add(linkBox);
        panel.Children.Add(copyBtn);
        panel.Children.Add(new TextBlock
        {
            Text     = "The share is active as long as this app is open. The link is also shown in the status bar below the file tree.",
            FontSize = 11,
            Margin   = new Thickness(0, 8, 0, 0),
            Foreground = secondaryBrush
        });

        var dialog = new ContentDialog
        {
            Title          = "Share Active",
            Content        = panel,
            CloseButtonText= "Done",
            XamlRoot       = XamlRoot
        };
        await dialog.ShowAsync();
    }

    // ─────────────────────────────────────────────────────────
    // Sync — Connect (user side)
    // ─────────────────────────────────────────────────────────

    private async void ConnectShare_Click(object sender, RoutedEventArgs e)
    {
        var linkBox = new TextBox
        {
            Header          = "Share link or token",
            PlaceholderText = "wsync://A3F2C891@192.168.1.100:5000",
            Margin          = new Thickness(0, 0, 0, 4)
        };

        var pasteBtn = new Button
        {
            Content = "Paste from clipboard",
            Margin  = new Thickness(0, 0, 0, 8)
        };
        pasteBtn.Click += async (_, _) =>
        {
            try
            {
                var pkg = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                if (pkg.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                    linkBox.Text = await pkg.GetTextAsync();
            }
            catch { }
        };

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(pasteBtn);
        panel.Children.Add(linkBox);

        var dialog = new ContentDialog
        {
            Title             = "Connect to Shared Folder",
            Content           = panel,
            PrimaryButtonText = "Connect",
            CloseButtonText   = "Cancel",
            XamlRoot          = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var raw = linkBox.Text.Trim();
        var parsed = ISyncService.ParseLink(raw);
        if (parsed is null)
        {
            await ShowInfoDialogAsync("Invalid link", "The link format is not recognized.\nExpected: wsync://TOKEN@host:port  or an 8-character token.");
            return;
        }

        var (serverUrl, token) = parsed.Value;

        try
        {
            // Auto-start embedded relay if connecting to localhost
            if (serverUrl.Contains("localhost") || serverUrl.Contains("127.0.0.1"))
                await InProcessSyncServer.EnsureStartedAsync();

            var sync = App.Services.GetRequiredService<ISyncService>();
            if (!sync.IsConnected)
                await sync.ConnectAsync(serverUrl);

            var hwId   = App.Services.GetRequiredService<IHardwareIdService>().GetHardwareId();
            var result = await sync.JoinShareAsync(token, hwId);

            if (result is null || !string.IsNullOrEmpty(result.Error))
            {
                await ShowInfoDialogAsync("Connection failed", result?.Error ?? "No response from server.");
                return;
            }

            _activeSyncToken     = token;
            _activeSyncRole      = result.Role;
            _activeSyncServerUrl = serverUrl;

            // Register in sidebar
            App.Services.GetRequiredService<SharedFolderManager>()
                        .Add(token, result.FolderName, result.Role, serverUrl);

            // Subscribe to admin-disconnect so we can clean up
            sync.AdminDisconnected += OnAdminDisconnected;

            // Listen for permission-denied responses
            sync.PermissionDenied += async (_, msg) =>
                DispatcherQueue.TryEnqueue(async () =>
                    await ShowInfoDialogAsync("No Permission", msg));

            // Request the file list — populate tree when it arrives
            sync.FileListReceived += OnRemoteFileListReceived;
            await sync.RequestFileListAsync(token);

            // Update status bar
            DispatcherQueue.TryEnqueue(() =>
            {
                SyncStatusBar.Visibility = Visibility.Visible;
                SyncStatusText.Text      = $"Connected  ·  {result.FolderName}  ·  {result.Role}";
            });
        }
        catch (Exception ex)
        {
            await ShowInfoDialogAsync("Error", $"Could not connect: {ex.Message}");
        }
    }

    private void OnRemoteFileListReceived(object? sender, string[] files)
    {
        var sync = App.Services.GetRequiredService<ISyncService>();
        sync.FileListReceived -= OnRemoteFileListReceived;

        DispatcherQueue.TryEnqueue(() =>
        {
            FileTreeVM.LoadRemoteFiles(files, $"Shared ({_activeSyncRole})");
        });
    }

    private async Task LoadRemoteFileAsync(string relativePath)
    {
        if (_activeSyncToken is null) return;

        var sync    = App.Services.GetRequiredService<ISyncService>();
        var base64  = await sync.RequestFileContentAsync(_activeSyncToken, relativePath);

        if (base64 is null)
        {
            await ShowInfoDialogAsync("Error", "Could not load file from remote. Admin may be offline.");
            return;
        }

        var content = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        _currentRemotePath           = relativePath;
        ViewModel.NoteTitle          = Path.GetFileNameWithoutExtension(relativePath);
        ViewModel.ContentMarkdown    = content;
    }

    private void OnAdminDisconnected(object? sender, string msg)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_activeSyncToken is not null)
                App.Services.GetRequiredService<SharedFolderManager>().Remove(_activeSyncToken);

            SyncStatusBar.Visibility = Visibility.Collapsed;
            FileTreeVM.ClearRemoteFiles();
            _activeSyncToken     = null;
            _activeSyncRole      = null;
            _activeSyncServerUrl = null;
            _currentRemotePath   = null;
            _ = ShowInfoDialogAsync("Share Ended", msg);
        });
    }

    private async void DisconnectShare_Click(object sender, RoutedEventArgs e)
    {
        if (_activeSyncToken is not null)
            App.Services.GetRequiredService<SharedFolderManager>().Remove(_activeSyncToken);

        _activeSyncToken     = null;
        _activeSyncRole      = null;
        _activeSyncServerUrl = null;
        _currentRemotePath   = null;

        _adminSession?.Dispose();
        _adminSession = null;

        FileTreeVM.ClearRemoteFiles();

        SyncStatusBar.Visibility = Visibility.Collapsed;

        var sync = App.Services.GetRequiredService<ISyncService>();
        await sync.DisconnectAsync();
    }

    /// <summary>
    /// Called from MainWindow when the user clicks a shared folder in the Shared Access sidebar.
    /// Reconnects if needed, then populates the file tree with the remote file list.
    /// </summary>
    public async Task LoadSharedFolderAsync(SharedFolderEntry entry)
    {
        try
        {
            var sync = App.Services.GetRequiredService<ISyncService>();
            if (!sync.IsConnected)
                await sync.ConnectAsync(entry.ServerUrl);

            _activeSyncToken     = entry.Token;
            _activeSyncRole      = entry.Role;
            _activeSyncServerUrl = entry.ServerUrl;
            _currentRemotePath   = null;

            sync.FileListReceived += OnRemoteFileListReceived;
            await sync.RequestFileListAsync(entry.Token);

            DispatcherQueue.TryEnqueue(() =>
            {
                SyncStatusBar.Visibility = Visibility.Visible;
                SyncStatusText.Text      = $"Connected  ·  {entry.DisplayName}  ·  {entry.Role}";
            });
        }
        catch (Exception ex)
        {
            await ShowInfoDialogAsync("Error", $"Could not load shared folder: {ex.Message}");
        }
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
            FileTreeVM.LoadDirectory(folder.Path);

            // Pin in sidebar so it survives restarts
            string name = new System.IO.DirectoryInfo(folder.Path).Name;
            (App.MainWindowInstance as MainWindow)?.PinFolder(folder.Path, name);
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
        string htmlTemplate = """
        <!DOCTYPE html>
        <html>
        <head>
            <meta charset="utf-8" />
            <link rel="stylesheet" href="https://unpkg.com/easymde/dist/easymde.min.css">
            <style>
                html, body { height: 100%; margin: 0; padding: 0; background: transparent; overflow: hidden; }
                .EasyMDEContainer { height: 100vh; display: flex; flex-direction: column; }
                .CodeMirror { flex: 1; height: 100% !important; min-height: 100% !important; font-family: 'Segoe UI Variable Text', sans-serif; font-size: 14px; }
                .CodeMirror-wrap { cursor: text; }

                /* Dark theme — applied via JS from WinUI ActualTheme */
                body.dark-theme .EasyMDEContainer { background: #1e1e1e !important; }
                body.dark-theme .CodeMirror { color: #e8e8e8 !important; background: #1e1e1e !important; }
                body.dark-theme .CodeMirror-scroll { background: #1e1e1e !important; }
                body.dark-theme .CodeMirror-cursor { border-left: 2px solid #fff !important; }
                body.dark-theme .CodeMirror-selected { background: #264f78 !important; }
                body.dark-theme .editor-toolbar { background: #2d2d2d !important; border-color: #444 !important; }
                body.dark-theme .editor-toolbar button { color: #ccc !important; }
                body.dark-theme .editor-toolbar button.active,
                body.dark-theme .editor-toolbar button:hover { background: #444 !important; border-color: #666 !important; }
                body.dark-theme .editor-toolbar i.separator { border-left-color: #555 !important; }
                body.dark-theme .editor-preview { background: #1e1e1e !important; color: #e0e0e0 !important; border-color: #444 !important; }
                body.dark-theme .editor-preview-side { background: #1e1e1e !important; color: #e0e0e0 !important; border-color: #444 !important; }
            </style>
        </head>
        <body>
            <textarea id="markdown-editor"></textarea>
            <script src="https://unpkg.com/easymde/dist/easymde.min.js"></script>
            <script>
                var easyMDE;

                function setTheme(isDark) {
                    if (isDark) document.body.classList.add('dark-theme');
                    else document.body.classList.remove('dark-theme');
                }

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

                    setTimeout(() => {
                        easyMDE.codemirror.refresh();
                        if (!easyMDE.isPreviewActive()) easyMDE.togglePreview();
                    }, 150);
                }

                function updateMarkdown(text) {
                    if (easyMDE && easyMDE.value() !== text) {
                        easyMDE.value(text);
                        if (!easyMDE.isPreviewActive()) easyMDE.togglePreview();
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
        QuillEditor.Focus(FocusState.Programmatic);

        bool isDark = ActualTheme == ElementTheme.Dark;
        string safeMarkdown = EscapeForJs(ViewModel.ContentMarkdown);
        _ = sender.ExecuteScriptAsync($"setTheme({isDark.ToString().ToLower()}); initializeEditor('{safeMarkdown}');");
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

    private async void ExportPdfButton_Click(object sender, RoutedEventArgs e)
    {
        var savePicker = new FileSavePicker();

        IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance);
        if (hWnd == IntPtr.Zero) return;
        WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hWnd);

        savePicker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
        savePicker.FileTypeChoices.Add("PDF Document", new[] { ".pdf" });
        savePicker.SuggestedFileName = ViewModel.NoteTitle;

        var file = await savePicker.PickSaveFileAsync();
        if (file is null) return;

        try
        {
            await ViewModel.GeneratePdfAsync(file.Path);
        }
        catch (Exception ex)
        {
            await ShowInfoDialogAsync("Export Failed", ex.Message);
        }
    }

    private async void SaveAsButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        // ── Sync mode: push file to admin via server ───────────
        if (_activeSyncToken is not null && _currentRemotePath is not null)
        {
            if (_activeSyncRole?.Equals("Viewer", StringComparison.OrdinalIgnoreCase) == true)
            {
                await ShowInfoDialogAsync("No Permission",
                    "You don't have permission to save files.\nYou are connected as a Viewer.");
                return;
            }

            var sync   = App.Services.GetRequiredService<ISyncService>();
            var hwId   = App.Services.GetRequiredService<IHardwareIdService>().GetHardwareId();
            var bytes  = Encoding.UTF8.GetBytes(ViewModel.ContentMarkdown);
            var base64 = Convert.ToBase64String(bytes);

            await sync.PushFileAsync(_activeSyncToken, _currentRemotePath, base64, hwId);
            return;
        }

        // ── Normal mode: save-as dialog ────────────────────────
        var savePicker = new FileSavePicker();

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

    private static Microsoft.UI.Xaml.Media.Brush? TryGetBrush(string key)
    {
        if (Application.Current.Resources.TryGetValue(key, out var val) &&
            val is Microsoft.UI.Xaml.Media.Brush brush)
            return brush;
        return null;
    }

    private async Task ShowInfoDialogAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title          = title,
            Content        = message,
            CloseButtonText= "OK",
            XamlRoot       = XamlRoot
        };
        await dialog.ShowAsync();
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