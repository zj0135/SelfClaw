namespace SelfClaw.Infrastructure.Channels.Feishu;

internal static class FeishuWsConstants
{
    public const string EndpointPath = "/callback/ws/endpoint";
    public const string DeviceId = "device_id";
    public const string ServiceId = "service_id";

    public const string HeaderTimestamp = "timestamp";
    public const string HeaderType = "type";
    public const string HeaderMessageId = "message_id";
    public const string HeaderSum = "sum";
    public const string HeaderSeq = "seq";
    public const string HeaderTraceId = "trace_id";
    public const string HeaderInstanceId = "instance_id";
    public const string HeaderBizRt = "biz_rt";
    public const string HandshakeStatus = "Handshake-Status";
    public const string HandshakeMessage = "Handshake-Msg";
    public const string HandshakeAuthErrorCode = "Handshake-Autherrcode";

    public const string MessageTypeEvent = "event";
    public const string MessageTypeCard = "card";
    public const string MessageTypePing = "ping";
    public const string MessageTypePong = "pong";

    public const int FrameTypeControl = 0;
    public const int FrameTypeData = 1;

    public const int ResponseOk = 0;
    public const int SystemBusy = 1;
    public const int Forbidden = 403;
    public const int AuthFailed = 514;
    public const int ExceedConnectionLimit = 1000040350;
    public const int InternalError = 1000040343;
}
