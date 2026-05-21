// ============================================================
// MainWindow.xaml.cs
// Shell window: handles navigation, theme switching, sizing constraints,
// and live storage / connection status updates in the sidebar footer.
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Networking.Connectivity;
using WorkSpaceApp.Core.Services;
using WorkSpaceApp.Features.Database.Views;
using WorkSpaceApp.Features.Notes.Views;
using WorkSpaceApp.Features.Reminders.Views;
using WorkSpaceApp.Features.Sync.Views;

namespace WorkSpaceApp;

public sealed partial class MainWindow : Window
{
    private readonly IStorageMonitorService _storage;
    private readonly ISignalRService _signalR;

    // Cached brushes to prevent continuous UI thread allocations
    private readonly SolidColorBrush _onlineBrush = new(Colors.LimeGreen);
    private readonly SolidColorBrush _offlineBrush = new(Colors.Red);
    private readonly SolidColorBrush _warningStorageBrush = new(Colors.OrangeRed);

    private ElementTheme _currentTheme = ElementTheme.Default;
    private bool _isWindowActive = true;

    public static MainWindow Instance { get; private set; }

    public MainWindow(IStorageMonitorService storage, ISignalRService signalR)
    {
        // ????????????? ??????????? (?????????? ????? ????? ??????????? XAML)
        Instance = this;
        InitializeComponent();

        _storage = storage;
        _signalR = signalR;

        // Apply native WinUI 3 window size constraints (1024x680)
        ConfigureWindowDimensions();

        // Apply system font
        ApplySegoeUiVariable();

        // Wire up tracking and status events
        this.Closed += (_, _) => _isWindowActive = false;
        _signalR.OwnerStatusChanged += OnOwnerStatusChanged;

        // Navigate to Dashboard on load
        ContentFrame.Navigate(typeof(NoteEditorPage));
        NavView.SelectedItem = DashboardItem;

        // Start background storage monitoring
        _ = RefreshStorageLoopAsync();
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
    // Словарь для отслеживания открытых папок
    private readonly Dictionary<string, string> _openedFolders = new();

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs e)
    {
        if (e.SelectedItemContainer is not NavigationViewItem item) return;

        string? tag = item.Tag?.ToString();
        
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
        // Извлекаем имя тега (например, "Tag_Important" -> "Important")
        string tagName = tag.Replace("Tag_", "");
        
        // Если сейчас открыта страница Dashboard, загружаем файлы с этим тегом
        if (ContentFrame.Content is NoteEditorPage notePage)
        {
            await notePage.LoadFilesByTagAsync(tagName);
        }
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
        
        if (folder != null)
        {
            // Сохраняем путь к папке
            _openedFolders[folderName] = folder.Path;
            
            // Добавляем новую папку в левый сайдбар
            AddFolderToSidebar(folderName, folder.Path);
            
            // Если сейчас открыта страница Dashboard, загружаем выбранную папку
            if (ContentFrame.Content is NoteEditorPage notePage)
            {
                notePage.LoadFolderIntoTree(folder.Path);
            }
        }
    }

    private void AddFolderToSidebar(string folderName, string folderPath)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            // Создаем новый элемент навигации для папки
            var newItem = new NavigationViewItem
            {
                Tag = $"Folder_{folderName.Replace(" ", "_")}",
                IsExpanded = true
            };

            // Создаем Grid с именем папки и счетчиком файлов
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto } );

            var nameText = new TextBlock
            {
                Text = folderName,
                FontSize = 12
            };

            var countText = new TextBlock
            {
                FontSize = 11,
                Foreground = (SolidColorBrush)App.Current.Resources["TextFillColorSecondaryBrush"],
                Margin = new Thickness(8, 0, 0, 0)
            };

            Grid.SetColumn(countText, 1);
            grid.Children.Add(nameText);
            grid.Children.Add(countText);

            newItem.Content = grid;

            // Добавляем элемент в список папок
            FoldersNavItem.MenuItems.Add(newItem);

            // Обновляем количество файлов
            UpdateFolderItemCount(newItem, folderPath);
        });
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
}