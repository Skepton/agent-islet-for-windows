using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Island.Core;

namespace Island.App;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record ActionButton(string Label, PermissionActionKind Kind, ICommand Command);

public abstract class CardViewModel(OverlayCard source) : ObservableObject
{
    public string Id { get; } = source.Id;

    /// <summary>The card this was built from. A card with the same id but a different instance (e.g. a notice that changed phase) needs a new view-model.</summary>
    public OverlayCard Source { get; } = source;

    /// <summary>Refreshes anything that depends on the clock (ages).</summary>
    public virtual void Update(DateTimeOffset now) { }
}

/// <summary>A permission request: header, command box and the No / Yes / remember buttons.</summary>
public sealed class PermissionCardViewModel : CardViewModel
{
    /// <summary>The island has room for one "Always Allow"; the panel lists every option.</summary>
    public const int IslandRememberButtons = 1;

    private readonly PermissionCard _card;
    private IReadOnlyList<string> _chips = Array.Empty<string>();

    public PermissionCardViewModel(
        PermissionCard card,
        DateTimeOffset now,
        Action<PermissionCard, PermissionAnswer> answer) : base(card)
    {
        _card = card;
        var text = CardPresenter.Present(card, now, IslandRememberButtons);
        Title = text.Title;
        YouLine = text.YouLine;
        AttentionLine = text.AttentionLine;
        ToolLabel = text.ToolLabel;
        CommandPrefix = text.CommandPrefix;
        Command = text.Command;
        Description = text.Description;
        _chips = text.Chips;
        Buttons = text.Actions
            .Select(a => new ActionButton(a.Label, a.Kind, new RelayCommand(() => answer(card, CardPresenter.Answer(card, a)))))
            .ToList();
        AnswerInTerminal = new RelayCommand(() => answer(card, CardPresenter.AnswerInTerminal(card)));
    }

    public string Title { get; }
    public string? YouLine { get; }
    public string AttentionLine { get; }
    public string ToolLabel { get; }
    public string CommandPrefix { get; }
    public string Command { get; }
    public string? Description { get; }
    public IReadOnlyList<ActionButton> Buttons { get; }
    public ICommand AnswerInTerminal { get; }

    public bool HasYouLine => YouLine is not null;
    public bool HasDescription => Description is not null;
    public bool HasPrefix => CommandPrefix.Length > 0;

    public IReadOnlyList<string> Chips
    {
        get => _chips;
        private set => Set(ref _chips, value);
    }

    public override void Update(DateTimeOffset now) => Chips = CardPresenter.Present(_card, now, IslandRememberButtons).Chips;
}

/// <summary>"Needs input" and "Finished" cards.</summary>
public sealed class NoticeCardViewModel : CardViewModel
{
    private readonly NoticeCard _card;
    private IReadOnlyList<string> _chips;

    public NoticeCardViewModel(NoticeCard card, DateTimeOffset now, Action<string> dismiss) : base(card)
    {
        _card = card;
        var text = CardPresenter.Present(card, now);
        Title = text.Title;
        YouLine = text.YouLine;
        Body = text.Body;
        IsDone = text.IsDone;
        _chips = text.Chips;
        Dismiss = new RelayCommand(() => dismiss(card.Id));
    }

    public string Title { get; }
    public string? YouLine { get; }
    public string Body { get; }
    public bool IsDone { get; }
    public ICommand Dismiss { get; }
    public bool HasYouLine => YouLine is not null;

    public IReadOnlyList<string> Chips
    {
        get => _chips;
        private set => Set(ref _chips, value);
    }

    public override void Update(DateTimeOffset now) => Chips = CardPresenter.Present(_card, now).Chips;
}

/// <summary>"New project: x. Show its sessions on the island?" with Ignore / Show.</summary>
public sealed class ProjectCardViewModel : CardViewModel
{
    public ProjectCardViewModel(ProjectCard card, Action<ProjectCard, ProjectDecision> decide) : base(card)
    {
        var text = CardPresenter.Present(card);
        Title = text.Title;
        Question = text.Question;
        Path = text.Path;
        Chips = text.Chips;
        Show = new RelayCommand(() => decide(card, ProjectDecision.Allowed));
        Ignore = new RelayCommand(() => decide(card, ProjectDecision.Ignored));
    }

    public string Title { get; }
    public string Question { get; }
    public string Path { get; }
    public IReadOnlyList<string> Chips { get; }
    public ICommand Show { get; }
    public ICommand Ignore { get; }
}

