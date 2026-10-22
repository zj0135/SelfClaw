import { describe, expect, it } from 'vitest';
import {
	buildMonthLabels,
	buildRankingRows,
	buildRingSegments,
	buildTrendAxisLabels,
	buildTrendSeries,
	buildWeeklyColumns,
	chartColor,
	levelForValue,
	buildHeatmapWeeks,
} from '../src/renderers/usageChartSeries.js';

function dayList(count, start = '2026-08-31') {
	const startDate = new Date(`${start}T00:00:00`);
	return Array.from({ length: count }, (_, index) => {
		const date = new Date(startDate.getFullYear(), startDate.getMonth(), startDate.getDate() - (count - 1 - index));
		const key = `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`;
		return { date: key, totalTokens: index * 100, turnCount: 1, toolCallCount: 0 };
	});
}

describe('levelForValue', () => {
	it('returns 0 for empty days and levels 1-4 for anything above', () => {
		expect(levelForValue(0, 100)).toBe(0);
		expect(levelForValue(10, 0)).toBe(0);
		expect(levelForValue(1, 100)).toBe(1);
		expect(levelForValue(30, 100)).toBe(2);
		expect(levelForValue(100, 100)).toBe(4);
	});

	it('never exceeds the top level for values above the peak', () => {
		expect(levelForValue(500, 100)).toBe(4);
	});
});

describe('buildHeatmapWeeks', () => {
	it('pads the grid to 52 weeks, starts on Sunday and ends on the current week', () => {
		const { weeks, today } = buildHeatmapWeeks(dayList(30));
		expect(weeks).toHaveLength(52);
		expect(weeks.flatMap((week) => week.cells)).toHaveLength(364);
		for (const week of weeks) {
			expect(week.cells).toHaveLength(7);
			expect(new Date(`${week.cells[0].date}T00:00:00`).getDay()).toBe(0);
		}

		expect(today).toBe('2026-08-31');
		const lastWeek = weeks[weeks.length - 1];
		expect(lastWeek.cells.some((cell) => cell.date === today)).toBe(true);
	});

	it('keeps provided values and marks days after today as future', () => {
		const { weeks } = buildHeatmapWeeks(dayList(3));
		const cells = weeks.flatMap((week) => week.cells);
		const last = cells.find((cell) => cell.date === '2026-08-31');
		expect(last.totalTokens).toBe(200);
		expect(last.isFuture).toBe(false);
		expect(cells.filter((cell) => cell.isFuture).every((cell) => cell.totalTokens === 0)).toBe(true);
	});
});

describe('buildWeeklyColumns', () => {
	const build = () => buildHeatmapWeeks([
		{ date: '2026-08-24', totalTokens: 10, turnCount: 1, toolCallCount: 0 },
		{ date: '2026-08-31', totalTokens: 40, turnCount: 1, toolCallCount: 0 },
	]).weeks;

	it('recomputes the daily levels against the daily peak', () => {
		const daily = buildWeeklyColumns(build(), 'daily');
		expect(daily.max).toBe(40);
		const cells = daily.columns.flatMap((column) => column.cells);
		expect(cells.find((cell) => cell.date === '2026-08-31').level).toBe(4);
		expect(cells.find((cell) => cell.date === '2026-08-24').level).toBe(1);
	});

	it('fills weekly columns from the bottom and uses the weekly peak', () => {
		const weekly = buildWeeklyColumns(build(), 'weekly');
		expect(weekly.max).toBe(40);
		const full = weekly.columns.find((column) => column.startDate === '2026-08-30');
		expect(full.filled).toBe(7);
		expect(full.cells.every((cell) => cell.level === 4)).toBe(true);

		const partial = weekly.columns.find((column) => column.startDate === '2026-08-23');
		expect(partial.filled).toBe(2);
		expect(partial.cells.slice(-2).every((cell) => cell.level > 0)).toBe(true);
		expect(partial.cells.slice(0, -2).every((cell) => cell.level === 0)).toBe(true);
	});

	it('prefix-sums in cumulative mode so levels are monotonic', () => {
		const cumulative = buildWeeklyColumns(build(), 'cumulative');
		const values = cumulative.columns.map((column) => column.value);
		expect(values[values.length - 1]).toBe(50);
		expect(values.every((value, index) => index === 0 || value >= values[index - 1])).toBe(true);
		expect(cumulative.columns.filter((column) => column.value > 0).every((column) => column.level > 0)).toBe(true);
	});
});

