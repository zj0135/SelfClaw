import { describe, expect, it } from 'vitest';
import { calculateUsageProgress, formatCompressionNotice, formatTokenCount, ringTick } from '../src/renderers/usageMeter.js';

describe('formatTokenCount', () => {
	it('abbreviates thousands and millions with at most two decimals', () => {
		expect(formatTokenCount(48200)).toBe('48.2K');
		expect(formatTokenCount(1200)).toBe('1.2K');
		expect(formatTokenCount(1240000)).toBe('1.24M');
		expect(formatTokenCount(1000)).toBe('1K');
		expect(formatTokenCount(999)).toBe('999');
	});

	it('returns a dash for unknown or non-positive counts', () => {
		expect(formatTokenCount(null)).toBe('—');
		expect(formatTokenCount(undefined)).toBe('—');
		expect(formatTokenCount(0)).toBe('—');
		expect(formatTokenCount(-5)).toBe('—');
		expect(formatTokenCount('n/a')).toBe('—');
	});

	it('keeps preset abbreviations untouched', () => {
		expect(formatTokenCount('32K')).toBe('32K');
	});

	it('carries into the next unit instead of printing 1000K', () => {
		expect(formatTokenCount(999_999)).toBe('1M');
		expect(formatTokenCount(999_950)).toBe('999.95K');
		expect(formatTokenCount(999_000)).toBe('999K');
	});
});

describe('formatCompressionNotice', () => {
	it('describes the reduction with both sizes and the model tooltip', () => {
		expect(formatCompressionNotice({
			messageCount: 12, sourceTokens: 48200, summaryTokens: 1200, model: 'gpt-x', summary: '# 摘要',
		})).toEqual({
			label: '上下文压缩',
			subtitle: '已压缩 12 条消息 · 48.2K 到 1.2K tokens',
			hasSummary: true,
			title: '压缩模型：gpt-x',
		});
	});

	it('drops the summary size when it is unknown', () => {
		const presentation = formatCompressionNotice({
			messageCount: 3, sourceTokens: 48200, summaryTokens: null, summary: '摘要',
		});
		expect(presentation.subtitle).toBe('已压缩 3 条消息 · 压缩前 48.2K tokens');
	});

	it('falls back to the message count when every size is unknown', () => {
		expect(formatCompressionNotice({ messageCount: 3, summary: '' })).toEqual({
			label: '上下文压缩',
			subtitle: '已压缩 3 条消息',
			hasSummary: false,
			title: '',
		});
	});

	it('ignores a missing notice', () => {
		expect(formatCompressionNotice(null)).toBeNull();
	});
});

