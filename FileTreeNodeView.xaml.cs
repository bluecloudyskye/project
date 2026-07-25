using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Windows.Input;
using WorkSpaceApp.Core.Models;
using WorkSpaceApp.Features.Notes.ViewModels;

namespace WorkSpaceApp.Features.Notes.Views;

public sealed partial class FileTreeNodeView : UserControl
{
    public static readonly DependencyProperty NodeProperty =
        DependencyProperty.Register(nameof(Node), typeof(FileTreeNode),
            typeof(FileTreeNodeView), new PropertyMetadata(null));

    public static readonly DependencyProperty NodeClickedCommandProperty =
        DependencyProperty.Register(nameof(NodeClickedCommand), typeof(ICommand),
            typeof(FileTreeNodeView), new PropertyMetadata(null));

    public FileTreeNode? Node
    {
        get => (FileTreeNode?)GetValue(NodeProperty);
        set => SetValue(NodeProperty, value);
    }

    public ICommand? NodeClickedCommand
    {
        get => (ICommand?)GetValue(NodeClickedCommandProperty);
        set => SetValue(NodeClickedCommandProperty, value);
    }

    // Helper property to show tag indicators only for files with tags
    public bool IsFileWithTags => Node != null && !Node.IsDirectory && Node.Tags.Count > 0;

    public FileTreeNodeView()
    {
        InitializeComponent();

        // Подписываемся на загрузку элемента, чтобы поймать внутреннюю кнопку
        this.Loaded += FileTreeNodeView_Loaded;
    }

    private void FileTreeNodeView_Loaded(object sender, RoutedEventArgs e)
    {
        // Находим кнопку внутри нашего шаблона
        if (this.Content is StackPanel panel && panel.Children[0] is Button button)
        {
            button.Click += Button_Click;
        }
    }

    private void Button_Click(object sender, RoutedEventArgs e)
    {
        if (Node == null) return;

        // Both files and directories just need to execute the
        // nd now!
        if (NodeClickedCommand != null && NodeClickedCommand.CanExecute(Node))
        {
            NodeClickedCommand.Execute(Node);
        }
    }

    private void FileItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (Node == null) return;

        var menuFlyout = new MenuFlyout();

        if (Node.IsDirectory && !Node.IsRemote)
        {
            // ── Folder context menu ────────────────────────────
            var shareItem = new MenuFlyoutItem
            {
                Text = "Share Folder...",
                Icon = new FontIcon { Glyph = "" }
            };
            shareItem.Click += (_, _) => ShareFolder_Click();
            menuFlyout.Items.Add(shareItem);
        }
        else if (!Node.IsDirectory && !Node.IsRemote)
        {
            // ── File context menu — tag picker ─────────────────
            var presetTags = new[]
            {
                new Tag { Name = "Important",    Color = "#ff4757" },
                new Tag { Name = "Work",         Color = "#0067c0" },
                new Tag { Name = "Ideas",        Color = "#ffa502" },
                new Tag { Name = "Meeting Notes",Color = "#2ed573" }
            };

            foreach (var tag in presetTags)
            {
                var item = new MenuFlyoutItem { Text = tag.Name, Tag = tag };
                if (Node.Tags.Any(t => t.Name == tag.Name))
                    item.Icon = new SymbolIcon(Symbol.Accept);
                item.Click += (_, _) => ToggleTagForFile(tag);
                menuFlyout.Items.Add(item);
            }
        }

        if (menuFlyout.Items.Count == 0) return;
        menuFlyout.ShowAt(this);
        e.Handled = true;
    }

    private void ShareFolder_Click()
    {
        if (Node == null || !Node.IsDirectory) return;
        var page = GetParentPage();
        page?.ShowShareFolderDialog(Node.FullPath, Node.Name);
    }

    private async void ToggleTagForFile(Tag tag)
    {
        if (Node == null || string.IsNullOrEmpty(Node.FullPath)) return;

        // Получаем ViewModel через родительский элемент
        var page = GetParentPage();
        if (page?.FileTreeVM is not FileTreeViewModel vm) return;

        await vm.ToggleFileTagAsync(Node.FullPath, tag);
    }

    private NoteEditorPage? GetParentPage()
    {
        DependencyObject current = this;
        while (current != null)
        {
            if (current is NoteEditorPage page)
                return page;
            current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}