describe('buildMonthLabels', () => {
	it('anchors labels on columns that contain the first of a month and keeps spans equal to the column count', () => {
		const { weeks } = buildHeatmapWeeks(dayList(364));
		const { columns } = buildWeeklyColumns(weeks, 'daily');
		const labels = buildMonthLabels(columns);
		expect(labels.reduce((sum, group) => sum + group.span, 0)).toBe(columns.length);
		expect(labels.filter((group) => group.label !== '')).toHaveLength(12);
		expect(labels[labels.length - 1].label).not.toBe('');
	});
});

describe('buildTrendSeries', () => {
	const daily = [
		{ date: '2026-08-30', models: [{ model: 'a', totalTokens: 10 }, { model: 'b', totalTokens: 30 }] },
		{ date: '2026-08-31', models: [{ model: 'a', totalTokens: 50 }] },
	];
	const models = [
		{ model: 'b', totalTokens: 900, share: 0.9 },
		{ model: 'a', totalTokens: 60, share: 0.1 },
	];

	it('keeps the top six series and takes the peak from the drawn series only', () => {
		const series = buildTrendSeries(daily, models);
		expect(series.labels).toEqual(['2026-08-30', '2026-08-31']);
		expect(series.series.map((item) => item.model)).toEqual(['b', 'a']);
		expect(series.series[0].values).toEqual([30, 0]);
		expect(series.series[1].values).toEqual([10, 50]);
		// b 的总量是 900，但单点峰值只有 50 —— 上限必须按序列峰值，不按模型总量。
		expect(series.maxTokens).toBe(50);
	});

	it('caps the drawn series at six models', () => {
		const many = Array.from({ length: 9 }, (_, index) => ({ model: `m${index}`, totalTokens: 100 - index, share: 0.1 }));
		expect(buildTrendSeries(daily, many).series).toHaveLength(6);
	});
});

describe('buildTrendAxisLabels', () => {
	it('shows every day for a seven day window', () => {
		const labels = ['1', '2', '3', '4', '5', '6', '7'];
		expect(buildTrendAxisLabels(labels).map((entry) => entry.index)).toEqual([0, 1, 2, 3, 4, 5, 6]);
	});

	it('thins harder when the plot is narrow so date labels cannot overlap', () => {
		const labels = Array.from({ length: 30 }, (_, index) => `d${index}`);
		const wide = buildTrendAxisLabels(labels, 900).map((entry) => entry.index);
		const narrow = buildTrendAxisLabels(labels, 260).map((entry) => entry.index);

		expect(narrow.length).toBeLessThan(wide.length);
		expect(narrow[0]).toBe(0);
		expect(narrow[narrow.length - 1]).toBe(29);
	});

	it('thins thirty days and always keeps the last day', () => {
		const labels = Array.from({ length: 30 }, (_, index) => `d${index}`);
		const indexes = buildTrendAxisLabels(labels).map((entry) => entry.index);
		expect(indexes[0]).toBe(0);
		expect(indexes[indexes.length - 1]).toBe(29);
		expect(indexes.length).toBeLessThanOrEqual(7);
		expect(new Set(indexes).size).toBe(indexes.length);
	});
});

