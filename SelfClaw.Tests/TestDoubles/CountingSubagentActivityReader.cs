using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;

namespace SelfClaw.Tests.TestDoubles;

internal sealed class CountingSubagentActivityReader(ISubagentActivityReader inner) : ISubagentActivityReader
{
    private int _listReads;
    private int _detailReads;
    internal int ListReads => Volatile.Read(ref _listReads);
    internal int DetailReads => Volatile.Read(ref _detailReads);
    internal Func<Task>? AfterDetailReadAsync { get; set; }

    public Task<SubagentActivityPage> ListAsync(SubagentActivityQuery query, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _listReads);
        return inner.ListAsync(query, cancellationToken);
    }

    public async Task<SubagentActivityDetail?> GetDetailAsync(Guid parentConversationId, Guid taskId, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _detailReads);
        var result = await inner.GetDetailAsync(parentConversationId, taskId, cancellationToken);
        if (AfterDetailReadAsync is { } afterRead)
        {
            await afterRead();
        }

        return result;
    }

    public Task<SubagentContentPage?> ReadContentAsync(SubagentContentQuery query, CancellationToken cancellationToken = default)
        => inner.ReadContentAsync(query, cancellationToken);
}
