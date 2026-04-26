namespace SelfClaw.Infrastructure.Channels.Feishu;

internal sealed class FeishuLongConnectionException(string message, bool isClientFault, int? code = null, Exception? inner = null)
    : Exception(message, inner)
{
    public bool IsClientFault { get; } = isClientFault;
    public int? Code { get; } = code;
}