describe('buildRingSegments', () => {
	const models = [
		{ model: 'a', totalTokens: 600, share: 0.6 },
		{ model: 'b', totalTokens: 300, share: 0.3 },
		{ model: 'c', totalTokens: 100, share: 0.1 },
	];

	it('accumulates dash lengths into the whole circumference', () => {
		const ring = buildRingSegments(models);
		const lengths = ring.segments.map((segment) => segment.share * ring.circumference);
		expect(lengths.reduce((sum, value) => sum + value, 0)).toBeCloseTo(ring.circumference, 6);
		const offsets = ring.segments.map((segment) => segment.dashOffset);
		expect(offsets[0]).toBe(0);
		expect(offsets[1]).toBeCloseTo(-lengths[0], 2);
		expect(offsets[2]).toBeCloseTo(-(lengths[0] + lengths[1]), 2);
	});

	it('draws an unbroken circle when only one model has usage', () => {
		const ring = buildRingSegments([{ model: 'only', totalTokens: 10, share: 1 }]);
		expect(ring.segments).toHaveLength(1);
		// 单模型时不留 2° 的缝：100% 的环缺一个口子看起来像数据缺失。
		expect(Number(ring.segments[0].dashArray.split(' ')[0])).toBeCloseTo(ring.circumference, 2);
	});

	it('merges everything past the fifth model into 其他模型', () => {
		const many = Array.from({ length: 8 }, (_, index) => ({ model: `m${index}`, totalTokens: 100, share: 0.125 }));
		const ring = buildRingSegments(many);
		expect(ring.segments).toHaveLength(6);
		expect(ring.segments[5].model).toBe('其他模型');
		expect(ring.segments[5].totalTokens).toBe(300);
		expect(ring.segments[5].color).toBe('var(--sc-chart-other)');
	});
});

describe('buildRankingRows', () => {
	it('keeps the sixth ranking colour when only six models exist', () => {
		const rows = buildRankingRows(
			Array.from({ length: 6 }, (_, index) => ({ model: `m${index}`, totalTokens: 100 - index, share: 0.1 })),
		);

		expect(rows[5].color).toBe('var(--sc-chart-6)');
	});

	it('merges the list the same way the ring does so one colour never covers several models', () => {
		const many = Array.from({ length: 8 }, (_, index) => ({ model: `m${index}`, totalTokens: 100, share: 0.125 }));
		const rows = buildRankingRows(many);

		// 环形把第 6 名以后压成「其他模型」；列表若仍逐行展开，会有 3 行同色却各自成条。
		expect(rows).toHaveLength(6);
		expect(rows[5].label).toBe('其他模型');
		expect(rows[5].color).toBe('var(--sc-chart-other)');
		expect(rows[5].totalTokens).toBe(300);
		expect(rows.slice(0, 5).every((row) => row.color !== 'var(--sc-chart-other)')).toBe(true);
	});

	it('labels a blank model id instead of showing an empty row', () => {
		const rows = buildRankingRows([{ model: '', totalTokens: 10, share: 1 }]);
		expect(rows[0].model).toBe('');
		expect(rows[0].label).toBe('未知模型');
	});

	it('skips zero-share models, numbers the rows and keeps the ring colour order', () => {
		const rows = buildRankingRows([
			{ model: 'a', totalTokens: 500, share: 0.5 },
			{ model: 'zero', totalTokens: 0, share: 0 },
			{ model: 'b', totalTokens: 500, share: 0.5 },
		]);

		expect(rows.map((row) => row.model)).toEqual(['a', 'b']);
		expect(rows.map((row) => row.rank)).toEqual(['01', '02']);
		expect(rows[0].color).toBe('var(--sc-chart-1)');
		expect(rows[1].color).toBe('var(--sc-chart-2)');
		expect(rows[0].barWidth).toBe('50%');
	});
});

describe('chartColor', () => {
	it('wraps to the neutral colour past the sixth series', () => {
		expect(chartColor(0)).toBe('var(--sc-chart-1)');
		expect(chartColor(5)).toBe('var(--sc-chart-6)');
		expect(chartColor(6)).toBe('var(--sc-chart-other)');
	});
});
