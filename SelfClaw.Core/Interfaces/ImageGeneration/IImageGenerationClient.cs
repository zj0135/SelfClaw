using System.Threading;
using System.Threading.Tasks;
using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

public interface IImageGenerationClient
{
    Task<ImageGenerationResult> GenerateImagesAsync(
        ImageGenerationRequest request,
        CancellationToken cancellationToken = default);

    Task<ImageGenerationResult> GenerateImageEditsAsync(
        ImageEditRequest request,
        CancellationToken cancellationToken = default);
}
