using CommunityToolkit.Mvvm.Input;

namespace silaa.ViewModels;

public enum NavigationSection
{
    Home,
    Projects,
    Apps,
    Memory,
    Vault,
    Browser,
    Skills,
    Automation,
    Schedule,
    PresentationTemplates,
    Settings,
    FolderSettings,
    SecuritySettings
}

public sealed class NavigationViewModel
{
    public NavigationViewModel(
        Func<Task> openHome,
        Func<Task> openMemory,
        Action openVault,
        Action openBrowser,
        Action openSettings,
        Action openSkills,
        Action openAutomation,
        Func<Task>? beginConversation = null,
        NavigationSection activeSection = NavigationSection.Home,
        Action? openFolderSettings = null,
        Action? openSecuritySettings = null,
        Func<Task>? openProjects = null,
        Func<Task>? openApps = null,
        Action? openPresentationTemplates = null,
        Func<Task>? openUsageGuide = null,
        Action? openSchedule = null)
    {
        ActiveSection = activeSection;
        OpenHomeCommand = new AsyncRelayCommand(openHome);
        OpenProjectsCommand = new AsyncRelayCommand(openProjects ?? openHome);
        OpenAppsCommand = new AsyncRelayCommand(openApps ?? openHome);
        BeginConversationCommand = new AsyncRelayCommand(beginConversation ?? openHome);
        OpenMemoryCommand = new AsyncRelayCommand(openMemory);
        OpenVaultCommand = new RelayCommand(openVault);
        OpenBrowserCommand = new RelayCommand(openBrowser);
        OpenSettingsCommand = new RelayCommand(openSettings);
        OpenFolderSettingsCommand = new RelayCommand(openFolderSettings ?? openSettings);
        OpenSecuritySettingsCommand = new RelayCommand(openSecuritySettings ?? openSettings);
        OpenSkillsCommand = new RelayCommand(openSkills);
        OpenAutomationCommand = new RelayCommand(openAutomation);
        OpenScheduleCommand = new RelayCommand(openSchedule ?? openAutomation);
        OpenPresentationTemplatesCommand = new RelayCommand(openPresentationTemplates ?? openSettings);
        OpenUsageGuideCommand = new AsyncRelayCommand(openUsageGuide ?? openHome);
    }

    public NavigationSection ActiveSection { get; }
    public bool IsHome => ActiveSection == NavigationSection.Home;
    public bool IsProjects => ActiveSection == NavigationSection.Projects;
    public bool IsApps => ActiveSection == NavigationSection.Apps;
    public bool IsMemory => ActiveSection == NavigationSection.Memory;
    public bool IsVault => ActiveSection == NavigationSection.Vault;
    public bool IsBrowser => ActiveSection == NavigationSection.Browser;
    public bool IsSkills => ActiveSection == NavigationSection.Skills;
    public bool IsAutomation => ActiveSection == NavigationSection.Automation;
    public bool IsSchedule => ActiveSection == NavigationSection.Schedule;
    public bool IsPresentationTemplates => ActiveSection == NavigationSection.PresentationTemplates;
    public bool IsSettings => ActiveSection == NavigationSection.Settings;
    public bool IsFolderSettings => ActiveSection == NavigationSection.FolderSettings;
    public bool IsSecuritySettings => ActiveSection == NavigationSection.SecuritySettings;
    public IAsyncRelayCommand BeginConversationCommand { get; }
    public IAsyncRelayCommand OpenHomeCommand { get; }
    public IAsyncRelayCommand OpenProjectsCommand { get; }
    public IAsyncRelayCommand OpenAppsCommand { get; }
    public IAsyncRelayCommand OpenMemoryCommand { get; }
    public IRelayCommand OpenVaultCommand { get; }
    public IRelayCommand OpenBrowserCommand { get; }
    public IRelayCommand OpenSettingsCommand { get; }
    public IRelayCommand OpenFolderSettingsCommand { get; }
    public IRelayCommand OpenSecuritySettingsCommand { get; }
    public IRelayCommand OpenSkillsCommand { get; }
    public IRelayCommand OpenAutomationCommand { get; }
    public IRelayCommand OpenScheduleCommand { get; }
    public IRelayCommand OpenPresentationTemplatesCommand { get; }
    public IAsyncRelayCommand OpenUsageGuideCommand { get; }
}
