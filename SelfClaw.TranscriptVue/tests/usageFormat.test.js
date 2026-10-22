import { describe, expect, it } from 'vitest';
import {
	formatCompactTokens,
	formatDayLabel,
	formatDuration,
	formatMonthLabel,
	formatPercent,
	formatTokenCount,
	parseLocalDate,
} from '../src/renderers/usageFormat.js';

describe('formatTokenCount', () => {
	it('carries 999999 into the next unit instead of printing 1000K', () => {
		expect(formatTokenCount(999_999)).toBe('1M');
		expect(formatTokenCount(999_950)).toBe('999.95K');
		expect(formatTokenCount(1_240_000)).toBe('1.24M');
	});

	it('keeps the dash for unknown counts so usageMeter can rely on it', () => {
		expect(formatTokenCount(0)).toBe('—');
		expect(formatTokenCount(null)).toBe('—');
	});
});

describe('formatCompactTokens', () => {
	it('treats zero as a real value and only blanks unknown input', () => {
		expect(formatCompactTokens(0)).toBe('0');
		expect(formatCompactTokens(1234)).toBe('1.23K');
		expect(formatCompactTokens(null)).toBe('—');
		expect(formatCompactTokens(undefined)).toBe('—');
		expect(formatCompactTokens('n/a')).toBe('—');
	});
});

describe('formatPercent', () => {
	it('renders shares as percentages with a <1% floor', () => {
		expect(formatPercent(0.5)).toBe('50%');
		expect(formatPercent(1)).toBe('100%');
		expect(formatPercent(0)).toBe('0%');
		expect(formatPercent(0.004)).toBe('<1%');
		expect(formatPercent(0.016)).toBe('2%');
	});

	it('returns a dash for missing shares', () => {
		expect(formatPercent(null)).toBe('—');
		expect(formatPercent(undefined)).toBe('—');
		expect(formatPercent(-1)).toBe('—');
	});
});

describe('formatDuration', () => {
	it('picks a readable unit for each magnitude', () => {
		expect(formatDuration(900)).toBe('900ms');
		expect(formatDuration(1500)).toBe('1.5s');
		expect(formatDuration(412_000)).toBe('6m52s');
		expect(formatDuration(180_000)).toBe('3m');
		expect(formatDuration(3_600_000)).toBe('1h');
		expect(formatDuration(5_400_000)).toBe('1h30m');
	});

	it('returns a dash when nothing was measured', () => {
		expect(formatDuration(0)).toBe('—');
		expect(formatDuration(null)).toBe('—');
	});
});

describe('local day labels', () => {
	it('formats host date strings without shifting the day', () => {
		expect(formatDayLabel('2026-08-31')).toBe('8月31日');
		expect(formatMonthLabel('2026-08-31')).toBe('8月');
	});

	it('parses to a local midnight instead of a UTC instant', () => {
		const parsed = parseLocalDate('2026-08-31');
		expect(parsed.getFullYear()).toBe(2026);
		expect(parsed.getMonth()).toBe(7);
		expect(parsed.getDate()).toBe(31);
	});

	it('keeps an unparseable value visible and blanks an empty one', () => {
		expect(formatDayLabel('')).toBe('—');
		expect(formatDayLabel(null)).toBe('—');
		expect(formatDayLabel('not-a-date')).toBe('not-a-date');
	});
});
