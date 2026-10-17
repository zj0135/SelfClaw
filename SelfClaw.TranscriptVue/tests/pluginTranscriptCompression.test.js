import { describe, expect, it } from 'vitest';
import { createPluginTranscriptProjector, maximumPluginTranscriptBytes } from '../src/renderers/pluginTranscript.js';

const compressionItem = (summaryLength = 300_000) => ({
	id: 'compression:c1', kind: 'compression', role: 'system', status: 'completed', segments: [],
	compaction: {
		compactionId: 'c1', messageCount: 42, sourceTokens: 182_000, summaryTokens: 6_400,
		model: 'gpt-x', summary: `# 摘要\n${'压缩后的结论。'.repeat(summaryLength / 7)}`,
	},
});

describe('plugin transcript projection of metadata-only kinds', () => {
	it('keeps an oversized compression item a compression item with a length-capped summary', () => {
		const item = compressionItem();
		const snapshot = createPluginTranscriptProjector()({ revision: 7, items: [item] });

		expect(snapshot.truncated).toBe(true);
		expect(snapshot.totalItems).toBe(1);
		const projected = snapshot.items[0];
		expect(projected.id).toBe('compression:c1');
		expect(projected.kind).toBe('compression');
		expect(projected.contentTruncated).toBe(true);
		// 分隔项没有正文：退化成空 segments，而不是伪造一个 content 段。
		expect(projected.segments).toEqual([]);
		expect(projected.compaction.summary).toHaveLength(2048);
		expect(item.compaction.summary.startsWith(projected.compaction.summary)).toBe(true);
		// 元数据跟着保留，插件仍能渲染副标题与模型提示。
		expect(projected.compaction).toMatchObject({
			compactionId: 'c1', messageCount: 42, sourceTokens: 182_000, summaryTokens: 6_400, model: 'gpt-x',
		});
		expect(new TextEncoder().encode(JSON.stringify(snapshot)).length).toBeLessThan(maximumPluginTranscriptBytes);
	});

	it('drops an unknown compaction payload instead of inventing one', () => {
		const item = { ...compressionItem(), compaction: undefined };
		const projected = createPluginTranscriptProjector()({ revision: 1, items: [item] }).items[0];

		expect(projected.kind).toBe('compression');
		// JSON 序列化会丢掉 null 键，总之不得凭空造出摘要。
		expect(projected.compaction ?? null).toBeNull();
	});

	it('still bounds turn-outcome items without adding a body', () => {
		const item = {
			id: 'turn-outcome:t1', kind: 'turn-outcome', role: 'system', status: 'failed', segments: [],
			turnOutcome: {
				turnId: 't1', status: 'failed', errorMessage: 'failure'.repeat(100_000), durationMs: 100,
				usage: { inputTokens: 3, outputTokens: 4, totalTokens: null },
			},
		};
		const projected = createPluginTranscriptProjector()({ revision: 1, items: [item] }).items[0];

		expect(projected.kind).toBe('turn-outcome');
		expect(projected.segments).toEqual([]);
		expect(projected.contentTruncated).toBe(true);
		expect(projected.turnOutcome.status).toBe('failed');
		expect(projected.turnOutcome.errorMessage).toHaveLength(2048);
		expect(projected.turnOutcome.usage.totalTokens).toBeNull();
	});
});
