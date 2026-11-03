namespace SelfClaw.Core.Models;

/// <summary>
/// A read either returns text, image content, or a model-correctable error.
/// </summary>
/// <remarks>
/// <see cref="Error"/> only carries expected, correctable failures (image delivery gated off, oversized
/// image, non-image binary); path escapes, missing files and IO faults still throw.
/// <see cref="ImageUnavailable"/> is the typed discriminator for the gated-off case, so callers can add the
/// capability reason without matching on the message text.
/// </remarks>
public sealed record WorkspaceFileReadResult(
    WorkspaceFileContent? Text = null,
    WorkspaceImageContent? Image = null,
    string? Error = null,
    bool ImageUnavailable = false);
