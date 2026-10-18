using System.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Infrastructure.Attachments;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Desktop.Services.Attachments;

/// <summary>
/// Reclaims attachment directories whose conversation no longer exists. Runs once per start; the
/// per-input cleanup on cancel/discard is owned by the attachment bridge, not here.
/// </summary>
internal sealed class AttachmentMaintenanceHost : BackgroundService
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);

    private readonly StoragePaths _storagePaths;
    private readonly IConversationRepository _conversations;
    private readonly ILogger<AttachmentMaintenanceHost> _logger;

    public AttachmentMaintenanceHost(
        StoragePaths storagePaths,
        IConversationRepository conversations,
        ILogger<AttachmentMaintenanceHost> logger)
    {
        _storagePaths = storagePaths;
        _conversations = conversations;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunOnceAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Startup cleanup is opportunistic: a failure must never take the host down.
            _logger.LogWarning(exception, "Attachment directory cleanup failed; leftovers stay until the next start.");
        }
    }

    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var root = AttachmentPathResolver.ResolveRoot(_storagePaths.AppDataDirectory);
        if (!Directory.Exists(root))
        {
            return;
        }

        var conversations = await _conversations.ListConversationsAsync(cancellationToken).ConfigureAwait(false);
        var liveConversationIds = conversations.Select(conversation => conversation.Id).ToHashSet();
        var staleBeforeUtc = DateTime.UtcNow - StaleAfter;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Guid.TryParse(Path.GetFileName(directory), out var conversationId)
                && liveConversationIds.Contains(conversationId))
            {
                continue;
            }

            // A fresh conversation's directory exists before its row does, and the conversation list
            // only covers interactive conversations, so untouched leftovers alone are reclaimable.
            TryCleanup(directory, staleBeforeUtc);
        }
    }

    private void TryCleanup(string directory, DateTime staleBeforeUtc)
    {
        try
        {
            if (ResolveLastWriteUtc(directory) >= staleBeforeUtc)
            {
                return;
            }

            Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Failed to delete stale attachment directory {Directory}.", directory);
        }
    }

    private static DateTime ResolveLastWriteUtc(string directory)
    {
        DateTime? latest = null;
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var written = File.GetLastWriteTimeUtc(path);
            if (latest is null || written > latest)
            {
                latest = written;
            }
        }

        // An empty directory has no file timestamp, so its own creation time stands in for one.
        return latest ?? Directory.GetLastWriteTimeUtc(directory);
    }
}
