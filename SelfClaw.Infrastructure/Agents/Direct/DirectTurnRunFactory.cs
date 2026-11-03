using SelfClaw.Infrastructure.Agents.Direct.Abstractions;
using SelfClaw.Infrastructure.Agents.Direct.Context;
using SelfClaw.Infrastructure.Agents.Direct.Models;

namespace SelfClaw.Infrastructure.Agents.Direct;

/// <summary>
/// Creates the per-turn <see cref="DirectTurnRun"/>. The run must never be a DI singleton: its context, allowance
/// and progress guard are per turn. The factory is, because its dependencies are stateless.
/// </summary>
internal sealed class DirectTurnRunFactory
{
    private readonly DirectPromptMeter _meter;
    private readonly DirectCompactionStep _compactionStep;
    private readonly IAttachmentContentReader _attachmentContentReader;

    internal DirectTurnRunFactory(
        DirectPromptMeter meter,
        DirectCompactionStep compactionStep,
        IAttachmentContentReader attachmentContentReader)
    {
        ArgumentNullException.ThrowIfNull(meter);
        ArgumentNullException.ThrowIfNull(compactionStep);
        ArgumentNullException.ThrowIfNull(attachmentContentReader);
        _meter = meter;
        _compactionStep = compactionStep;
        _attachmentContentReader = attachmentContentReader;
    }

    internal DirectTurnRun Create(DirectTurnSetup setup, DirectEventTranslator output)
        => new(setup, output, _meter, _compactionStep, _attachmentContentReader);
}
