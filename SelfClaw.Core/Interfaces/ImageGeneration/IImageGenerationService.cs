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

    /// <summary>
    /// Pure lookup, no I/O: the workbench builds its size/quality/style controls from this before it
    /// ever sends a request.
    /// </summary>
    ImageGenerationModelCapabilities GetModelCapabilities(string model);

    /// <summary>
    /// Resolves the exact profile a job will use and returns its capabilities. The host must validate
    /// input against the same model that will execute the request, including the case where no id was
    /// supplied and the service picks a default profile.
    /// </summary>
    Task<ImageGenerationModelCapabilities> GetCapabilitiesForProfileAsync(
        Guid? modelProfileId,
        CancellationToken cancellationToken = default);
}
