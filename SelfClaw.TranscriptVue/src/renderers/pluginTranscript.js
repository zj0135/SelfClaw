export const maximumPluginTranscriptBytes = 256 * 1024;
const encoder = new TextEncoder();

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
            json = JSON.stringify({ id: item.id, kind: item.kind, role: item.role, status: item.status,
                contentTruncated: true, segments: [{ kind: 'content', markdown: '此消息超过插件摘要窗口的大小限制。' }] });
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
