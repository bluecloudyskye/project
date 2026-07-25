// ============================================================
// MainWindow.xaml.cs
// Shell window: handles navigation, theme switching, sizing constraints,
// and live storage / connection status updates in the sidebar footer.
// ============================================================
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Networking.Connectivity;
using WorkSpaceApp.Core.Services;
using WorkSpaceApp.Infrastructure.Data;
using WorkSpaceApp.Features.Database.Views;
using WorkSpaceApp.Features.Notes.Views;
using WorkSpaceApp.Features.Reminders.Views;
using WorkSpaceApp.Features.Sync.Views;

namespace WorkSpaceApp;

public sealed partial class MainWindow : Window
{
    private readonly IStorageMonitorService _storage;
    private readonly ISignalRService        _signalR;
    private readonly IUserProfileService    _profile;
    private readonly SharedFolderManager   _sharedFolders;
    private readonly PinnedFoldersService  _pinnedFolders;

    private readonly SolidColorBrush _onlineBrush         = new(Colors.LimeGreen);
    private readonly SolidColorBrush _offlineBrush        = new(Colors.Red);
    private readonly SolidColorBrush _warningStorageBrush = new(Colors.OrangeRed);

    // token → NavigationViewItem mapping for quick updates
    private readonly Dictionary<string, NavigationViewItem> _sharedNavItems = new();

    // session tag → folder path (tags are GUIDs, paths are persisted by PinnedFoldersService)
    private readonly Dictionary<string, string> _pinnedFolderTags = new();

    private ElementTheme _currentTheme = ElementTheme.Default;
    private bool _isWindowActive = true;

    public static MainWindow Instance { get; private set; }

    public MainWindow(IStorageMonitorService storage, ISignalRService signalR,
                      IUserProfileService profile,
                      SharedFolderManager sharedFolders, PinnedFoldersService pinnedFolders)
    {
        Instance = this;
        InitializeComponent();

        _storage        = storage;
        _signalR        = signalR;
        _profile        = profile;
        _sharedFolders  = sharedFolders;
        _pinnedFolders  = pinnedFolders;

        ConfigureWindowDimensions();
        ApplySegoeUiVariable();

        this.Closed += (_, _) => _isWindowActive = false;
        _signalR.OwnerStatusChanged += OnOwnerStatusChanged;
        _profile.DisplayNameChanged += (_, name) =>
            DispatcherQueue.TryEnqueue(() => UserNameButton.Content = name);

        // Subscribe to shared folder collection changes
        _sharedFolders.Folders.CollectionChanged += OnSharedFoldersChanged;

        ContentFrame.Navigate(typeof(NoteEditorPage));
        NavView.SelectedItem = DashboardItem;

        UserNameButton.Content = _profile.DisplayName;

        _ = RefreshStorageLoopAsync();
        RestorePinnedFolders();
    }

    /// <summary>
    /// Configures the base launch size for WinUI 3 windows using Win32 Interop.
    /// </summary>
    private void ConfigureWindowDimensions()
    {
        IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Microsoft.UI.WindowId windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd); 
        AppWindow appWindow = AppWindow.GetFromWindowId(windowId);

