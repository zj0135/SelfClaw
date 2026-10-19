using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace SelfClaw.Infrastructure.Agents.Direct;

/// <summary>
/// Detects a turn that keeps calling the same tools with the same arguments and getting the same results.
/// A request-count cap cannot tell a long task from a stuck one; this can, because a loop only shows
/// progress by producing something new. Consecutive identical batches are the signal, so an agent that
/// polls a build sees a different result each time and never trips it.
/// </summary>
internal sealed class NoProgressGuard
{
    /// <summary>
    /// Five in a row: a retry, a re-read after an edit and a second look at a file are all legitimate, so the
    /// guard waits until the pattern is unmistakable. The cost of waiting is bounded by the request budget.
    /// </summary>
    internal const int RepeatedBatchLimit = 5;

    private readonly StringBuilder _fingerprint = new();
    private string? _previousFingerprint;
    private int _repeatedBatches;

    internal int RepeatedBatches => _repeatedBatches;

    /// <summary>
    /// Records one completed tool batch. Returns true once the same batch repeats
    /// <see cref="RepeatedBatchLimit"/> times, which is the point at which the caller must stop the turn.
    /// </summary>
    internal bool Observe(IReadOnlyList<FunctionCallContent> calls, IEnumerable<AIContent> results)
    {
        var fingerprint = BuildFingerprint(calls, results);
        if (string.Equals(fingerprint, _previousFingerprint, StringComparison.Ordinal))
        {
            _repeatedBatches++;
        }
        else
        {
            _previousFingerprint = fingerprint;
            _repeatedBatches = 1;
        }

        return _repeatedBatches >= RepeatedBatchLimit;
    }

    // Call arguments and results decide progress; the call id is random per response and must not enter the
    // fingerprint, or two identical calls would look different.
    private string BuildFingerprint(IReadOnlyList<FunctionCallContent> calls, IEnumerable<AIContent> results)
    {
        _fingerprint.Clear();
        foreach (var call in calls)
        {
            _fingerprint.Append('\u001f').Append(call.Name)
                .Append('\u001e').Append(Serialize(call.Arguments));
        }

        foreach (var content in results)
        {
            _fingerprint.Append('\u001d');
            if (content is FunctionResultContent result)
            {
                _fingerprint.Append(Serialize(result.Result));
                _fingerprint.Append('\u001e').Append(result.Exception?.Message);
            }
            else
            {
                _fingerprint.Append(Serialize(content));
            }
        }

        return _fingerprint.ToString();
    }

    private static string Serialize(object? value) => value switch
    {
        null => string.Empty,
        string text => text,
        JsonElement element => element.GetRawText(),
        _ => JsonSerializer.Serialize(value)
    };
}
