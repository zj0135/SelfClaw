namespace SelfClaw.Core.Models;

public sealed record TodoItem(string Content, TodoItemStatus Status, string? ActiveForm = null);
