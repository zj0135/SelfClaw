namespace SelfClaw.Desktop.Pet;

internal sealed class PetBehavior
{
    private static readonly TimeSpan WaitingAfter = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan AmbientPlayMin = TimeSpan.FromMilliseconds(1400);
    private static readonly TimeSpan AmbientPlayVariance = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan AmbientRestMin = TimeSpan.FromMilliseconds(9000);
    private static readonly TimeSpan AmbientRestVariance = TimeSpan.FromMilliseconds(9000);
    private static readonly TimeSpan AmbientInitialDelayMin = TimeSpan.FromMilliseconds(4000);
    private static readonly PetTimerCommand TimerUnchanged = new(PetTimerOperation.Unchanged);
    private static readonly PetTimerCommand TimerStopped = new(PetTimerOperation.Stop);

    private readonly Random _random;
    private HashSet<string> _supportedRows;
    private PetInteraction _interaction = PetInteraction.Idle;
    private PetWorkState _workState;
    private bool _isAnimationRunning;
    private bool _isDragging;
    private bool _isHovering;
    private AmbientPhase _ambientPhase;
    private string? _ambientRowId;
    private string? _lastAmbientRowId;

    public PetBehavior(Random? random = null)
    {
        _random = random ?? Random.Shared;
        _supportedRows = PetLayout.CreateDefaultGrid()
            .RowsDef
            .Select(row => row.Id)
            .ToHashSet(StringComparer.Ordinal);
    }

    public PetBehaviorResult ConfigureRows(IEnumerable<string> supportedRows)
    {
        ArgumentNullException.ThrowIfNull(supportedRows);
        _supportedRows = supportedRows.ToHashSet(StringComparer.Ordinal);
        return CreateResult(TimerUnchanged, TimerUnchanged);
    }

    public PetBehaviorResult Apply(PetBehaviorEvent behaviorEvent)
    {
        ArgumentNullException.ThrowIfNull(behaviorEvent);

        var commands = behaviorEvent.Kind switch
        {
            PetBehaviorEventKind.AnimationStarted => StartAnimation(),
            PetBehaviorEventKind.AnimationStopped => StopAnimation(),
            PetBehaviorEventKind.PointerEntered => PointerEntered(),
            PetBehaviorEventKind.PointerExited => PointerExited(),
            PetBehaviorEventKind.PointerPressed => PointerPressed(),
            PetBehaviorEventKind.DragDirectionChanged => DragDirectionChanged(behaviorEvent.DragInteraction),
            PetBehaviorEventKind.PointerReleased => PointerReleased(behaviorEvent.IsHovering),
            PetBehaviorEventKind.WaitingElapsed => WaitingElapsed(),
            PetBehaviorEventKind.AmbientElapsed => AmbientElapsed(),
            PetBehaviorEventKind.WorkStateChanged => ChangeWorkState(behaviorEvent.WorkState),
            _ => (TimerUnchanged, TimerUnchanged),
        };

        return CreateResult(commands.Item1, commands.Item2);
    }

    private (PetTimerCommand Waiting, PetTimerCommand Ambient) StartAnimation()
    {
        _isAnimationRunning = true;
        if (_workState != PetWorkState.None)
        {
            return (TimerStopped, TimerStopped);
        }

        return (
            RestartTimer(WaitingAfter),
            _interaction == PetInteraction.Idle ? RestartAmbient(initial: true) : TimerStopped);
    }

    private (PetTimerCommand Waiting, PetTimerCommand Ambient) StopAnimation()
    {
        _isAnimationRunning = false;
        CancelAmbient();
        return (TimerStopped, TimerStopped);
    }

    private (PetTimerCommand Waiting, PetTimerCommand Ambient) PointerEntered()
    {
        var commands = RegisterUserInteraction();
        _isHovering = true;
        if (!_isDragging)
        {
            _interaction = PetInteraction.Hover;
        }

        return commands;
    }

