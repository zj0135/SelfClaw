using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.Settings;
using SelfClaw.Desktop.Services.Transcript;
using SelfClaw.Desktop.Services.Transcript.Views;
using SelfClaw.Desktop.Services.WebView;
using SelfClaw.Infrastructure.Automation;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Desktop.Services.Automation;

/// <summary>
/// WebView bridge for the automation module. Reads and writes go straight through the stores; every
/// scheduling side effect is delegated to <see cref="AutomationSchedulerHost"/> so TickerQ stays
/// rebuildable from the task table. Deletion/history reclamation is intentionally absent here.
/// </summary>
internal sealed class AutomationBridge : IDisposable
{
    private const string MessagePrefix = "automation/";
    private const string SettingsNodeName = "automation";
    private const int MaximumTitleLength = 120;

    private static readonly JsonSerializerOptions SettingsJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IAutomationTaskStore _taskStore;
    private readonly IAutomationRunStore _runStore;
    private readonly AutomationScheduleCalculator _calculator;
    private readonly AutomationSchedulerHost _scheduler;
    private readonly AutomationChangeNotifier _notifier;
    private readonly AutomationRunCancellationRegistry _cancellations;
    private readonly IConversationRepository _conversations;
    private readonly IConversationTurnRepository _turns;
    private readonly IUsageStatisticsReader _usage;
    private readonly DesktopSettingsJsonStore _settingsStore;
    private readonly TranscriptProjection _transcriptProjection;
    private readonly WebViewHostChannel _channel;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<AutomationBridge> _logger;
    private bool _disposed;

    public AutomationBridge(
        IAutomationTaskStore taskStore,
        IAutomationRunStore runStore,
        AutomationScheduleCalculator calculator,
        AutomationSchedulerHost scheduler,
        AutomationChangeNotifier notifier,
        AutomationRunCancellationRegistry cancellations,
        IConversationRepository conversations,
        IConversationTurnRepository turns,
        IUsageStatisticsReader usage,
        DesktopSettingsJsonStore settingsStore,
        StoragePaths storagePaths,
        WebViewHostChannel channel,
        Dispatcher dispatcher,
        ILogger<AutomationBridge> logger)
    {
        _taskStore = taskStore;
        _runStore = runStore;
        _calculator = calculator;
        _scheduler = scheduler;
        _notifier = notifier;
        _cancellations = cancellations;
        _conversations = conversations;
        _turns = turns;
        _usage = usage;
        _settingsStore = settingsStore;
        _transcriptProjection = new TranscriptProjection(storagePaths);
        _channel = channel;
        _dispatcher = dispatcher;
        _logger = logger;
        _notifier.Changed += OnChanged;
    }

    public void Dispose()
    {
        _disposed = true;
        _notifier.Changed -= OnChanged;
    }

