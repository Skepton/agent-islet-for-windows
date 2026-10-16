using System.Windows.Threading;
using Island.Core;

namespace Island.App;

/// <summary>
/// Owns the overlay: the card queue, its view-model and the island window. Feed it hub effects and usage updates;
/// it shows, hides and answers. Everything runs on the UI thread.
/// </summary>
public sealed class OverlayController : IDisposable
{
    /// <summary>Request ids (and project keys) with this prefix belong to <c>--demo</c> and are answered locally.</summary>
    public const string DemoPrefix = "demo-";

    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan FullScreenCheckInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RelativeTimeRefresh = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HoverInterval = TimeSpan.FromMilliseconds(100);

    private readonly IslandHub _hub;
    private readonly Action<string> _log;
    private readonly OverlayState _state;
    private readonly OverlayViewModel _viewModel;
    private readonly IslandWindow _window;
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _fullScreenCheck;
    private readonly DispatcherTimer _hover;
    private readonly UsageReveal _reveal = new();
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;
    private DateTimeOffset _openSince = DateTimeOffset.UtcNow;
    private string? _placedFor;
    private bool _suppressed;
    private bool _pointerOver;

    public OverlayController(IslandHub hub, Action<string> log)
    {
        _hub = hub;
        _log = log;
        _state = new OverlayState();
        _viewModel = new OverlayViewModel(
            previous: () => { _state.Previous(); Refresh(); },
            next: () => { _state.Next(); Refresh(); },
            expand: () => { Expand(); Refresh(); });
        _window = new IslandWindow(_viewModel);

        // Hovering opens the pill; leaving lets the island shrink back a little sooner than a fresh card would.
        _window.MouseEnter += (_, _) =>
        {
            _pointerOver = true;
            if (_viewModel.IsCollapsed) { Expand(); Refresh(); }
        };
        _window.MouseLeave += (_, _) =>
        {
            _pointerOver = false;
            _openSince = DateTimeOffset.UtcNow - (IslandCollapse.After - IslandCollapse.LeaveGrace);
        };

        _tick = new DispatcherTimer { Interval = TickInterval };
        _tick.Tick += (_, _) => OnTick();
        _fullScreenCheck = new DispatcherTimer { Interval = FullScreenCheckInterval };
        _fullScreenCheck.Tick += (_, _) => CheckFullScreen();

        // The usage bar lets clicks through (it must never block the window underneath), so it cannot see the mouse:
        // look at where the pointer is instead.
        _hover = new DispatcherTimer { Interval = HoverInterval };
        _hover.Tick += (_, _) => OnHoverTick();
    }

    public void Start()
    {
        _tick.Start();
        _fullScreenCheck.Start();
        _hover.Start();
        Refresh();
    }

    /// <summary>How the usage numbers show when no card needs the user.</summary>
    public void SetUsageBar(UsageBarMode mode)
    {
        _viewModel.UsageBar = mode;
        Refresh();
    }

    /// <summary>Forgets the usage reading (usage tracking was turned off): the bar and the numbers go away at once.</summary>
    public void ClearUsage()
    {
        var now = DateTimeOffset.UtcNow;
        _reveal.Observe(null, now);
        _viewModel.SetUsage(null, now);
        _viewModel.UsageRevealed = false;
        Refresh();
    }

    public void OnEffects(IReadOnlyList<StoreEffect> effects)
    {
        _state.Apply(effects, DateTimeOffset.UtcNow);
        Refresh();
    }

    public void OnUsage(UsageUpdate update)
    {
        var now = DateTimeOffset.UtcNow;
        _state.ApplyUsage(update, now);
        _reveal.Observe(update.Latest, now);
        _viewModel.UsageRevealed = _reveal.IsRevealed(now);
        _viewModel.SetUsage(update.Latest, now);
        Refresh();
    }

    /// <summary>Removes every card and the usage strip (used by the demo between scenes).</summary>
    public void Reset()
    {
        foreach (var card in _state.Snapshot().Cards) _state.Dismiss(card.Id);
        Refresh();
    }

    public void Dispose()
    {
        _tick.Stop();
        _fullScreenCheck.Stop();
        _hover.Stop();
        _window.Close();
    }