    private (PetTimerCommand Waiting, PetTimerCommand Ambient) PointerExited()
    {
        var commands = RegisterUserInteraction();
        _isHovering = false;
        if (_isDragging)
        {
            return commands;
        }

        _interaction = PetInteraction.Idle;
        return WithAmbientSchedule(commands, initial: true);
    }

    private (PetTimerCommand Waiting, PetTimerCommand Ambient) PointerPressed()
    {
        var commands = RegisterUserInteraction();
        if (_interaction == PetInteraction.Waiting)
        {
            _interaction = _isHovering ? PetInteraction.Hover : PetInteraction.Idle;
        }

        return _interaction == PetInteraction.Idle
            ? WithAmbientSchedule(commands, initial: true)
            : commands;
    }

    private (PetTimerCommand Waiting, PetTimerCommand Ambient) DragDirectionChanged(
        PetInteraction dragInteraction)
    {
        var commands = RegisterUserInteraction();
        if (dragInteraction is not (PetInteraction.DragRight
            or PetInteraction.DragLeft
            or PetInteraction.DragUp
            or PetInteraction.DragDown))
        {
            return commands;
        }

        _isDragging = true;
        _interaction = dragInteraction;
        return commands;
    }

    private (PetTimerCommand Waiting, PetTimerCommand Ambient) PointerReleased(bool isHovering)
    {
        var commands = RegisterUserInteraction();
        _isDragging = false;
        _isHovering = isHovering;
        _interaction = isHovering ? PetInteraction.Hover : PetInteraction.Idle;
        return _interaction == PetInteraction.Idle
            ? WithAmbientSchedule(commands, initial: true)
            : commands;
    }

    private (PetTimerCommand Waiting, PetTimerCommand Ambient) WaitingElapsed()
    {
        if (_workState != PetWorkState.None || _interaction != PetInteraction.Idle)
        {
            return (TimerStopped, TimerUnchanged);
        }

        _interaction = PetInteraction.Waiting;
        CancelAmbient();
        return (TimerStopped, TimerStopped);
    }

    private (PetTimerCommand Waiting, PetTimerCommand Ambient) AmbientElapsed()
    {
        if (!_isAnimationRunning ||
            _interaction != PetInteraction.Idle ||
            _workState != PetWorkState.None)
        {
            CancelAmbient();
            return (TimerUnchanged, TimerStopped);
        }

        return _ambientPhase switch
        {
            AmbientPhase.WaitingToPlay => StartAmbientPlay(),
            AmbientPhase.Playing => EndAmbientPlay(),
            _ => (TimerUnchanged, TimerStopped),
        };
    }

    private (PetTimerCommand Waiting, PetTimerCommand Ambient) ChangeWorkState(PetWorkState workState)
    {
        if (_workState == workState)
        {
            return (TimerUnchanged, TimerUnchanged);
        }

        _workState = workState;
        if (workState != PetWorkState.None)
        {
            if (_interaction == PetInteraction.Waiting)
            {
                _interaction = PetInteraction.Idle;
                _isDragging = false;
                _isHovering = false;
            }

            CancelAmbient();
            return (TimerStopped, TimerStopped);
        }

        return (
            _isAnimationRunning ? RestartTimer(WaitingAfter) : TimerUnchanged,
            _isAnimationRunning && _interaction == PetInteraction.Idle
                ? RestartAmbient(initial: true)
                : TimerUnchanged);
    }

    private (PetTimerCommand Waiting, PetTimerCommand Ambient) RegisterUserInteraction()
    {
        CancelAmbient();
        return (
            _isAnimationRunning && _workState == PetWorkState.None
                ? RestartTimer(WaitingAfter)
                : TimerUnchanged,
            TimerStopped);
    }

    private (PetTimerCommand Waiting, PetTimerCommand Ambient) WithAmbientSchedule(
        (PetTimerCommand Waiting, PetTimerCommand Ambient) commands,
        bool initial)
    {
        if (!_isAnimationRunning || _workState != PetWorkState.None)
        {
            return commands;
        }

        return (commands.Waiting, RestartAmbient(initial));
    }

