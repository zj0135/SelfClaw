using Microsoft.Extensions.AI;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Infrastructure.AiProviders.Abstractions;

/// <summary>
/// Provider-specific factory that turns a resolved <see cref="AiProviderClientRequest"/>
/// into a runnable <see cref="IChatClient"/> plus its <see cref="ChatOptions"/>.
/// One adapter owns a single <see cref="AiProviderKind"/> but may support more
/// than one <see cref="AiProviderApiFormat"/>.
/// </summary>
/// <remarks>
/// Adapters encapsulate SDK choice, credential/endpoint wiring, sampling
/// mapping and provider-specific raw option injection, keeping that detail out
/// of the shared execution layer.
/// </remarks>
public interface IAiProviderAdapter
{
    /// <summary>The provider kind this adapter handles.</summary>
    AiProviderKind ProviderKind { get; }

    /// <summary>Returns whether this adapter can serve the given API format.</summary>
    bool SupportsApiFormat(AiProviderApiFormat apiFormat);

    /// <summary>
    /// Creates a Microsoft.Extensions.AI <see cref="IChatClient"/> for the request.
    /// Throws <see cref="NotSupportedException"/> when the profile's API format is
    /// not supported by this adapter.
    /// </summary>
    IChatClient CreateChatClient(AiProviderClientRequest request);

    /// <summary>
    /// Builds the <see cref="ChatOptions"/> for the request, mapping shared
    /// sampling/tool settings and injecting provider-specific raw options.
    /// </summary>
    ChatOptions CreateChatOptions(AiProviderClientRequest request);
}
