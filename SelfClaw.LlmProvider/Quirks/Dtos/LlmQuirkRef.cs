namespace SelfClaw.LlmProvider.Quirks.Dtos;

/// <summary>对某个具名 quirk 的一次引用。</summary>
/// <param name="Id">见 <see cref="LlmQuirkRegistry"/> 里注册的 id。</param>
/// <param name="Parameters">该条目自己的参数，例如 <c>api-version</c>。</param>
public sealed record LlmQuirkRef(string Id, IReadOnlyDictionary<string, string>? Parameters = null)
{
    public string Id { get; } = string.IsNullOrWhiteSpace(Id)
        ? throw new ArgumentException("A quirk id is required.", nameof(Id))
        : Id;
}
