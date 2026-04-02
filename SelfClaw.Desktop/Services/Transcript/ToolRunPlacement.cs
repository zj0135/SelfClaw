using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services;

internal sealed record ToolRunPlacement(ToolExecutionRecord Record, int? AfterSegmentIndex);
