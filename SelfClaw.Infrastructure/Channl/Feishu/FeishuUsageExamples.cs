namespace SelfClaw.Infrastructure.Channl.Feishu;

/// <summary>
/// Minimal usage snippets for the C# Feishu rewrite.
/// These methods are examples only and are not invoked by the agent runtime.
/// </summary>
public static class FeishuUsageExamples
{
    public static async Task RunBotLoopAsync(CancellationToken cancellationToken = default)
    {
        FeishuBotService? service = null;
        service = new FeishuBotService(
            new FeishuChannelOptions
            {
                AppId = "cli_xxxxx",
                AppSecret = "your-app-secret",
                BotDisplayName = "OpenCowork Bot",
                Log = Console.WriteLine
            },
            async (message, ct) =>
            {
                Console.WriteLine($"[{message.ChatName}] {message.SenderName}: {message.Content}");

                if (!string.IsNullOrWhiteSpace(message.Content))
                    await service!.SendMessageAsync(message.ChatId, $"Echo: {message.Content}", ct);
            },
            running => Console.WriteLine($"Feishu running: {running}"));

        await service.StartAsync(cancellationToken);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await service.DisposeAsync();
        }
    }

    public static async Task RunMediaAndMentionExampleAsync(CancellationToken cancellationToken = default)
    {
        await using var service = new FeishuBotService(
            new FeishuChannelOptions
            {
                AppId = "cli_xxxxx",
                AppSecret = "your-app-secret",
                BotDisplayName = "OpenCowork Bot"
            },
            (_, _) => Task.CompletedTask);

        await service.StartAsync(cancellationToken);

        var imageResult = await service.SendImageAsync(
            chatId: "oc_xxx_group_chat_id",
            source: "C:/temp/demo.png",
            cancellationToken: cancellationToken);

        var fileResult = await service.SendFileAsync(
            chatId: "oc_xxx_group_chat_id",
            source: "https://example.com/report.pdf",
            cancellationToken: cancellationToken);

        await service.SendMentionAsync(
            chatId: "oc_xxx_group_chat_id",
            userIds: ["ou_xxx_user_open_id"],
            atAll: false,
            text: "Please review the latest result.",
            cancellationToken: cancellationToken);

        await service.SendUrgentAsync(
            messageId: fileResult.MessageId,
            userIds: ["ou_xxx_user_id"],
            urgentTypes: [FeishuUrgentType.App],
            cancellationToken: cancellationToken);

        Console.WriteLine($"Image messageId: {imageResult.MessageId}");
        Console.WriteLine($"File messageId: {fileResult.MessageId}");
    }

    public static async Task RunStreamingCardExampleAsync(CancellationToken cancellationToken = default)
    {
        await using var service = new FeishuBotService(
            new FeishuChannelOptions
            {
                AppId = "cli_xxxxx",
                AppSecret = "your-app-secret",
                BotDisplayName = "OpenCowork Bot"
            },
            (_, _) => Task.CompletedTask);

        await service.StartAsync(cancellationToken);

        var stream = await service.SendStreamingMessageAsync(
            chatId: "oc_xxx_group_chat_id",
            initialContent: "Preparing answer...",
            replyToMessageId: null,
            cancellationToken: cancellationToken);

        await stream.UpdateAsync("Preparing answer...\n\nStep 1 complete.", cancellationToken);
        await Task.Delay(600, cancellationToken);
        await stream.UpdateAsync("Preparing answer...\n\nStep 1 complete.\nStep 2 complete.", cancellationToken);
        await Task.Delay(600, cancellationToken);
        await stream.FinishAsync("All steps complete. Final answer is ready.", cancellationToken);
    }
}
