namespace SelfClaw.Core.Models;

/// <summary>
/// The user's choice of network exit for the requests SelfClaw makes on its own behalf. Read per HTTP
/// handler creation and per child process launch, so a change applies to the next request without a
/// restart. <see cref="Host"/> and <see cref="Port"/> only carry meaning for
/// <see cref="NetworkProxyMode.Custom"/>; a custom endpoint that is not filled in yet is treated as
/// <see cref="NetworkProxyMode.System"/> at use time rather than failing a request, because the settings
/// page saves while the user is still typing.
/// </summary>
public sealed record NetworkProxySettings(
    NetworkProxyMode Mode = NetworkProxyMode.System,
    string Host = "",
    int Port = 0);