    public async Task<object?> TryHandleAsync(
        string type,
        JsonElement payload,
        CancellationToken cancellationToken = default)
    {
        if (!type.StartsWith(MessagePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var requestId = ReadOptionalString(payload, "requestId");
        try
        {
            return type switch
            {
                "automation/list-tasks" => await ListTasksAsync(type, requestId, cancellationToken).ConfigureAwait(false),
                "automation/list-calendar" => await ListCalendarAsync(type, requestId, payload, cancellationToken).ConfigureAwait(false),
                "automation/list-runs" => await ListRunsAsync(type, requestId, payload, cancellationToken).ConfigureAwait(false),
                "automation/get-run" => await GetRunAsync(type, requestId, payload, cancellationToken).ConfigureAwait(false),
                "automation/save-task" => await SaveTaskAsync(type, requestId, payload, cancellationToken).ConfigureAwait(false),
                "automation/preview-schedule" => PreviewSchedule(type, requestId, payload),
                "automation/set-task-enabled" => await SetTaskEnabledAsync(type, requestId, payload, cancellationToken).ConfigureAwait(false),
                "automation/run-task-now" => await RunTaskNowAsync(type, requestId, payload, cancellationToken).ConfigureAwait(false),
                "automation/cancel-run" => await CancelRunAsync(type, requestId, payload, cancellationToken).ConfigureAwait(false),
                "automation/get-settings" => await GetSettingsAsync(type, requestId, cancellationToken).ConfigureAwait(false),
                "automation/set-settings" => await SetSettingsAsync(type, requestId, payload, cancellationToken).ConfigureAwait(false),
                _ => Failure(type, requestId, "Unsupported", $"Unsupported automation message '{type}'.")
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Automation bridge message {Type} failed.", type);
            return Failure(type, requestId, "OperationFailed", exception.Message);
        }
    }

    private void OnChanged(string reason, Guid? taskId, Guid? runId)
    {
        if (_disposed || _dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.InvokeAsync(() => OnChanged(reason, taskId, runId));
            return;
        }

        _channel.PostPush(new
        {
            type = "automation/changed",
            reason,
            taskId = taskId?.ToString("D"),
            runId = runId?.ToString("D")
        });
    }

    private async Task<object> ListTasksAsync(string type, string? requestId, CancellationToken cancellationToken)
    {
        var tasks = await _taskStore.ListAsync(cancellationToken).ConfigureAwait(false);
        return new { type, requestId, ok = true, tasks = tasks.Select(ToTask).ToArray() };
    }

    private async Task<object> ListCalendarAsync(
        string type,
        string? requestId,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        if (!TryReadUtc(payload, "fromUtc", out var fromUtc) || !TryReadUtc(payload, "toUtc", out var toUtc) || toUtc <= fromUtc)
        {
            return Failure(type, requestId, AutomationErrorCodes.InvalidSchedule, "A valid fromUtc/toUtc range is required.");
        }

        var now = DateTimeOffset.UtcNow;
        var limit = AutomationDefaults.ProjectionLimit;
        var tasks = await _taskStore.ListAsync(cancellationToken).ConfigureAwait(false);
        var tasksById = tasks.ToDictionary(task => task.Id);
        var events = new List<AutomationCalendarEvent>();
        var truncated = false;
        foreach (var task in tasks)
        {
            if (events.Count >= limit)
            {
                truncated = true;
                break;
            }

            if (task.IsEnabled && task.Kind != AutomationTaskKind.None)
            {
                foreach (var atUtc in _calculator.ProjectOccurrences(task.Kind, task.Schedule, fromUtc, toUtc, limit - events.Count))
                {
                    events.Add(new AutomationCalendarEvent("projected", task.Id, atUtc, false, task.Title, TaskKind: TaskKindWire(task.Kind)));
                }

                continue;
            }

            if (task.Kind == AutomationTaskKind.None && task.DueAtUtc is DateTimeOffset due &&
                (due >= fromUtc && due <= toUtc || due < now))
            {
                events.Add(new AutomationCalendarEvent(
                    "todo",
                    task.Id,
                    due,
                    true,
                    task.Title,
                    Status: task.Status == AutomationTaskStatus.Done ? "done" : "open",
                    IsOverdue: task.Status == AutomationTaskStatus.Open && due < now,
                    TaskKind: "none"));
            }
        }

        foreach (var run in await _runStore.ListAsync(null, limit, cancellationToken).ConfigureAwait(false))
        {
            var atUtc = run.CompletedAtUtc ?? run.StartedAtUtc ?? run.CreatedAtUtc;
            if (atUtc < fromUtc || atUtc > toUtc)
            {
                continue;
            }

            events.Add(new AutomationCalendarEvent(
                "run",
                run.TaskId,
                atUtc,
                false,
                tasksById.TryGetValue(run.TaskId, out var task) ? task.Title : "自动化任务",
                RunId: run.Id,
                Status: RunStatusWire(run.Status),
                DurationMs: DurationMs(run)));
        }

        events.Sort((left, right) => left.AtUtc.CompareTo(right.AtUtc));
        if (events.Count > limit)
        {
            events.RemoveRange(limit, events.Count - limit);
            truncated = true;
        }

        return new { type, requestId, ok = true, events = events.ToArray(), truncated };
    }

    private async Task<object> ListRunsAsync(
        string type,
        string? requestId,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var taskId = ReadOptionalGuid(payload, "taskId");
        var status = ReadOptionalString(payload, "status");
        var limit = Math.Clamp(ReadOptionalInt32(payload, "limit") ?? 100, 1, AutomationDefaults.ProjectionLimit);
        var runs = await _runStore.ListAsync(taskId, limit, cancellationToken).ConfigureAwait(false);
        var projected = status is null
            ? runs
            : runs.Where(run => RunStatusWire(run.Status) == status).ToArray();
        return new { type, requestId, ok = true, runs = projected.Select(ToRun).ToArray() };
    }

    private async Task<object> GetRunAsync(
        string type,
        string? requestId,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        if (ReadOptionalGuid(payload, "runId") is not Guid runId)
        {
            return Failure(type, requestId, AutomationErrorCodes.UnknownRun, "runId is required.");
        }

        var run = await _runStore.GetAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return Failure(type, requestId, AutomationErrorCodes.UnknownRun, "The automation run no longer exists.");
        }

        var task = await _taskStore.GetAsync(run.TaskId, cancellationToken).ConfigureAwait(false);
        var transcript = await BuildTranscriptAsync(run, cancellationToken).ConfigureAwait(false);
        var usage = run.ConversationId is Guid conversationId
            ? await _usage.GetConversationUsageAsync(conversationId, cancellationToken).ConfigureAwait(false)
            : null;
        return new
        {
            type,
            requestId,
            ok = true,
            run = ToRun(run),
            task = task is null ? null : ToTask(task),
            transcript,
            usage = ToUsage(usage)
        };
    }

    private async Task<object> SaveTaskAsync(
        string type,
        string? requestId,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        if (!payload.TryGetProperty("task", out var element) || element.ValueKind != JsonValueKind.Object)
        {
            return Failure(type, requestId, "InvalidTask", "task is required.");
        }

        var title = (ReadOptionalString(element, "title") ?? string.Empty).Trim();
        if (title.Length == 0)
        {
            return Failure(type, requestId, AutomationErrorCodes.InvalidTitle, "任务标题必填。");
        }

        if (title.Length > MaximumTitleLength)
        {
            return Failure(type, requestId, AutomationErrorCodes.InvalidTitle, $"任务标题不能超过 {MaximumTitleLength} 个字符。");
        }

        var kind = ReadTaskKind(element);
        var prompt = ReadOptionalString(element, "prompt");
        if (kind != AutomationTaskKind.None && string.IsNullOrWhiteSpace(prompt))
        {
            return Failure(type, requestId, AutomationErrorCodes.TriggerRequiresPrompt, "带触发器的任务必须填写提示词。");
        }

        var requestedId = ReadOptionalGuid(element, "id");
        var existing = requestedId is Guid id
            ? await _taskStore.GetAsync(id, cancellationToken).ConfigureAwait(false)
            : null;
        var schedule = ReadSchedule(element);
        var isEnabled = ReadOptionalBoolean(element, "isEnabled") ?? existing?.IsEnabled ?? true;
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? nextRunAtUtc = null;
        if (isEnabled && kind != AutomationTaskKind.None &&
            !_calculator.TryComputeNextRun(kind, schedule, now, out nextRunAtUtc, out var errorCode))
        {
            return Failure(type, requestId, errorCode ?? AutomationErrorCodes.InvalidSchedule, ScheduleMessage(errorCode));
        }

        var status = ReadTaskStatus(element) ?? existing?.Status ?? AutomationTaskStatus.Open;
        var task = new AutomationTaskRecord(
            existing?.Id ?? requestedId ?? Guid.NewGuid(),
            title,
            ReadOptionalString(element, "notes") ?? existing?.Notes ?? string.Empty,
            kind,
            kind == AutomationTaskKind.None ? null : schedule,
            nextRunAtUtc,
            prompt,
            ReadOptionalString(element, "instructions") ?? existing?.Instructions ?? string.Empty,
            ReadOptionalString(element, "toolPolicy") ?? existing?.ToolPolicy ?? "system",
            ReadStringArray(element, "pluginIds") ?? existing?.PluginIds ?? [],
            ReadStringArray(element, "skillIds") ?? existing?.SkillIds ?? [],
            ReadStringArray(element, "mcpServerIds") ?? existing?.McpServerIds ?? [],
            ReadGuidNode(element, "modelProfileId", existing?.ModelProfileId),
            ReadGuidNode(element, "workspaceRootId", existing?.WorkspaceRootId),
            existing?.ToolPermissionMode ?? ToolPermissionMode.RequireApproval,
            status,
            status == AutomationTaskStatus.Done
                ? ReadOptionalUtc(element, "completedAtUtc") ?? existing?.CompletedAtUtc ?? now
                : null,
            prompt is null ? ReadOptionalUtc(element, "dueAtUtc") ?? existing?.DueAtUtc : null,
            isEnabled,
            existing?.LastRunAtUtc,
            existing?.LastRunStatus,
            existing?.CreatedAtUtc ?? now,
            now);

        var saved = await _taskStore.SaveAsync(task, cancellationToken).ConfigureAwait(false);
        _notifier.Notify("task-saved", saved.Id);
        await _scheduler.RequestRescheduleAsync(cancellationToken).ConfigureAwait(false);
        return new
        {
            type,
            requestId,
            ok = true,
            task = ToTask(saved),
            nextRunAtUtc = saved.NextRunAtUtc?.ToString("O")
        };
    }

    private object PreviewSchedule(string type, string? requestId, JsonElement payload)
    {
        var kind = ReadTaskKind(payload);
        if (kind == AutomationTaskKind.None)
        {
            return new { type, requestId, ok = true, nextRunAtUtc = (string?)null };
        }

        if (!_calculator.TryComputeNextRun(kind, ReadSchedule(payload), DateTimeOffset.UtcNow, out var nextRunAtUtc, out var errorCode))
        {
            return Failure(type, requestId, errorCode ?? AutomationErrorCodes.InvalidSchedule, ScheduleMessage(errorCode));
        }

        return new { type, requestId, ok = true, nextRunAtUtc = nextRunAtUtc?.ToString("O") };
    }

    private async Task<object> SetTaskEnabledAsync(
        string type,
        string? requestId,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        if (ReadOptionalGuid(payload, "taskId") is not Guid taskId)
        {
            return Failure(type, requestId, AutomationErrorCodes.UnknownTask, "taskId is required.");
        }

        var isEnabled = ReadRequiredBoolean(payload, "isEnabled");
        var existing = await _taskStore.GetAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return Failure(type, requestId, AutomationErrorCodes.UnknownTask, "The automation task no longer exists.");
        }

        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? nextRunAtUtc = null;
        if (isEnabled && existing.Kind != AutomationTaskKind.None &&
            _calculator.TryComputeNextRun(existing.Kind, existing.Schedule, now, out var computed, out _))
        {
            nextRunAtUtc = computed;
        }

        var saved = await _taskStore.SaveAsync(
            existing with { IsEnabled = isEnabled, NextRunAtUtc = nextRunAtUtc, UpdatedAtUtc = now },
            cancellationToken).ConfigureAwait(false);
        _notifier.Notify("task-enabled", saved.Id);
        await _scheduler.RequestRescheduleAsync(cancellationToken).ConfigureAwait(false);
        return new { type, requestId, ok = true, task = ToTask(saved) };
    }

    private async Task<object> RunTaskNowAsync(
        string type,
        string? requestId,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        if (ReadOptionalGuid(payload, "taskId") is not Guid taskId)
        {
            return Failure(type, requestId, AutomationErrorCodes.UnknownTask, "taskId is required.");
        }

        var run = await _scheduler.TryStartManualRunAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return Failure(type, requestId, AutomationErrorCodes.UnknownTask, "该任务不存在或没有提示词。");
        }

        if (run.Status == AutomationRunStatus.Skipped)
        {
            return Failure(type, requestId, AutomationErrorCodes.RunConflict, "该任务已有正在进行的运行。");
        }

        return new { type, requestId, ok = true, runId = run.Id.ToString("D"), run = ToRun(run) };
    }