        if (appWindow != null)
        {
            // Set launching size to match requested 1024x680 Layout
            appWindow.Resize(new Windows.Graphics.SizeInt32(1024, 680));
        }
    }

    // ?????????????????????????????????????????????????????????
    // Navigation
    // ?????????????????????????????????????????????????????????
    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs e)
    {
        if (e.SelectedItemContainer is not NavigationViewItem item) return;

        string? tag = item.Tag?.ToString();
        
        // Pinned folder item — load its tree directly
        if (tag != null && tag.StartsWith("PinnedFolder_"))
        {
            HandlePinnedFolderSelection(tag);
            return;
        }

        // Проверяем, выбрана ли папка из раздела Folders
        if (tag != null && tag.StartsWith("Folder_"))
        {
            HandleFolderSelection(tag, item);
            return;
        }
        
        // Проверяем, выбран ли тег из раздела Tags
        if (tag != null && tag.StartsWith("Tag_"))
        {
            HandleTagSelection(tag, item);
            return;
        }

        // Shared folder item selected from Shared Access section
        if (tag != null && tag.StartsWith("SharedFolder_"))
        {
            _ = HandleSharedFolderSelectionAsync(tag);
            return;
        }

        Type? page = tag switch
        {
            "Dashboard" => typeof(NoteEditorPage),
            "Database" => typeof(DatabasePage),
            "Reminders" => typeof(RemindersPage),
            _ => null
        };

        if (page is not null && ContentFrame.CurrentSourcePageType != page)
        {
            ContentFrame.Navigate(page);
        }
    }

    private async void HandleTagSelection(string tag, NavigationViewItem item)
    {
        string tagName = tag.Replace("Tag_", "");

        // Navigate to Dashboard if we're not already there
        if (ContentFrame.Content is not NoteEditorPage)
            ContentFrame.Navigate(typeof(NoteEditorPage));

        if (ContentFrame.Content is NoteEditorPage notePage)
            await notePage.LoadFilesByTagAsync(tagName);
    }

    private void HandleFolderSelection(string tag, NavigationViewItem item)
    {
        // Извлекаем имя папки из тега (например, "Folder_ProjectNotes" -> "Project Notes")
        string folderName = tag.Replace("Folder_", "").Replace("_", " ");
        
        // Показываем диалог выбора папки
        _ = SelectFolderAndAddToSidebarAsync(folderName, item);
    }

    private async Task SelectFolderAndAddToSidebarAsync(string folderName, NavigationViewItem folderItem)
    {
        var folderPicker = new Windows.Storage.Pickers.FolderPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd == IntPtr.Zero) return;

        WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, hwnd);
        folderPicker.FileTypeFilter.Add("*");

        var folder = await folderPicker.PickSingleFolderAsync();
        if (folder is null) return;

        string name = new DirectoryInfo(folder.Path).Name;
        PinFolder(folder.Path, name);

        if (ContentFrame.Content is NoteEditorPage notePage)
            notePage.LoadFolderIntoTree(folder.Path);
    }

    /// <summary>Saves a folder to the pinned list and adds it to the sidebar. Safe to call multiple times.</summary>
    public void PinFolder(string path, string name)
    {
        _pinnedFolders.Add(name, path);
        AddFolderToSidebar(name, path);
    }

    private void AddFolderToSidebar(string folderName, string folderPath)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            // Skip if already showing
            if (_pinnedFolderTags.ContainsValue(folderPath)) return;

            var tag = $"PinnedFolder_{Guid.NewGuid():N}";
            _pinnedFolderTags[tag] = folderPath;

            var item = new NavigationViewItem { Tag = tag };
            item.Icon = new FontIcon { Glyph = "" };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameText  = new TextBlock { Text = folderName, FontSize = 12 };
            var countText = new TextBlock
            {
                FontSize   = 11,
                Foreground = (SolidColorBrush)App.Current.Resources["TextFillColorSecondaryBrush"],
                Margin     = new Thickness(8, 0, 0, 0)
            };
            Grid.SetColumn(countText, 1);
            grid.Children.Add(nameText);
            grid.Children.Add(countText);
            item.Content = grid;

            // Right-click → Remove from Sidebar
            var flyout     = new MenuFlyout();
            var removeItem = new MenuFlyoutItem
            {
                Text = "Remove from Sidebar",
                Icon = new FontIcon { Glyph = "" }
            };
            string capturedTag  = tag;
            string capturedPath = folderPath;
            removeItem.Click += (_, _) => RemovePinnedFolder(capturedTag, capturedPath);
            flyout.Items.Add(removeItem);
            item.ContextFlyout = flyout;

            FoldersNavItem.MenuItems.Add(item);
            UpdateFolderItemCount(item, folderPath);
        });
    }

    private void HandlePinnedFolderSelection(string tag)
    {
        if (!_pinnedFolderTags.TryGetValue(tag, out var path)) return;
        if (ContentFrame.CurrentSourcePageType != typeof(NoteEditorPage))
            ContentFrame.Navigate(typeof(NoteEditorPage));
        if (ContentFrame.Content is NoteEditorPage notePage)
            notePage.LoadFolderIntoTree(path);
    }

    private void RemovePinnedFolder(string tag, string path)
    {
        _pinnedFolders.Remove(path);
        _pinnedFolderTags.Remove(tag);
        DispatcherQueue.TryEnqueue(() =>
        {
            var item = FoldersNavItem.MenuItems
                .OfType<NavigationViewItem>()
                .FirstOrDefault(i => i.Tag?.ToString() == tag);
            if (item is not null)
                FoldersNavItem.MenuItems.Remove(item);
        });
    }

    private void RestorePinnedFolders()
    {
        foreach (var pf in _pinnedFolders.Load())
        {
            if (Directory.Exists(pf.Path))
                AddFolderToSidebar(pf.Name, pf.Path);
        }
    }

    private async void UpdateFolderItemCount(NavigationViewItem folderItem, string folderPath)
    {
        try
        {
            int fileCount = Directory.GetFiles(folderPath, "*.md", SearchOption.AllDirectories).Length;
            
            DispatcherQueue.TryEnqueue(() =>
            {
                // Находим Grid с количеством файлов и обновляем его
                if (folderItem.Content is Grid grid)
                {
                    if (grid.Children.Count > 1 && grid.Children[1] is TextBlock countText)
                    {
                        countText.Text = fileCount.ToString();
                    }
                }
            });
        }
        catch
        {
            // Игнорируем ошибки подсчета
        }
    }

    // ?????????????????????????????????????????????????????????
    // Theme toggle (Light ? Dark)
    // ?????????????????????????????????????????????????????????
    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        if (Content is FrameworkElement root)
        {
            _currentTheme = _currentTheme == ElementTheme.Dark
                ? ElementTheme.Light
                : ElementTheme.Dark;

            root.RequestedTheme = _currentTheme;

            // Swap icon: Moon = dark mode style, Sun = light mode style
            ThemeIcon.Glyph = _currentTheme == ElementTheme.Dark
                ? "\uE706"   // Sun Icon
                : "\uE708";  // Moon Icon
        }
    }

    // ?????????????????????????????????????????????????????????
    // SignalR status callback
    // ?????????????????????????????????????????????????????????
    private void OnOwnerStatusChanged(object? sender, OwnerStatusChangedEventArgs e)
    {
        // Marshal execution seamlessly to the UI thread
        DispatcherQueue.TryEnqueue(() =>
        {
            ConnectionDot.Fill = e.IsOnline ? _onlineBrush : _offlineBrush;
            ConnectionLabel.Text = e.IsOnline ? "Connected" : "Disconnected";
        });
    }

    // ?????????????????????????????????????????????????????????
    // User display name — click to rename
    // ?????????????????????????????????????????????????????????
    private async void UserNameButton_Click(object sender, RoutedEventArgs e)
    {
        var input = new TextBox
        {
            PlaceholderText = "Enter your public name",
            Text            = _profile.DisplayName,
            MaxLength       = 64
        };

        var dialog = new ContentDialog
        {
            Title             = "Change display name",
            Content           = input,
            PrimaryButtonText = "Save",
            CloseButtonText   = "Cancel",
            XamlRoot          = Content.XamlRoot,
            DefaultButton     = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        string name = input.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        await _profile.UpdateDisplayNameAsync(name);
    }

    // ?????????????????????????????????????????????????????????
    // Storage bar refresh loop (every 5 seconds)
    // ?????????????????????????????????????????????????????????
    private async Task RefreshStorageLoopAsync()
    {
        // Loop terminates naturally if the UI window is destroyed
        while (_isWindowActive)
        {
            double usageMb = _storage.GetDatabaseSizeBytes() / 1_048_576.0;
            double limitMb = _storage.LimitBytes / 1_048_576.0;
            double ratio = _storage.UsageRatio * 100;

            DispatcherQueue.TryEnqueue(() =>
            {
                StorageBar.Value = ratio;
                StorageLabel.Text = $"{usageMb:F0} MB / {limitMb / 1024:F0} GB";

                // Warn user visually when database usage breaks past 80%
                StorageBar.Foreground = ratio > 80 ? _warningStorageBrush : null;
            });

            await Task.Delay(5_000);
        }
    }

    // ?????????????????????????????????????????????????????????
    // Apply Segoe UI Variable as the global font
    // ?????????????????????????????????????????????????????????
    private void ApplySegoeUiVariable()
    {
        // ??????????: FrameworkElement ?? ????? ???????? FontFamily.
        // ?? ???????? ???????? ??????? ? Control (????????, ? ?????? ????????? Grid/Page), ? ???????? ??? ???????? ????.
        if (Content is Control rootControl)
        {
            rootControl.FontFamily = new FontFamily("Segoe UI Variable");
        }
    }

    // ?????????????????????????????????????????????????????????
    // Shared Access — dynamic sidebar items
    // ?????????????????????????????????????????????????????????

    private void OnSharedFoldersChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            switch (e.Action)
            {
                case System.Collections.Specialized.NotifyCollectionChangedAction.Add:
                    if (e.NewItems is null) break;
                    foreach (SharedFolderEntry entry in e.NewItems)
                        AddSharedFolderItem(entry);
                    break;

                case System.Collections.Specialized.NotifyCollectionChangedAction.Remove:
                    if (e.OldItems is null) break;
                    foreach (SharedFolderEntry entry in e.OldItems)
                        RemoveSharedFolderItem(entry.Token);
                    break;

                case System.Collections.Specialized.NotifyCollectionChangedAction.Reset:
                    foreach (var item in _sharedNavItems.Values)
                        SharedAccessNavItem.MenuItems.Remove(item);
                    _sharedNavItems.Clear();
                    break;
            }
        });
    }

    private void AddSharedFolderItem(SharedFolderEntry entry)
    {
        var item = new NavigationViewItem
        {
            Tag     = $"SharedFolder_{entry.Token}",
            Content = entry.DisplayName
        };
        item.Icon = new FontIcon { Glyph = "" };

        var flyout = new MenuFlyout();

        var renameItem = new MenuFlyoutItem
        {
            Text = "Rename",
            Icon = new FontIcon { Glyph = "" }
        };
        renameItem.Click += async (_, _) => await RenameSharedFolderAsync(entry, item);

        var deleteItem = new MenuFlyoutItem
        {
            Text = "Delete",
            Icon = new FontIcon { Glyph = "" }
        };
        deleteItem.Click += async (_, _) => await DeleteSharedFolderAsync(entry);

        var saveCopyItem = new MenuFlyoutItem
        {
            Text = "Save Copy",
            Icon = new FontIcon { Glyph = "" }
        };
        saveCopyItem.Click += async (_, _) => await SaveCopySharedFolderAsync(entry);

        flyout.Items.Add(renameItem);
        flyout.Items.Add(deleteItem);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(saveCopyItem);

        item.ContextFlyout = flyout;

        _sharedNavItems[entry.Token] = item;
        SharedAccessNavItem.MenuItems.Add(item);
    }

    private void RemoveSharedFolderItem(string token)
    {
        if (!_sharedNavItems.TryGetValue(token, out var item)) return;
        SharedAccessNavItem.MenuItems.Remove(item);
        _sharedNavItems.Remove(token);
    }

    private async Task HandleSharedFolderSelectionAsync(string tag)
    {
        var token = tag["SharedFolder_".Length..];
        var entry = _sharedFolders.Get(token);
        if (entry is null) return;

        if (ContentFrame.CurrentSourcePageType != typeof(NoteEditorPage))
            ContentFrame.Navigate(typeof(NoteEditorPage));

        if (ContentFrame.Content is NoteEditorPage notePage)
            await notePage.LoadSharedFolderAsync(entry);
    }

    private async Task RenameSharedFolderAsync(SharedFolderEntry entry, NavigationViewItem item)
    {
        var nameBox = new TextBox
        {
            Header = "New name",
            Text   = entry.DisplayName
        };

        var dialog = new ContentDialog
        {
            Title             = "Rename Folder",
            Content           = nameBox,
            PrimaryButtonText = "Rename",
            CloseButtonText   = "Cancel",
            XamlRoot          = Content?.XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var newName = nameBox.Text.Trim();
        if (string.IsNullOrEmpty(newName)) return;

        entry.DisplayName = newName;
        DispatcherQueue.TryEnqueue(() => item.Content = newName);
    }

    private async Task DeleteSharedFolderAsync(SharedFolderEntry entry)
    {
        var dialog = new ContentDialog
        {
            Title             = "Remove Connection",
            Content           = $"Remove \"{entry.DisplayName}\" from Shared Access?",
            PrimaryButtonText = "Remove",
            CloseButtonText   = "Cancel",
            XamlRoot          = Content?.XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        _sharedFolders.Remove(entry.Token);
    }

    private async Task SaveCopySharedFolderAsync(SharedFolderEntry entry)
    {
        var folderPicker = new Windows.Storage.Pickers.FolderPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd == IntPtr.Zero) return;

        WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, hwnd);
        folderPicker.FileTypeFilter.Add("*");

        var folder = await folderPicker.PickSingleFolderAsync();
        if (folder is null) return;

        await SaveCopyAsync(entry, folder.Path);
    }

    private async Task SaveCopyAsync(SharedFolderEntry entry, string destFolder)
    {
        try
        {
            var sync = App.Services.GetRequiredService<ISyncService>();
            if (!sync.IsConnected)
                await sync.ConnectAsync(entry.ServerUrl);

            // Request the file list and wait for the response
            string[]? files = null;
            var tcs = new System.Threading.Tasks.TaskCompletionSource<string[]>();

            void OnList(object? _, string[] f)
            {
                sync.FileListReceived -= OnList;
                tcs.TrySetResult(f);
            }
            sync.FileListReceived += OnList;
            await sync.RequestFileListAsync(entry.Token);

            try   { files = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch { files = null; }
            finally { sync.FileListReceived -= OnList; }

            if (files is null || files.Length == 0) return;

            // Download and write each file
            foreach (var relativePath in files)
            {
                var base64 = await sync.RequestFileContentAsync(entry.Token, relativePath);
                if (base64 is null) continue;

                var bytes    = Convert.FromBase64String(base64);
                var destPath = Path.Combine(destFolder, relativePath.Replace('/', Path.DirectorySeparatorChar));
                var dir      = Path.GetDirectoryName(destPath);
                if (dir is not null) Directory.CreateDirectory(dir);
                await File.WriteAllBytesAsync(destPath, bytes);
            }
        }
        catch (Exception ex)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                var dlg = new ContentDialog
                {
                    Title           = "Save Copy Failed",
                    Content         = ex.Message,
                    CloseButtonText = "OK",
                    XamlRoot        = Content?.XamlRoot
                };
                await dlg.ShowAsync();
            });
        }
    }
}