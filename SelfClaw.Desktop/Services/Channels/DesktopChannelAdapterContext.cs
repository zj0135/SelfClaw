using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;

namespace SelfClaw.Desktop.Services;

public sealed record DesktopChannelAdapterContext(
    ISecretProtector SecretProtector,
    ILoggerFactory LoggerFactory);