    private (PetTimerCommand Waiting, PetTimerCommand Ambient) StartAmbientPlay()
    {
        var rowId = PickAmbientRowId();
        if (rowId is null)
        {
            return (TimerUnchanged, RestartAmbient(initial: false));
        }

        _ambientRowId = rowId;
        _lastAmbientRowId = rowId;
        _ambientPhase = AmbientPhase.Playing;
        return (TimerUnchanged, RestartTimer(RandomDelay(AmbientPlayMin, AmbientPlayVariance)));
    }

    private (PetTimerCommand Waiting, PetTimerCommand Ambient) EndAmbientPlay()
    {
        _ambientRowId = null;
        return (TimerUnchanged, RestartAmbient(initial: false));
    }

    private PetTimerCommand RestartAmbient(bool initial)
    {
        _ambientPhase = AmbientPhase.WaitingToPlay;
        var delay = initial
            ? RandomDelay(AmbientInitialDelayMin, AmbientRestVariance)
            : RandomDelay(AmbientRestMin, AmbientRestVariance);
        return RestartTimer(delay);
    }

    private string? PickAmbientRowId()
    {
        var candidates = PetLayout.AmbientRowIds
            .Where(_supportedRows.Contains)
            .Where(rowId => !string.Equals(rowId, _lastAmbientRowId, StringComparison.Ordinal))
            .ToArray();

        if (candidates.Length == 0)
        {
            candidates = PetLayout.AmbientRowIds.Where(_supportedRows.Contains).ToArray();
        }

        return candidates.Length == 0 ? null : candidates[_random.Next(candidates.Length)];
    }

    private PetBehaviorResult CreateResult(
        PetTimerCommand waitingTimer,
        PetTimerCommand ambientTimer)
        => new(
            _interaction,
            _workState,
            ResolveSupportedRowId(ResolvePreferredRowId()),
            waitingTimer,
            ambientTimer);

    private string ResolvePreferredRowId()
    {
        if (_interaction is PetInteraction.Hover
            or PetInteraction.DragRight
            or PetInteraction.DragLeft
            or PetInteraction.DragUp
            or PetInteraction.DragDown)
        {
            return PetLayout.GetRowId(_interaction);
        }

        return _ambientRowId ?? _workState switch
        {
            PetWorkState.Working or PetWorkState.Reviewing => PetLayout.ReviewRowId,
            PetWorkState.Running => PetLayout.RunningRowId,
            PetWorkState.AwaitingApproval => PetLayout.WaitingRowId,
            PetWorkState.Succeeded => PetLayout.WavingRowId,
            PetWorkState.Failed => PetLayout.FailedRowId,
            PetWorkState.Cancelled => PetLayout.WaitingRowId,
            _ => PetLayout.GetRowId(_interaction),
        };
    }

    private string ResolveSupportedRowId(string preferredRowId)
    {
        if (_supportedRows.Contains(preferredRowId))
        {
            return preferredRowId;
        }

        foreach (var fallback in new[]
                 {
                     PetLayout.ReviewRowId,
                     PetLayout.WaitingRowId,
                     PetLayout.IdleRowId,
                 })
        {
            if (_supportedRows.Contains(fallback))
            {
                return fallback;
            }
        }

        return preferredRowId;
    }

    private void CancelAmbient()
    {
        _ambientPhase = AmbientPhase.None;
        _ambientRowId = null;
    }

    private TimeSpan RandomDelay(TimeSpan minimum, TimeSpan variance)
        => minimum + TimeSpan.FromMilliseconds(_random.NextDouble() * variance.TotalMilliseconds);

    private static PetTimerCommand RestartTimer(TimeSpan delay)
        => new(PetTimerOperation.Restart, delay);

    private enum AmbientPhase
    {
        None,
        WaitingToPlay,
        Playing,
    }
}
