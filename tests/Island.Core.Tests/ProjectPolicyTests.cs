using System.Collections.Concurrent;
using Island.Core;

namespace Island.Core.Tests;

public sealed class ProjectPolicyTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "island-projects-" + Guid.NewGuid().ToString("N"));

    private string StatePath => Path.Combine(_dir, "projects.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static HookFrame Frame(string evt, string id = "r", string? project = @"C:\work\my-app", string session = "s1") =>
        new(id, TestAgent.AgentId, 0, 0, evt == "permission", TestAgent.Payload(evt, session), Project: project);

    private (IslandHub Hub, ConcurrentQueue<StoreEffect> Effects, SessionStore Store) Hub(ProjectPolicy policy)
    {
        var effects = new ConcurrentQueue<StoreEffect>();
        var store = new SessionStore();
        var hub = new IslandHub(store, new PermissionBroker(), new IAgentIntegration[] { new TestAgent() },
            e => { foreach (var x in e) effects.Enqueue(x); }, () => T0, policy);
        return (hub, effects, store);
    }

    [Theory]
    [InlineData(@"C:\Work\My-App\", @"C:\Work\My-App", "My-App")]
    [InlineData("C:/Work/My-App", @"C:\Work\My-App", "My-App")]
    [InlineData("  c:\\work\\my-app  ", @"c:\work\my-app", "my-app")]
    [InlineData("C:", @"C:\", "C:")]
    public void Paths_are_normalised_and_compared_without_case(string input, string path, string name)
    {
        var project = ProjectRef.From(input)!;

        Assert.Equal(path, project.Path);
        Assert.Equal(name, project.Name);
        Assert.Equal(ProjectRef.From(@"c:\WORK\my-app")!.Key, ProjectRef.From(@"C:\work\My-App\")!.Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_paths_are_no_project(string? input)
    {
        Assert.Null(ProjectRef.From(input));
    }

    [Fact]
    public void Choices_survive_a_restart_and_forgetting_removes_them()
    {
        var first = new ProjectPolicy(new ProjectPolicyStore(StatePath));
        first.Set(ProjectRef.From(@"C:\a")!, ProjectDecision.Allowed, T0);
        first.Set(ProjectRef.From(@"C:\b")!, ProjectDecision.Ignored, T0);
        first.Set(ProjectRef.From(@"C:\c")!, ProjectDecision.Allowed, T0);
        first.Set(ProjectRef.From(@"C:\c")!, ProjectDecision.Unknown, T0);

        var second = new ProjectPolicy(new ProjectPolicyStore(StatePath));

        Assert.Equal(ProjectDecision.Allowed, second.Check(ProjectRef.From(@"c:\A")!));
        Assert.Equal(ProjectDecision.Ignored, second.Check(ProjectRef.From(@"C:\b")!));
        Assert.Equal(ProjectDecision.Unknown, second.Check(ProjectRef.From(@"C:\c")!));
        Assert.Equal(2, second.List().Count);
        Assert.Equal(T0, second.List()[0].DecidedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"v":99,"projects":[]}""")]
    [InlineData("""{"v":1,"projects":[{"path":"C:\\a","decision":"maybe"},{"decision":"allowed"}]}""")]
    public void A_missing_or_damaged_file_means_no_choices_yet(string content)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath, content);

        Assert.Empty(new ProjectPolicyStore(StatePath).Load());
        Assert.Empty(new ProjectPolicyStore(Path.Combine(_dir, "missing.json")).Load());
    }

    [Fact]
    public async Task A_new_project_is_asked_about_once_and_its_events_are_dropped()
    {
        var (hub, effects, store) = Hub(new ProjectPolicy());

        Assert.Null(await hub.HandleAsync(Frame("finished", "1"), CancellationToken.None));
        Assert.Null(await hub.HandleAsync(Frame("finished", "2"), CancellationToken.None));

        var asked = Assert.Single(effects.OfType<ProjectApprovalRequested>());
        Assert.Equal("my-app", asked.Project.Name);
        Assert.Equal("Test Agent", asked.Agent);
        Assert.Empty(effects.OfType<NoticeShown>());
        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public async Task A_permission_request_from_a_new_project_gets_no_decision_at_once()
    {
        var (hub, _, _) = Hub(new ProjectPolicy());

        var result = await hub.HandleAsync(Frame("permission"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(result);
    }

    [Fact]
    public async Task Showing_a_project_lets_its_next_events_through()
    {
        var (hub, effects, store) = Hub(new ProjectPolicy());
        await hub.HandleAsync(Frame("finished", "1"), CancellationToken.None);

        hub.DecideProject(ProjectRef.From(@"C:\work\my-app")!, ProjectDecision.Allowed);
        await hub.HandleAsync(Frame("finished", "2"), CancellationToken.None);

        Assert.Contains(effects, e => e is ProjectDecided { Decision: ProjectDecision.Allowed });
        Assert.Single(effects.OfType<NoticeShown>());
        Assert.Equal(@"c:\work\my-app", store.Snapshot().Single().Project);
    }

    [Fact]
    public async Task Ignoring_a_shown_project_drops_its_sessions_and_releases_its_request()
    {
        var policy = new ProjectPolicy();
        policy.Set(ProjectRef.From(@"C:\work\my-app")!, ProjectDecision.Allowed, T0);
        var (hub, effects, store) = Hub(policy);
        var pending = hub.HandleAsync(Frame("permission", "req"), CancellationToken.None);
        await Eventually(() => store.Snapshot().Any(s => s.Pending is not null));

        hub.DecideProject(ProjectRef.From(@"C:\work\my-app")!, ProjectDecision.Ignored);

        Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains(effects, e => e is SessionRemoved);
        Assert.Empty(store.Snapshot());
        Assert.Null(await hub.HandleAsync(Frame("finished", "later"), CancellationToken.None));
        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public async Task Other_projects_keep_their_sessions_when_one_is_ignored()
    {
        var policy = new ProjectPolicy();
        policy.Set(ProjectRef.From(@"C:\a")!, ProjectDecision.Allowed, T0);
        policy.Set(ProjectRef.From(@"C:\b")!, ProjectDecision.Allowed, T0);
        var (hub, _, store) = Hub(policy);
        await hub.HandleAsync(Frame("finished", "1", @"C:\a", "sa"), CancellationToken.None);
        await hub.HandleAsync(Frame("finished", "2", @"C:\b", "sb"), CancellationToken.None);

        hub.DecideProject(ProjectRef.From(@"C:\a")!, ProjectDecision.Ignored);

        Assert.Equal("sb", store.Snapshot().Single().SessionId);
    }

    [Fact]
    public async Task A_forgotten_project_is_asked_about_again()
    {
        var policy = new ProjectPolicy();
        policy.Set(ProjectRef.From(@"C:\work\my-app")!, ProjectDecision.Ignored, T0);
        var (hub, effects, _) = Hub(policy);
        await hub.HandleAsync(Frame("finished", "1"), CancellationToken.None);
        Assert.Empty(effects.OfType<ProjectApprovalRequested>());

        hub.DecideProject(ProjectRef.From(@"C:\work\my-app")!, ProjectDecision.Unknown);
        await hub.HandleAsync(Frame("finished", "2"), CancellationToken.None);

        Assert.Single(effects.OfType<ProjectApprovalRequested>());
        Assert.Empty(policy.List());
    }

    [Fact]
    public async Task Without_a_folder_there_is_nothing_to_ask_and_the_event_gets_through()
    {
        var (hub, effects, store) = Hub(new ProjectPolicy());

        await hub.HandleAsync(Frame("finished", project: null), CancellationToken.None);

        Assert.Empty(effects.OfType<ProjectApprovalRequested>());
        Assert.Single(store.Snapshot());
    }

    [Fact]
    public void The_overlay_shows_the_question_until_it_is_answered()
    {
        var state = new OverlayState();
        var project = ProjectRef.From(@"C:\work\my-app")!;

        state.Apply(new StoreEffect[] { new ProjectApprovalRequested(project, "Test Agent") }, T0);
        var card = Assert.IsType<ProjectCard>(state.Snapshot().Current);
        Assert.False(state.Tick(T0.AddHours(1)));
        var text = CardPresenter.Present(card);

        state.Apply(new StoreEffect[] { new ProjectDecided(project, ProjectDecision.Allowed) }, T0.AddHours(1));

        Assert.Equal("New project: my-app", text.Title);
        Assert.Equal("Show Test Agent sessions from this folder on the island?", text.Question);
        Assert.Equal(@"C:\work\my-app", text.Path);
        Assert.Empty(state.Snapshot().Cards);
    }

    [Fact]
    public void Project_questions_rank_below_needs_input_and_above_milestones()
    {
        var state = new OverlayState();
        state.Apply(new StoreEffect[] { new ProjectApprovalRequested(ProjectRef.From(@"C:\p")!), Events.Input("hi").ToNotice() }, T0);

        Assert.Equal(new[] { CardKind.NeedsInput, CardKind.ProjectApproval }, state.Snapshot().Cards.Select(c => c.Kind).ToArray());
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not met in time.");
            await Task.Delay(25);
        }
    }
}

internal static class EventTestExtensions
{
    public static NoticeShown ToNotice(this InputRequested input) =>
        new(input.SessionId, "my-app", SessionPhase.NeedsInput, input.Text);
}
