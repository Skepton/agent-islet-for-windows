using System.Collections.ObjectModel;
using System.Windows.Input;
using Island.Core;

namespace Island.App;

public sealed record ProjectRow(
    string Name,
    string Path,
    string Status,
    string Decided,
    bool IsShown,
    ICommand Show,
    ICommand Ignore,
    ICommand Forget);

/// <summary>Backs the settings window: the projects the user chose to show or ignore. UI thread only.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly IslandHub _hub;
    private readonly bool _askingEnabled;

    /// <param name="askingEnabled">False when the app runs with <c>--allow-all-projects</c>.</param>
    public SettingsViewModel(IslandHub hub, bool askingEnabled)
    {
        _hub = hub;
        _askingEnabled = askingEnabled;
    }

    public ObservableCollection<ProjectRow> Projects { get; } = new();

    public string Explanation => _askingEnabled
        ? "The first time a session from a new folder reaches the island, it asks whether to show that project. " +
          "Ignored projects never appear; their agent works exactly as if the island were not running. " +
          "Forget a project to be asked again next time."
        : "Every project is shown: the app was started with --allow-all-projects, so choices below are not applied.";

    public string EmptyText => "No projects yet. Start an agent session in a project folder and the island will ask.";

    public bool IsEmpty => Projects.Count == 0;

    public void Refresh()
    {
        Projects.Clear();
        foreach (var entry in _hub.Projects)
        {
            var project = entry.Project;
            var shown = entry.Decision == ProjectDecision.Allowed;
            Projects.Add(new ProjectRow(
                project.Name,
                project.Path,
                shown ? "Shown" : "Ignored",
                entry.DecidedAt == default ? "" : entry.DecidedAt.LocalDateTime.ToString("g"),
                shown,
                new RelayCommand(() => Decide(project, ProjectDecision.Allowed)),
                new RelayCommand(() => Decide(project, ProjectDecision.Ignored)),
                new RelayCommand(() => Decide(project, ProjectDecision.Unknown))));
        }
        Raise(nameof(IsEmpty));
    }

    private void Decide(ProjectRef project, ProjectDecision decision)
    {
        _hub.DecideProject(project, decision);
        Refresh();
    }
}
