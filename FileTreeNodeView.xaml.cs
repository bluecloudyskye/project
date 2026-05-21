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

        // Both files and directories just need to execute the command now!
        if (NodeClickedCommand != null && NodeClickedCommand.CanExecute(Node))
        {
            NodeClickedCommand.Execute(Node);
        }
    }

    private void FileItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (Node == null || Node.IsDirectory) return;

        // Создаем контекстное меню для выбора тега
        var menuFlyout = new MenuFlyout();

        // Добавляем пункты для 4 предустановленных тегов
        var importantTag = new Tag { Name = "Important", Color = "#ff4757" };
        var workTag = new Tag { Name = "Work", Color = "#0067c0" };
        var ideasTag = new Tag { Name = "Ideas", Color = "#ffa502" };
        var meetingNotesTag = new Tag { Name = "Meeting Notes", Color = "#2ed573" };

        var presetTags = new[] { importantTag, workTag, ideasTag, meetingNotesTag };

        foreach (var tag in presetTags)
        {
            var menuItem = new MenuFlyoutItem
            {
                Text = tag.Name,
                Tag = tag
            };

            // Проверяем, установлен ли уже этот тег
            bool hasTag = Node.Tags.Any(t => t.Name == tag.Name);
            if (hasTag)
            {
                menuItem.Icon = new SymbolIcon(Symbol.Accept);
            }

            menuItem.Click += (s, args) => ToggleTagForFile(tag);
            menuFlyout.Items.Add(menuItem);
        }

        menuFlyout.ShowAt(this);
        e.Handled = true;
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
        var parent = this.Parent;
        while (parent != null)
        {
            if (parent is NoteEditorPage page)
                return page;
            parent = (parent as FrameworkElement)?.Parent;
        }
        return null;
    }
}