    private void Refresh()
    {
        var now = DateTimeOffset.UtcNow;
        _lastRefresh = now;

        var snapshot = _state.Snapshot();
        _viewModel.Show(snapshot, now, card => Create(card, now));

        // While something full-screen is running only cards that need the user get through, not the usage strip.
        var visible = _viewModel.HasContent && !(_suppressed && snapshot.Current is null);
        _window.SetClickThrough(_viewModel.IsStripOnly);

        if (!visible)
        {
            _window.SlideOut();
            _placedFor = null;
            return;
        }

        // Re-place when the front card changes, so the island follows the window the user is working in.
        // A new front card always opens fully, even when the island had shrunk to its pill.
        var key = snapshot.Current?.Id ?? "";
        if (_placedFor != key)
        {
            _placedFor = key;
            Expand();
            _window.PlaceAtTop();
        }
        _window.SlideIn();
    }

    private void Expand()
    {
        _viewModel.IsCollapsed = false;
        _openSince = DateTimeOffset.UtcNow;
    }

    private CardViewModel Create(OverlayCard card, DateTimeOffset now) => card switch
    {
        PermissionCard permission => new PermissionCardViewModel(permission, now, Answer),
        NoticeCard notice => new NoticeCardViewModel(notice, now, Dismiss),
        ProjectCard project => new ProjectCardViewModel(project, Decide),
        UsageCard usage => new UsageCardViewModel(usage, Dismiss),
        _ => throw new ArgumentOutOfRangeException(nameof(card)),
    };

    private void Dismiss(string id)
    {
        _state.Dismiss(id);
        Refresh();
    }

    private void Answer(PermissionCard card, PermissionAnswer answer)
    {
        // Record what is being sent, so a click is never silent.
        _log($"answered           {card.SessionName}  {answer.Log}");

        if (card.Id.StartsWith(DemoPrefix, StringComparison.Ordinal))
        {
            _state.Apply(new StoreEffect[] { new PermissionCleared(card.SessionId, card.Id, answer.Outcome) }, DateTimeOffset.UtcNow);
            Refresh();
            return;
        }

        // The hub reports the cleared card back through OnEffects.
        _hub.Answer(card.Id, answer.Verdict, answer.Outcome);
    }

    private void Decide(ProjectCard card, ProjectDecision decision)
    {
        _log($"project {decision.ToString().ToLowerInvariant(),-10} {card.Project.Path}");

        if (card.Project.Key.StartsWith(DemoPrefix, StringComparison.Ordinal))
        {
            _state.Apply(new StoreEffect[] { new ProjectDecided(card.Project, decision) }, DateTimeOffset.UtcNow);
            Refresh();
            return;
        }

        // The hub reports the decision back through OnEffects, which removes the card.
        _hub.DecideProject(card.Project, decision);
    }

    private void OnTick()
    {
        var now = DateTimeOffset.UtcNow;
        if (_state.Tick(now) || now - _lastRefresh >= RelativeTimeRefresh) Refresh();

        var snapshot = _state.Snapshot();
        if (_viewModel.IsCollapsed && (snapshot.Current is null || !IslandCollapse.IsWaiting(snapshot.Current.Kind)))
        {
            Expand(); // nothing waits any more (e.g. only a finished card is left): show it normally
            Refresh();
        }
        else if (!_viewModel.IsCollapsed && IslandCollapse.ShouldCollapse(snapshot, _openSince, now, _pointerOver))
        {
            // Not a clearing: the card still waits behind the pill. Logged so "it vanished" can be told apart from a real clear.
            _log($"island shrank to the pill: {snapshot.Current?.Kind} still waiting");
            _viewModel.IsCollapsed = true;
            Refresh();
        }
    }

    private void OnHoverTick()
    {
        var now = DateTimeOffset.UtcNow;

        // Only the usage bar listens for the pointer; a card has real buttons and gets real mouse events.
        if (_viewModel.IsStripOnly
            && _window.PixelBounds() is { } island
            && NativeMethods.GetCursorPos(out var pointer)
            && UsageReveal.PointerOnBar(island.Bounds, pointer.X, pointer.Y, island.Scale, _viewModel.UsageRevealed))
        {
            _reveal.Hover(now);
        }

        var revealed = _reveal.IsRevealed(now);
        if (revealed == _viewModel.UsageRevealed) return;
        _viewModel.UsageRevealed = revealed;
        Refresh();
    }

    private void CheckFullScreen()
    {
        // The bar follows the window the user is working in, even when no card ever opens.
        if (_viewModel.IsHairline) _window.PlaceAtTop();

        var suppressed = NativeMethods.IsFullScreenAppRunning();
        if (suppressed == _suppressed) return;

        _suppressed = suppressed;
        _state.SuppressNonCritical = suppressed;
        Refresh();
    }
}