describe('calculateUsageProgress', () => {
	const usage = (overrides = {}) => ({
		inputTokens: 127_810, outputTokens: 3_170, contextTokens: 20_620, contextWindowTokens: 1_000_000,
		contextIsEstimated: false, ...overrides,
	});

	it('computes percentage and ratio based on declared context window', () => {
		const progress = calculateUsageProgress(usage());
		expect(progress.limitTokens).toBe(1_000_000);
		expect(progress.isAbsoluteThreshold).toBe(false);
		expect(progress.percentage).toBe(2);
		expect(progress.percentageText).toBe('2%');
		expect(progress.contextText).toBe('20.62K');
		expect(progress.limitText).toBe('1M');
		expect(progress.inputTokensText).toBe('127.81K');
		expect(progress.outputTokensText).toBe('3.17K');
		expect(progress.totalTokensText).toBe('130.98K');
		expect(progress.tone).toBe('normal');
	});

	it('reports the cache hit rate as the cached share of input', () => {
		// 127000 / 127810 = 99.36% -> 99.4%
		const progress = calculateUsageProgress(usage({ cachedInputTokens: 127_000 }));
		expect(progress.cacheHitText).toBe('99.4%');
	});

	it('clamps an impossible cached share instead of printing more than 100%', () => {
		const progress = calculateUsageProgress(usage({ cachedInputTokens: 200_000 }));
		expect(progress.cacheHitText).toBe('100%');
	});

	it('hides the cache hit rate when either side is unknown instead of showing 0%', () => {
		expect(calculateUsageProgress(usage()).cacheHitText).toBeNull();
		expect(calculateUsageProgress(usage({ cachedInputTokens: 0 })).cacheHitText).toBeNull();
		expect(calculateUsageProgress(usage({ cachedInputTokens: 500, inputTokens: 0 })).cacheHitText).toBeNull();
	});

	it('falls back to configured absolute threshold when context window is missing', () => {
		const progress = calculateUsageProgress(usage({ contextWindowTokens: null, contextTokens: 20_000 }), 64_000);
		expect(progress.limitTokens).toBe(64_000);
		expect(progress.isAbsoluteThreshold).toBe(true);
		expect(progress.percentage).toBe(31);
		expect(progress.percentageText).toBe('31%');
		expect(progress.limitText).toBe('64K');
	});

	it('displays <1% when context tokens exist but round down to 0%', () => {
		const progress = calculateUsageProgress(usage({ contextTokens: 100, contextWindowTokens: 1_000_000 }));
		expect(progress.percentage).toBe(0);
		expect(progress.percentageText).toBe('<1%');
	});

	it('sets tone according to context saturation', () => {
		expect(calculateUsageProgress(usage({ contextTokens: 700_000, contextWindowTokens: 1_000_000 })).tone).toBe('warm');
		expect(calculateUsageProgress(usage({ contextTokens: 900_000, contextWindowTokens: 1_000_000 })).tone).toBe('critical');
	});

	it('marks estimated context size with a tilde', () => {
		const progress = calculateUsageProgress(usage({ contextIsEstimated: true }));
		expect(progress.contextText).toBe('~20.62K');
		expect(progress.contextIsEstimated).toBe(true);
	});

	it('returns null when usage is absent', () => {
		expect(calculateUsageProgress(null)).toBeNull();
		expect(calculateUsageProgress(undefined)).toBeNull();
	});

	describe('with the compaction trigger the host resolved', () => {
		// 本机复现配置：窗口 1,048,576，比例 0.5，触发点 524,288。
		const triggered = (overrides = {}) => usage({
			contextWindowTokens: 1_048_576, compactionTriggerTokens: 524_288, ...overrides,
		});

		it('places the trigger on the same scale as the context reading', () => {
			const progress = calculateUsageProgress(triggered({ contextTokens: 262_144 }));
			expect(progress.percentageText).toBe('25%');
			expect(progress.triggerTokens).toBe(524_288);
			expect(progress.triggerRatio).toBe(0.5);
			expect(progress.triggerText).toBe('524.29K');
			expect(progress.triggerShareText).toBe('50%');
		});

		it('colours the meter by the distance to the trigger instead of the window', () => {
			expect(calculateUsageProgress(triggered({ contextTokens: 262_144 })).tone).toBe('normal');
			// 84% / 85% / 100% of the trigger, all far below the window stops of 65% / 85%.
			expect(calculateUsageProgress(triggered({ contextTokens: 440_000 })).tone).toBe('normal');
			expect(calculateUsageProgress(triggered({ contextTokens: 446_000 })).tone).toBe('warm');
			expect(calculateUsageProgress(triggered({ contextTokens: 524_288 })).tone).toBe('critical');
		});

		it('keeps the window stops while the trigger is unknown', () => {
			const progress = calculateUsageProgress(triggered({ compactionTriggerTokens: null, contextTokens: 524_288 }));
			expect(progress.triggerTokens).toBeNull();
			expect(progress.triggerRatio).toBeNull();
			expect(progress.triggerText).toBeNull();
			expect(progress.tone).toBe('normal');
		});

		it('drops a remembered trigger as soon as compaction is switched off', () => {
			const progress = calculateUsageProgress(triggered({ contextTokens: 524_288 }), 64_000, false);
			expect(progress.triggerTokens).toBeNull();
			expect(progress.triggerRatio).toBeNull();
			expect(progress.tone).toBe('normal');
		});

		it('draws no mark when the limit shown already is the trigger', () => {
			const progress = calculateUsageProgress(
				triggered({ contextWindowTokens: null, compactionTriggerTokens: 64_000, contextTokens: 60_000 }), 64_000);
			expect(progress.isAbsoluteThreshold).toBe(true);
			expect(progress.triggerRatio).toBeNull();
			expect(progress.triggerText).toBeNull();
			// 色调仍按触发点算：60K / 64K 已过 85%。
			expect(progress.tone).toBe('warm');
		});

		it('ignores a trigger that is not a positive number', () => {
			for (const value of [0, -1, 'n/a', Number.NaN]) {
				expect(calculateUsageProgress(triggered({ compactionTriggerTokens: value })).triggerTokens).toBeNull();
			}
		});
	});
});

describe('ringTick', () => {
	const ring = { center: 10, inner: 5, outer: 9 };

	it('starts where the progress arc starts and follows it around', () => {
		expect(ringTick(0, ring)).toEqual({ x1: 15, y1: 10, x2: 19, y2: 10 });
		expect(ringTick(0.25, ring)).toEqual({ x1: 10, y1: 15, x2: 10, y2: 19 });
		expect(ringTick(0.5, ring)).toEqual({ x1: 5, y1: 10, x2: 1, y2: 10 });
	});

	it('has no tick without a ratio', () => {
		expect(ringTick(null, ring)).toBeNull();
		expect(ringTick(undefined, ring)).toBeNull();
	});
});
