namespace Island.Core;

/// <summary>
/// Glue between incoming hook frames, the agent integrations, the session store and the presentation layer.
/// Holds permission requests until the UI answers, the forwarder disconnects, or the request is superseded.
/// With a <see cref="ProjectPolicy"/>, only projects the user chose to show get through; a new project is asked about
/// once and its events are dropped until then (the agent behaves as if the island were not there).
/// </summary>
public sealed class IslandHub
{
    private readonly SessionStore _store;
    private readonly PermissionBroker _broker;
    private readonly Dictionary<string, IAgentIntegration> _agents;
    private readonly ProjectPolicy? _projects;
    private readonly Action<IReadOnlyList<StoreEffect>> _present;
    private readonly Func<DateTimeOffset> _clock;
    private readonly HashSet<string> _asked = new(StringComparer.Ordinal);

    /// <param name="projects">Null lets every project through (tests, <c>--allow-all-projects</c>).</param>
    public IslandHub(
        SessionStore store,
        PermissionBroker broker,
        IEnumerable<IAgentIntegration> agents,
        Action<IReadOnlyList<StoreEffect>> present,
        Func<DateTimeOffset>? clock = null,
        ProjectPolicy? projects = null)
    {
        _store = store;
        _broker = broker;
        _agents = agents.ToDictionary(a => a.Id, StringComparer.Ordinal);
        _present = present;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _projects = projects;
    }

    public IReadOnlyList<SessionInfo> Sessions => _store.Snapshot();

    /// <summary>The user's project choices; empty when every project is let through.</summary>
    public IReadOnlyList<ProjectEntry> Projects => _projects?.List() ?? Array.Empty<ProjectEntry>();

    /// <returns>What the forwarder should print for a held permission request; null for everything else.</returns>
    public async Task<string?> HandleAsync(HookFrame frame, CancellationToken ct)
    {
        // A frame from an agent this build does not know: stay silent, the agent carries on as normal.
        if (!_agents.TryGetValue(frame.Agent, out var agent)) return null;
        if (agent.Translate(frame.PayloadJson, frame.Id) is not { } evt) return null;

        var project = ProjectRef.From(frame.Project ?? evt.Cwd);
        var source = evt.Source ?? evt.GetType().Name;
        if (!Admit(project, agent))
        {
            Dispatch(new StoreEffect[] { new EventReceived(evt.SessionId, source, Shown: false) });
            return null;
        }

        evt = evt with { ClientApp = frame.ClientApp ?? evt.ClientApp, Agent = agent.DisplayName, Project = project?.Key, ProjectName = project?.Name };
        var holdForDecision = frame.Wait && evt is PermissionRequested;

        // Register the waiter before anything is presented, so an answer given instantly cannot be lost.
        var output = holdForDecision ? _broker.HoldAsync(frame.Id, agent, ct) : null;
        var effects = new List<StoreEffect> { new EventReceived(evt.SessionId, source, Shown: true) };
        effects.AddRange(_store.Apply(evt, _clock()));
        Dispatch(effects);
        if (output is null) return null;

        var answer = await output.ConfigureAwait(false);
        if (!ct.IsCancellationRequested) return answer;

        // The forwarder went away (terminal answered, or its deadline passed): drop the card.
        Dispatch(_store.ResolvePermission(frame.Id, _clock(), PermissionOutcome.Abandoned));
        return null;
    }

    /// <summary>
    /// A hook that arrived over HTTP (<see cref="HookHttpServer"/>). The agent turns it into a frame; everything after
    /// that is the same as for the pipe. Returns the response body for a request that waited for an answer, else null.
    /// </summary>
    public async Task<string?> HandleHttpAsync(string agentId, HookHttpRequest request, CancellationToken ct)
    {
        if (!_agents.TryGetValue(agentId, out var agent)) return null;
        var frame = agent.FrameFromHttp(Guid.NewGuid().ToString("N"), request, _clock());
        return frame is null ? null : await HandleAsync(frame, ct).ConfigureAwait(false);
    }

    /// <summary>Called by the UI when the user acts on a permission card.</summary>
    /// <param name="verdict">The answer, or null for "answer in terminal" (the hook prints nothing).</param>
    public void Answer(string requestId, PermissionVerdict? verdict, PermissionOutcome outcome)
    {
        _broker.Resolve(requestId, verdict);
        Dispatch(_store.ResolvePermission(requestId, _clock(), outcome));
    }

    /// <summary>
    /// Records the user's choice for a project. Ignoring or forgetting one drops its sessions and cards at once;
    /// a forgotten project is asked about again on its next event.
    /// </summary>
    public void DecideProject(ProjectRef project, ProjectDecision decision)
    {
        if (_projects is null) return;
        var now = _clock();
        _projects.Set(project, decision, now);
        lock (_asked)
        {
            if (decision == ProjectDecision.Unknown) _asked.Remove(project.Key);
            else _asked.Add(project.Key);
        }

        var effects = new List<StoreEffect> { new ProjectDecided(project, decision) };
        if (decision != ProjectDecision.Allowed) effects.AddRange(_store.RemoveProject(project.Key, now));
        Dispatch(effects);
    }

    /// <summary>Drops sessions that have been silent for hours. Call occasionally.</summary>
    public void Prune() => Dispatch(_store.Prune(_clock()));

    /// <summary>True when events from <paramref name="project"/> may reach the island. Asks about a new project once.</summary>
    private bool Admit(ProjectRef? project, IAgentIntegration agent)
    {
        // No folder to ask about (rare): nothing to decide, let it through.
        if (_projects is null || project is null) return true;

        switch (_projects.Check(project))
        {
            case ProjectDecision.Allowed:
                return true;
            case ProjectDecision.Ignored:
                return false;
        }

        bool first;
        lock (_asked) first = _asked.Add(project.Key);
        if (first) Dispatch(new StoreEffect[] { new ProjectApprovalRequested(project, agent.DisplayName) });
        return false;
    }

    private void Dispatch(IReadOnlyList<StoreEffect> effects)
    {
        if (effects.Count == 0) return;

        foreach (var effect in effects)
        {
            // Any card that closes without an answer releases its held hook with "no decision".
            if (effect is PermissionCleared { Outcome: not PermissionOutcome.Answered } cleared)
                _broker.Resolve(cleared.RequestId, null);
        }
        _present(effects);
    }
}