    private async Task<object> CancelRunAsync(
        string type,
        string? requestId,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        if (ReadOptionalGuid(payload, "runId") is not Guid runId)
        {
            return Failure(type, requestId, AutomationErrorCodes.UnknownRun, "runId is required.");
        }

        var run = await _runStore.GetAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return Failure(type, requestId, AutomationErrorCodes.UnknownRun, "The automation run no longer exists.");
        }

        if (run.Status is AutomationRunStatus.Queued or AutomationRunStatus.Running)
        {
            _cancellations.CancelRun(runId);
        }

        var current = await _runStore.GetAsync(runId, cancellationToken).ConfigureAwait(false) ?? run;
        return new { type, requestId, ok = true, run = ToRun(current) };
    }

    private async Task<object> GetSettingsAsync(string type, string? requestId, CancellationToken cancellationToken)
    {
        var settings = await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        return new { type, requestId, ok = true, settings = ToSettings(settings) };
    }

    private async Task<object> SetSettingsAsync(
        string type,
        string? requestId,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var settings = new AutomationSettings(ReadOptionalBoolean(payload, "notifyOnSuccess") ?? false);
        await _settingsStore.WriteNodeAsync(SettingsNodeName, settings, SettingsJsonOptions, cancellationToken).ConfigureAwait(false);
        return new { type, requestId, ok = true, settings = ToSettings(settings) };
    }

    private async Task<AutomationSettings> ReadSettingsAsync(CancellationToken cancellationToken)
        => await _settingsStore.ReadNodeAsync<AutomationSettings>(SettingsNodeName, SettingsJsonOptions, cancellationToken)
            .ConfigureAwait(false) ?? new AutomationSettings();

    private async Task<object> BuildTranscriptAsync(AutomationRunRecord run, CancellationToken cancellationToken)
    {
        if (run.ConversationId is not Guid conversationId)
        {
            return new { items = Array.Empty<TranscriptRenderItem>() };
        }

        var conversation = await _conversations.GetConversationAsync(conversationId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<MessageRecord> messages = conversation is null
            ? []
            : await _conversations.ListMessagesAsync(conversationId, cancellationToken).ConfigureAwait(false);
        if (conversation is null || messages.Count == 0)
        {
            return new { items = Array.Empty<TranscriptRenderItem>() };
        }

        var turns = await _turns.ListTurnsAsync(conversationId, cancellationToken).ConfigureAwait(false);
        var toolRuns = await _conversations.ListToolExecutionsAsync(conversationId, cancellationToken).ConfigureAwait(false);
        _transcriptProjection.Invalidate();
        var state = _transcriptProjection.Build(new TranscriptProjectionRequest(
            messages,
            turns,
            toolRuns,
            [conversation],
            [],
            conversationId,
            AutoScroll: false,
            IsBusy: false,
            ActivityText: null,
            AgentMode: "direct",
            SelectedAgentId: string.Empty,
            SelectedAgentName: string.Empty,
            CapabilityRevision: 0,
            ToolPermissionMode: "fullAccess"));
        return new { items = state?.Items ?? [] };
    }

    private static object Failure(string type, string? requestId, string errorCode, string errorMessage)
        => new { type, requestId, ok = false, errorCode, errorMessage };

    private static object ToTask(AutomationTaskRecord task)
        => new
        {
            id = task.Id.ToString("D"),
            title = task.Title,
            notes = task.Notes,
            kind = TaskKindWire(task.Kind),
            schedule = task.Schedule is null ? null : ToSchedule(task.Schedule),
            nextRunAtUtc = task.NextRunAtUtc?.ToString("O"),
            prompt = task.Prompt,
            instructions = task.Instructions,
            toolPolicy = task.ToolPolicy,
            pluginIds = task.PluginIds,
            skillIds = task.SkillIds,
            mcpServerIds = task.McpServerIds,
            modelProfileId = task.ModelProfileId?.ToString("D"),
            workspaceRootId = task.WorkspaceRootId?.ToString("D"),
            toolPermissionMode = task.ToolPermissionMode == ToolPermissionMode.FullAccess ? "fullAccess" : "requireApproval",
            status = task.Status == AutomationTaskStatus.Done ? "done" : "open",
            completedAtUtc = task.CompletedAtUtc?.ToString("O"),
            dueAtUtc = task.DueAtUtc?.ToString("O"),
            isEnabled = task.IsEnabled,
            lastRunAtUtc = task.LastRunAtUtc?.ToString("O"),
            lastRunStatus = task.LastRunStatus is AutomationRunStatus status ? RunStatusWire(status) : null,
            createdAtUtc = task.CreatedAtUtc.ToString("O"),
            updatedAtUtc = task.UpdatedAtUtc.ToString("O")
        };

    private static object ToRun(AutomationRunRecord run)
        => new
        {
            id = run.Id.ToString("D"),
            taskId = run.TaskId.ToString("D"),
            conversationId = run.ConversationId?.ToString("D"),
            trigger = run.Trigger switch
            {
                AutomationRunTrigger.Manual => "manual",
                AutomationRunTrigger.CatchUp => "catchUp",
                _ => "scheduled"
            },
            scheduledForUtc = run.ScheduledForUtc?.ToString("O"),
            status = RunStatusWire(run.Status),
            skipReason = run.SkipReason switch
            {
                AutomationRunSkipReason.Overlap => "overlap",
                AutomationRunSkipReason.Stale => "stale",
                _ => null
            },
            attempt = run.Attempt,
            modelProfileId = run.ModelProfileId?.ToString("D"),
            workspaceRootId = run.WorkspaceRootId?.ToString("D"),
            workspacePathSnapshot = run.WorkspacePathSnapshot,
            startedAtUtc = run.StartedAtUtc?.ToString("O"),
            completedAtUtc = run.CompletedAtUtc?.ToString("O"),
            errorCode = run.ErrorCode,
            errorMessage = run.ErrorMessage,
            createdAtUtc = run.CreatedAtUtc.ToString("O"),
            durationMs = DurationMs(run)
        };

    private static object ToSettings(AutomationSettings settings)
        => new { notifyOnSuccess = settings.NotifyOnSuccess };

    private static object? ToUsage(UsageReport? usage)
        => usage is null
            ? null
            : new
            {
                inputTokens = usage.Totals.InputTokens,
                outputTokens = usage.Totals.OutputTokens,
                totalTokens = usage.Totals.TotalTokens,
                costUsdMicros = usage.Totals.CostUsdMicros,
                turnCount = usage.Totals.TurnCount
            };

    private static object ToSchedule(AutomationScheduleSpec schedule)
        => new
        {
            version = schedule.Version,
            time = schedule.Time,
            weekdays = schedule.Weekdays,
            dayOfMonth = schedule.DayOfMonth,
            runAtUtc = schedule.RunAtUtc?.ToString("O"),
            cron = schedule.Cron
        };

    private static long? DurationMs(AutomationRunRecord run)
        => run.StartedAtUtc is DateTimeOffset started && run.CompletedAtUtc is DateTimeOffset completed
            ? (long)Math.Max(0, (completed - started).TotalMilliseconds)
            : null;

    private static string TaskKindWire(AutomationTaskKind kind)
        => kind switch
        {
            AutomationTaskKind.Once => "once",
            AutomationTaskKind.Daily => "daily",
            AutomationTaskKind.Weekly => "weekly",
            AutomationTaskKind.Monthly => "monthly",
            AutomationTaskKind.Cron => "cron",
            _ => "none"
        };

    private static string RunStatusWire(AutomationRunStatus status)
        => status switch
        {
            AutomationRunStatus.Running => "running",
            AutomationRunStatus.Completed => "completed",
            AutomationRunStatus.Failed => "failed",
            AutomationRunStatus.Cancelled => "cancelled",
            AutomationRunStatus.Interrupted => "interrupted",
            AutomationRunStatus.Skipped => "skipped",
            _ => "queued"
        };

    private static string ScheduleMessage(string? errorCode)
        => errorCode == AutomationErrorCodes.IntervalTooShort
            ? $"触发间隔不能小于 {AutomationDefaults.MinimumInterval.TotalMinutes:0} 分钟。"
            : "触发器配置无效。";

    private static AutomationTaskKind ReadTaskKind(JsonElement element)
        => ReadOptionalString(element, "kind")?.Trim().ToLowerInvariant() switch
        {
            null or "" or "none" => AutomationTaskKind.None,
            "once" => AutomationTaskKind.Once,
            "daily" => AutomationTaskKind.Daily,
            "weekly" => AutomationTaskKind.Weekly,
            "monthly" => AutomationTaskKind.Monthly,
            "cron" => AutomationTaskKind.Cron,
            _ => throw new ArgumentException("Property 'kind' has an unsupported value.")
        };

    private static AutomationTaskStatus? ReadTaskStatus(JsonElement element)
        => ReadOptionalString(element, "status")?.Trim().ToLowerInvariant() switch
        {
            null => null,
            "open" => AutomationTaskStatus.Open,
            "done" => AutomationTaskStatus.Done,
            _ => throw new ArgumentException("Property 'status' has an unsupported value.")
        };

    private static AutomationScheduleSpec? ReadSchedule(JsonElement element)
    {
        if (!element.TryGetProperty("schedule", out var schedule) || schedule.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new AutomationScheduleSpec(
            ReadOptionalInt32(schedule, "version") ?? 1,
            ReadOptionalString(schedule, "time"),
            ReadInt32Array(schedule, "weekdays"),
            ReadOptionalInt32(schedule, "dayOfMonth"),
            ReadOptionalUtc(schedule, "runAtUtc"),
            ReadOptionalString(schedule, "cron"));
    }

    private static string? ReadOptionalString(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = element.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static Guid? ReadOptionalGuid(JsonElement payload, string propertyName)
    {
        var value = ReadOptionalString(payload, propertyName);
        return Guid.TryParse(value, out var guid) && guid != Guid.Empty ? guid : null;
    }

    private static Guid? ReadGuidNode(JsonElement payload, string propertyName, Guid? fallback)
    {
        if (!payload.TryGetProperty(propertyName, out var element))
        {
            return fallback;
        }

        return ReadOptionalGuid(payload, propertyName);
    }

    private static bool? ReadOptionalBoolean(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.GetBoolean()
            : throw new ArgumentException($"Property '{propertyName}' must be a boolean.");
    }

    private static bool ReadRequiredBoolean(JsonElement payload, string propertyName)
        => ReadOptionalBoolean(payload, propertyName)
            ?? throw new ArgumentException($"Boolean property '{propertyName}' is required.");

    private static int? ReadOptionalInt32(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return element.TryGetInt32(out var value)
            ? value
            : throw new ArgumentException($"Property '{propertyName}' must be an integer.");
    }

    private static IReadOnlyList<int>? ReadInt32Array(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException($"Property '{propertyName}' must be an array.");
        }

        return element.EnumerateArray().Select(item =>
            item.TryGetInt32(out var value)
                ? value
                : throw new ArgumentException($"Property '{propertyName}' must contain only integers.")).ToArray();
    }

    private static IReadOnlyList<string>? ReadStringArray(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException($"Property '{propertyName}' must be an array.");
        }

        return element.EnumerateArray().Select(item =>
            item.ValueKind == JsonValueKind.String
                ? item.GetString() ?? string.Empty
                : throw new ArgumentException($"Property '{propertyName}' must contain only strings.")).ToArray();
    }

    private static DateTimeOffset? ReadOptionalUtc(JsonElement payload, string propertyName)
    {
        var value = ReadOptionalString(payload, propertyName);
        return value is null || !DateTimeOffset.TryParse(value, out var parsed) ? null : parsed.ToUniversalTime();
    }

    private static bool TryReadUtc(JsonElement payload, string propertyName, out DateTimeOffset value)
    {
        var parsed = ReadOptionalUtc(payload, propertyName);
        value = parsed ?? default;
        return parsed is not null;
    }
}
