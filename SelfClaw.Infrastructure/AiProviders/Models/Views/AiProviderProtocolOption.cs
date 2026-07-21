using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Infrastructure.AiProviders.Models.Views;

/// <summary>
/// A user-facing protocol choice for a custom provider connection. Each option
/// maps a wire protocol the user selects to the concrete
/// <see cref="AiProviderKind"/> and default <see cref="AiProviderApiFormat"/>
/// used to build the connection. The list is curated from the registered
/// adapters so the UI never has to reason about provider kinds directly.
/// </summary>
public sealed record AiProviderProtocolOption(
    string Id,
    string Label,
    AiProviderKind ProviderKind,
    AiProviderApiFormat DefaultApiFormat,
    AiProviderAuthKind AuthKind,
    bool SupportsModelListing);
