export const maximumPluginTranscriptBytes = 256 * 1024;
const encoder = new TextEncoder();
const maxMetadataChars = 2048;
// 分隔项与回合终态没有正文：超限时只能退化成带元数据的精简项，写成 content 段会让插件把说明/摘要当成消息正文。
const metadataOnlyKinds = new Set(['turn-outcome', 'compression']);

function truncateTurnOutcome(outcome) {
    if (!outcome) return null;
    return { ...outcome,
        errorMessage: typeof outcome.errorMessage === 'string' ? outcome.errorMessage.slice(0, maxMetadataChars) : null,
        usage: outcome.usage ? { totalTokens: outcome.usage.totalTokens ?? null } : null };
}

function truncateCompaction(compaction) {
    if (!compaction) return null;
    return { compactionId: compaction.compactionId ?? null, messageCount: compaction.messageCount ?? 0,
        sourceTokens: compaction.sourceTokens ?? null, summaryTokens: compaction.summaryTokens ?? null,
        model: typeof compaction.model === 'string' ? compaction.model : null,
        summary: typeof compaction.summary === 'string' ? compaction.summary.slice(0, maxMetadataChars) : '' };
}

function truncateItem(item) {
    const truncated = { id: item.id, kind: item.kind, role: item.role, status: item.status,
        turnOutcome: truncateTurnOutcome(item.turnOutcome), contentTruncated: true,
        segments: metadataOnlyKinds.has(item.kind) ? [] : [{ kind: 'content', markdown: '此消息超过插件摘要窗口的大小限制。' }] };
    if (item.kind === 'compression') truncated.compaction = truncateCompaction(item.compaction);
    return truncated;
}

export function createPluginTranscriptProjector() {
    const cache = new WeakMap();
    function projectItem(item) {
        let projected = cache.get(item);
        if (projected) return projected;
        let json = JSON.stringify(item);
        let bytes = encoder.encode(json).length;
        let truncated = false;
        if (bytes > maximumPluginTranscriptBytes - 1024) {
            truncated = true;
            json = JSON.stringify(truncateItem(item));
            bytes = encoder.encode(json).length;
        }
        projected = { item: JSON.parse(json), bytes, truncated };
        cache.set(item, projected);
        return projected;
    }
    return (payload) => {
        const source = Array.isArray(payload?.items) ? payload.items : [];
        const items = [];
        let bytes = 1024;
        let truncated = false;
        for (let index = source.length - 1; index >= 0; index--) {
            const projected = projectItem(source[index]);
            if (bytes + projected.bytes + 1 > maximumPluginTranscriptBytes) { truncated = true; break; }
            bytes += projected.bytes + 1;
            items.unshift(projected.item);
            truncated ||= projected.truncated;
        }
        return { revision: payload?.revision || 0, items, totalItems: source.length, truncated };
    };
}
