import { describe, expect, it } from 'vitest';
import { calculateUsageProgress, formatCompressionNotice, formatContextUsage, formatTokenCount } from '../src/renderers/usageMeter.js';

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

describe('formatContextUsage', () => {
	const usage = (overrides = {}) => ({
		inputTokens: 1_240_000, outputTokens: 342_000, contextTokens: 12_400, contextWindowTokens: 32_000,
		contextIsEstimated: false, ...overrides,
	});

	it('shows the context size against its window plus the cumulative totals', () => {
		expect(formatContextUsage(usage())).toEqual({
			contextText: '12.4K / 32K',
			contextTitle: '',
			totals: [{ label: '输入', value: '1.24M' }, { label: '输出', value: '342K' }],
		});
	});

	it('falls back to an unavailable context when the size is unknown', () => {
		expect(formatContextUsage(usage({ contextTokens: null }))).toMatchObject({
			contextText: '不可用',
			contextTitle: '',
		});
	});

	it('drops the window when the model declares none', () => {
		expect(formatContextUsage(usage({ contextWindowTokens: null })).contextText).toBe('12.4K');
	});

	it('marks an approximate size with a tilde and explains it in the tooltip', () => {
		expect(formatContextUsage(usage({ contextIsEstimated: true }))).toMatchObject({
			contextText: '~12.4K / 32K',
			contextTitle: '由本地计数近似',
		});
	});

	it('omits the cumulative part while no turn has reported tokens', () => {
		expect(formatContextUsage(usage({ inputTokens: 0, outputTokens: 0 })).totals).toEqual([]);
	});

	it('keeps only the reported half of the cumulative totals', () => {
		expect(formatContextUsage(usage({ outputTokens: null })).totals).toEqual([{ label: '输入', value: '1.24M' }]);
	});

	it('ignores a missing usage summary', () => {
		expect(formatContextUsage(null)).toBeNull();
		expect(formatContextUsage(undefined)).toBeNull();
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
});
