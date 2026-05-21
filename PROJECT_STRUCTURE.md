# WorkSpace — WinUI 3 Application
## Project Structure (MVVM + Clean Architecture)

```
WorkSpaceApp/
├── App.xaml / App.xaml.cs              # Application entry point, DI container
├── MainWindow.xaml / .cs               # Shell with NavigationView + Mica backdrop
│
├── Core/
│   ├── Models/                         # Domain entities (EF Core models)
│   │   ├── Note.cs
│   │   ├── Tag.cs
│   │   ├── DynamicTable.cs
│   │   ├── ChangeLog.cs
│   │   └── UserRole.cs
│   │
│   ├── Services/                       # Business logic services (interfaces + impls)
│   │   ├── IHardwareIdService.cs
│   │   ├── HardwareIdService.cs        # WMI-based UUID extraction
│   │   ├── ISignalRService.cs
│   │   ├── SignalRService.cs           # Heartbeat + online status hub client
│   │   ├── IStorageMonitorService.cs
│   │   └── StorageMonitorService.cs    # 1 GB limit enforcement
│   │
│   └── Helpers/
│       └── ThemeHelper.cs              # Dark/Light theme switching
│
├── Features/
│   ├── Notes/
│   │   ├── ViewModels/NoteEditorViewModel.cs
│   │   └── Views/NoteEditorPage.xaml   # Rich-text editor + tag manager + history log
│   │
│   ├── Database/
│   │   ├── ViewModels/DatabaseViewModel.cs
│   │   └── Views/DatabasePage.xaml     # Dynamic table view with audit trail
│   │
│   ├── Sync/
│   │   ├── ViewModels/CollaborationViewModel.cs
│   │   └── Views/CollaborationPage.xaml # SignalR status + role-based file sync
│   │
│   └── Auth/
│       ├── ViewModels/RegistrationViewModel.cs
│       └── Views/RegistrationPage.xaml  # Hardware-ID-based registration
│
├── Infrastructure/
│   ├── Data/
│   │   └── AppDbContext.cs             # EF Core DbContext with SQLite
│   └── Migrations/                     # EF auto-generated migrations
│
└── Assets/                             # Icons, images
```

## NuGet Packages Required
- Microsoft.WindowsAppSDK (WinUI 3)
- Microsoft.EntityFrameworkCore.Sqlite
- Microsoft.EntityFrameworkCore.Tools
- Microsoft.AspNetCore.SignalR.Client
- QuestPDF
- System.Management (WMI)
- CommunityToolkit.Mvvm (source-gen MVVM)
- CommunityToolkit.WinUI
