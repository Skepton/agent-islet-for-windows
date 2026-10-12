using System.Windows.Threading;
using Island.ClaudeCode;
using Island.Core;

namespace Island.App;

/// <summary>
/// <c>--demo</c>: cycles fake cards through the real overlay so the look can be tuned without a Claude Code session.
/// Permission requests use ids starting with <see cref="OverlayController.DemoPrefix"/>, so answering them stays local.
/// </summary>
public sealed class DemoDriver
{
    private const string AddRules = """{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"npm test:*"}],"behavior":"allow","destination":"localSettings"}""";

    private static readonly TimeSpan SceneLength = TimeSpan.FromSeconds(7);
    private static readonly string Agent = new ClaudeCodeAgent().DisplayName;

    private readonly OverlayController _overlay;
    private readonly DispatcherTimer _timer;
    private readonly Action[] _scenes;
    private int _next;

    public DemoDriver(OverlayController overlay)
    {
        _overlay = overlay;
        _scenes = new Action[] { Permission, NeedsInput, Finished, NewProject, Milestone35, Milestone90, ThreeSessions };
        _timer = new DispatcherTimer { Interval = SceneLength };
        _timer.Tick += (_, _) => NextScene();
    }

    public void Start()
    {
        _overlay.OnUsage(UsageUpdateFor(fiveHour: 28, sevenDay: 55));
        NextScene();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private void NextScene()
    {
        _overlay.Reset();
        _overlay.SessionCountOverride = 1;
        _scenes[_next]();
        _next = (_next + 1) % _scenes.Length;
    }

    private void Permission() => _overlay.OnEffects(new StoreEffect[] { PermissionEffect("demo-1", "s1", "my-project", "fix the failing tests") });

    private void NeedsInput() => _overlay.OnEffects(new StoreEffect[]
    {
        new NoticeShown("s1", "my-project", SessionPhase.NeedsInput, "Claude needs your input: which database should the migration target?",
            "add a migration for the new table", "VS Code", Agent),
    });

    private void Finished() => _overlay.OnEffects(new StoreEffect[]
    {
        new NoticeShown("s1", "my-project", SessionPhase.Finished,
            "I fixed the three failing tests in the parser module.\n\n- The date parser no longer drops the time zone.\n- Empty input now returns an empty list instead of throwing.\n- Added a regression test for each case.\n\nAll 214 tests pass.",
            "fix the failing tests", "VS Code", Agent),
    });

    private void NewProject() => _overlay.OnEffects(new StoreEffect[]
    {
        new ProjectApprovalRequested(new ProjectRef(OverlayController.DemoPrefix + "website", @"C:\Users\you\Projects\website", "website"), Agent),
    });

    private void Milestone35() => _overlay.OnUsage(UsageUpdateFor(fiveHour: 36, sevenDay: 55, new MilestoneEvent(UsageWindowId.FiveHour, 35, 36, Resets(3))));

    private void Milestone90() => _overlay.OnUsage(UsageUpdateFor(fiveHour: 92, sevenDay: 55, new MilestoneEvent(UsageWindowId.FiveHour, 90, 92, Resets(1))));

    private void ThreeSessions()
    {
        _overlay.SessionCountOverride = 3;
        _overlay.OnEffects(new StoreEffect[]
        {
            PermissionEffect("demo-2", "s1", "my-project", "fix the failing tests"),
            new NoticeShown("s2", "website", SessionPhase.NeedsInput, "Claude is waiting for your input", "redesign the footer", "Terminal", Agent),
            new NoticeShown("s3", "docs", SessionPhase.Finished, "Done. The README now explains the installer.", "update the README", "VS Code", Agent),
        });
    }

    private static PermissionShown PermissionEffect(string requestId, string sessionId, string name, string prompt) => new(
        sessionId,
        name,
        new PendingPermission(
            requestId, "Bash", "npm test -- --runInBand --coverage", "default",
            SuggestionChoice.FromRaw(new[] { AddRules }).Select(s => s.ToOption("Bash")).ToList(),
            "Run the test suite with coverage",
            ToolSummary.Headline("Bash"),
            ToolSummary.CommandPrefix("Bash")),
        prompt,
        "VS Code",
        Agent);

    private static DateTimeOffset Resets(int hours) => DateTimeOffset.UtcNow.AddHours(hours).AddMinutes(10);

    private static UsageUpdate UsageUpdateFor(double fiveHour, double sevenDay, params MilestoneEvent[] milestones)
    {
        var snapshot = new UsageSnapshot(DateTimeOffset.UtcNow, new[]
        {
            new UsageWindow(UsageWindowId.FiveHour, fiveHour, Resets(1)),
            new UsageWindow(UsageWindowId.SevenDay, sevenDay, DateTimeOffset.UtcNow.AddDays(2).AddHours(3)),
        });
        return new UsageUpdate(new UsageFetchResult(UsageFetchStatus.Ok, snapshot), milestones, snapshot, 0);
    }
}
