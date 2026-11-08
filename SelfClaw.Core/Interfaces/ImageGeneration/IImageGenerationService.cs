using System;
using System.Threading;
using System.Threading.Tasks;
using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

public interface IImageGenerationService
{
    Task<ImageGenerationResult> GenerateImagesAsync(
        Guid? modelProfileId,
        ImageGenerationRequest request,
        CancellationToken cancellationToken = default);

    Task<ImageGenerationResult> GenerateImageEditsAsync(
        Guid? modelProfileId,
        ImageEditRequest request,
        CancellationToken cancellationToken = default);
}
