namespace SelfClaw.Desktop.Pet;

/// <summary>
/// Interaction state transitions for the desktop pet. Timing is owned by the ViewModel.
/// </summary>
public sealed class PetStateMachine
{
    private bool _isDragging;
    private bool _isHovering;

    public PetInteraction Current { get; private set; } = PetInteraction.Idle;

    public event Action<PetInteraction>? InteractionChanged;

    public void Reset()
    {
        _isDragging = false;
        _isHovering = false;
        SetInteraction(PetInteraction.Idle);
    }

    public void PointerEntered()
    {
        _isHovering = true;
        if (!_isDragging)
        {
            SetInteraction(PetInteraction.Hover);
        }
    }

    public void PointerExited()
    {
        _isHovering = false;
        if (!_isDragging)
        {
            SetInteraction(PetInteraction.Idle);
        }
    }

    public void PointerPressed()
    {
        if (Current == PetInteraction.Waiting)
        {
            SetInteraction(_isHovering ? PetInteraction.Hover : PetInteraction.Idle);
        }
    }

    public void DragDirectionChanged(PetInteraction dragInteraction)
    {
        if (dragInteraction is not (PetInteraction.DragRight
            or PetInteraction.DragLeft
            or PetInteraction.DragUp
            or PetInteraction.DragDown))
        {
            return;
        }

        _isDragging = true;
        SetInteraction(dragInteraction);
    }

    public void PointerReleased(bool isHovering)
    {
        _isDragging = false;
        _isHovering = isHovering;
        SetInteraction(_isHovering ? PetInteraction.Hover : PetInteraction.Idle);
    }

    public void WaitingElapsed()
    {
        if (Current == PetInteraction.Idle)
        {
            SetInteraction(PetInteraction.Waiting);
        }
    }

    private void SetInteraction(PetInteraction next)
    {
        if (Current == next)
        {
            return;
        }

        Current = next;
        InteractionChanged?.Invoke(Current);
    }
}
