namespace SelfClaw.Desktop.Pet;

internal static class PetAnimationResolver
{
    public static string ResolveRowId(PetInteraction interaction, PetWorkState workState)
    {
        if (interaction is PetInteraction.Hover
            or PetInteraction.DragRight
            or PetInteraction.DragLeft
            or PetInteraction.DragUp
            or PetInteraction.DragDown)
        {
            return PetLayout.GetRowId(interaction);
        }

        return workState switch
        {
            PetWorkState.Working or PetWorkState.Reviewing => PetLayout.ReviewRowId,
            PetWorkState.Running => PetLayout.RunningRowId,
            PetWorkState.AwaitingApproval => PetLayout.WaitingRowId,
            PetWorkState.Succeeded => PetLayout.WavingRowId,
            PetWorkState.Failed => PetLayout.FailedRowId,
            PetWorkState.Cancelled => PetLayout.WaitingRowId,
            _ => PetLayout.GetRowId(interaction),
        };
    }
}