public sealed class UsageCardViewModel : CardViewModel
{
    public UsageCardViewModel(UsageCard card, Action<string> dismiss) : base(card)
    {
        var text = CardPresenter.Present(card);
        Title = text.Title;
        Percent = text.Percent;
        Level = text.Level;
        ResetsText = text.ResetsText;
        Dismiss = new RelayCommand(() => dismiss(card.Id));
    }

    public string Title { get; }
    public double Percent { get; }
    public int Level { get; }
    public string ResetsText { get; }
    public ICommand Dismiss { get; }
}

/// <summary>Everything the island window binds to. All logic lives in Core; this only holds the current values.</summary>
public sealed class OverlayViewModel : ObservableObject
{
    private CardViewModel? _current;
    private string _pagerText = "";
    private bool _hasPager;
    private string _footerText = "";
    private bool _hasFooter;
    private bool _hasStrip;
    private UsageSnapshot? _usage;

    private bool _isCollapsed;
    private string _pillText = "";

    public OverlayViewModel(Action previous, Action next, Action showAll, Action expand)
    {
        Previous = new RelayCommand(previous);
        Next = new RelayCommand(next);
        ShowAll = new RelayCommand(showAll);
        Expand = new RelayCommand(expand);
    }

    public ICommand Previous { get; }
    public ICommand Next { get; }
    public ICommand ShowAll { get; }
    public ICommand Expand { get; }

    /// <summary>True while the island is shrunk to the small pill (something still waits, but the user has not looked for a while).</summary>
    public bool IsCollapsed
    {
        get => _isCollapsed;
        set
        {
            if (!Set(ref _isCollapsed, value)) return;
            Raise(nameof(IsExpanded));
        }
    }

    public bool IsExpanded => !IsCollapsed;

    /// <summary>The pill's count of cards that still need the user.</summary>
    public string PillText
    {
        get => _pillText;
        private set => Set(ref _pillText, value);
    }

    public ObservableCollection<UsageStripItem> Strip { get; } = new();

    public bool HasStrip
    {
        get => _hasStrip;
        private set => Set(ref _hasStrip, value);
    }

    public CardViewModel? Current
    {
        get => _current;
        private set => Set(ref _current, value);
    }

    /// <summary>"1 of 3"; only meaningful when <see cref="HasPager"/>.</summary>
    public string PagerText
    {
        get => _pagerText;
        private set => Set(ref _pagerText, value);
    }

    public bool HasPager
    {
        get => _hasPager;
        private set => Set(ref _hasPager, value);
    }

    public string FooterText
    {
        get => _footerText;
        private set => Set(ref _footerText, value);
    }

    public bool HasFooter
    {
        get => _hasFooter;
        private set => Set(ref _hasFooter, value);
    }

    public bool HasContent => Current is not null || HasStrip;

    /// <summary>True when only the usage strip is visible, so the window can let clicks pass through.</summary>
    public bool IsStripOnly => Current is null && HasStrip;

    public void SetUsage(UsageSnapshot? snapshot, DateTimeOffset now)
    {
        _usage = snapshot;
        RefreshStrip(now);
    }

    /// <summary>
    /// Shows <paramref name="card"/> (null for none). A card with the same id is kept so its controls don't flicker;
    /// <paramref name="create"/> is only called for a new one.
    /// </summary>
    public void Show(OverlaySnapshot snapshot, int sessionCount, DateTimeOffset now, Func<OverlayCard, CardViewModel> create)
    {
        if (snapshot.Current is null)
        {
            Current = null;
        }
        else if (!ReferenceEquals(Current?.Source, snapshot.Current))
        {
            Current = create(snapshot.Current);
        }
        else
        {
            Current.Update(now);
        }

        HasPager = snapshot.Cards.Count > 1;
        PagerText = HasPager ? $"{snapshot.Index + 1} of {snapshot.Cards.Count}" : "";
        PillText = snapshot.Cards.Count(c => IslandCollapse.IsWaiting(c.Kind)).ToString();
        HasFooter = sessionCount > 1;
        FooterText = HasFooter ? $"Show all {sessionCount} sessions" : "";
        RefreshStrip(now);

        Raise(nameof(HasContent));
        Raise(nameof(IsStripOnly));
    }

    public void RefreshStrip(DateTimeOffset now)
    {
        var items = _usage is null ? Array.Empty<UsageStripItem>() : CardPresenter.PresentStrip(_usage, now);
        if (!items.SequenceEqual(Strip))
        {
            Strip.Clear();
            foreach (var item in items) Strip.Add(item);
        }
        HasStrip = items.Count > 0;
        Raise(nameof(HasContent));
        Raise(nameof(IsStripOnly));
    }

}
