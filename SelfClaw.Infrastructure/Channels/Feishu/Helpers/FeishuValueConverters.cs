namespace SelfClaw.Infrastructure.Channels.Feishu;

public static class FeishuValueConverters
{
    public static string ToApiValue(this FeishuFileType fileType) => fileType switch
    {
        FeishuFileType.Opus => "opus",
        FeishuFileType.Mp4 => "mp4",
        FeishuFileType.Pdf => "pdf",
        FeishuFileType.Doc => "doc",
        FeishuFileType.Xls => "xls",
        FeishuFileType.Ppt => "ppt",
        _ => "stream"
    };

    public static FeishuFileType DetectFileType(string fileName, FeishuFileType fallback = FeishuFileType.Stream)
    {
        var extension = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return extension switch
        {
            "opus" => FeishuFileType.Opus,
            "mp4" => FeishuFileType.Mp4,
            "pdf" => FeishuFileType.Pdf,
            "doc" or "docx" => FeishuFileType.Doc,
            "xls" or "xlsx" => FeishuFileType.Xls,
            "ppt" or "pptx" => FeishuFileType.Ppt,
            _ => fallback
        };
    }

    public static string ToApiValue(this FeishuUrgentType urgentType) => urgentType switch
    {
        FeishuUrgentType.Sms => "sms",
        _ => "app"
    };

    public static string ToApiValue(this FeishuMessageResourceType resourceType) => resourceType switch
    {
        FeishuMessageResourceType.File => "file",
        _ => "image"
    };
}
