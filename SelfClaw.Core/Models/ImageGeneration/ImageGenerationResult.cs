using System;
using System.Collections.Generic;

namespace SelfClaw.Core.Models;

public sealed record ImageGenerationResult(
    IReadOnlyList<GeneratedImageItem> Images,
    TimeSpan Duration,
    Guid? ModelProfileId = null);
