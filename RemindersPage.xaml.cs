// RemindersPage.xaml.cs
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using WorkSpaceApp.Features.Reminders.ViewModels;

namespace WorkSpaceApp.Features.Reminders.Views;

public sealed partial class RemindersPage : Page
{
    public RemindersViewModel ViewModel { get; }

    public RemindersPage()
    {
        ViewModel = App.Services.GetRequiredService<RemindersViewModel>();
        InitializeComponent();
    }
}
