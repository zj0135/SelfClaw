// 用量仪表盘的全部算术：热力图补格与等级、周聚合、月份标签、折线几何、环形 dash。
// 组件里只渲染这些结果，不自己算 —— 三种热力图模式的等级必须各自重算，
// 因为「每日峰值」和「每周峰值」不是一个量纲，后端也不提供 level。

import { formatMonthLabel, parseLocalDate, resolveModelLabel } from '../../../renderers/usageFormat.js';

export const HEATMAP_WEEKS = 52;
export const HEATMAP_DAYS_PER_WEEK = 7;
export const MAX_HEAT_LEVEL = 4;
export const MONTH_LABEL_LIMIT = 12;
export const CHART_COLOR_COUNT = 6;
export const TREND_MODEL_LIMIT = 6;
export const RING_SEGMENT_LIMIT = 6;
export const RING_RADIUS = 40;

export const HEATMAP_MODES = [
	{ id: 'daily', label: '每日' },
	{ id: 'weekly', label: '每周' },
	{ id: 'cumulative', label: '累计' },
];

// 固定色序：下标只决定色相序号，具体颜色由 --sc-chart-n 决定（组件里不出现颜色字面量）。
export function chartColor(index) {
	return index >= 0 && index < CHART_COLOR_COUNT ? `var(--sc-chart-${index + 1})` : 'var(--sc-chart-other)';
}

export function levelForValue(value, max) {
	if (!Number.isFinite(value) || !Number.isFinite(max) || value <= 0 || max <= 0) return 0;
	return Math.min(MAX_HEAT_LEVEL, Math.max(1, Math.ceil((value / max) * MAX_HEAT_LEVEL)));
}

// 补格 + 周日对齐：网格列数固定，第一行永远是周日，最后一列的周六是「本周」的周六，
// 因此今天之后的格子标成 isFuture（不画底色，也不参与 hover）。
export function buildHeatmapWeeks(days, weekCount = HEATMAP_WEEKS) {
	const byDate = new Map();
	for (const day of Array.isArray(days) ? days : []) {
		if (day?.date) byDate.set(day.date, day);
	}

	const provided = [...byDate.keys()].sort();
	const lastDate = provided.length ? provided[provided.length - 1] : toDateString(new Date());
	const today = parseLocalDate(lastDate) ?? new Date();
	const weekStart = addDays(startOfWeek(today), -(weekCount - 1) * HEATMAP_DAYS_PER_WEEK);

	const weeks = [];
	for (let weekIndex = 0; weekIndex < weekCount; weekIndex += 1) {
		const cells = [];
		for (let dayIndex = 0; dayIndex < HEATMAP_DAYS_PER_WEEK; dayIndex += 1) {
			const date = addDays(weekStart, weekIndex * HEATMAP_DAYS_PER_WEEK + dayIndex);
			const key = toDateString(date);
			const source = byDate.get(key);
			cells.push({
				date: key,
				totalTokens: numberOrZero(source?.totalTokens),
				turnCount: numberOrZero(source?.turnCount),
				toolCallCount: numberOrZero(source?.toolCallCount),
				isFuture: date.getTime() > today.getTime(),
			});
		}

		weeks.push({
			key: cells[0].date,
			startDate: cells[0].date,
			endDate: cells[HEATMAP_DAYS_PER_WEEK - 1].date,
			cells,
		});
	}

	return { weeks, today: toDateString(today) };
}

// 每周 / 累计不是换数据源：把一列汇总成单值后从底部向上填充 7 行，等级按该模式自己的峰值算。
export function buildWeeklyColumns(weeks, mode = 'daily', metric = 'totalTokens') {
	const columns = (Array.isArray(weeks) ? weeks : []).map((week) => ({
		key: week.key,
		startDate: week.startDate,
		endDate: week.endDate,
		cells: week.cells,
		value: week.cells.reduce((sum, cell) => sum + numberOrZero(cell[metric]), 0),
	}));

	if (mode === 'weekly') {
		const max = columns.reduce((peak, column) => Math.max(peak, column.value), 0);
		return { mode, max, columns: fillColumns(columns, max) };
	}

	if (mode === 'cumulative') {
		let running = 0;
		const cumulative = columns.map((column) => {
			running += column.value;
			return { ...column, value: running };
		});
		const max = cumulative.reduce((peak, column) => Math.max(peak, column.value), 0);
		return { mode, max, columns: fillColumns(cumulative, max) };
	}

	const max = columns.reduce(
		(peak, column) => column.cells.reduce((inner, cell) => Math.max(inner, numberOrZero(cell[metric])), peak),
		0,
	);
	return {
		mode: 'daily',
		max,
		columns: columns.map((column) => ({
			...column,
			level: levelForValue(column.value, max),
			cells: column.cells.map((cell) => ({
				...cell,
				level: cell.isFuture ? 0 : levelForValue(numberOrZero(cell[metric]), max),
			})),
		})),
	};
}

// 月份文案锚定「包含每月 1 日」的那一列；最多显示最近 12 个月，更早的月份保留占位但不写字，
// 于是 span 总和恒等于列数，表头和网格不会错位。
export function buildMonthLabels(columns, locale = 'zh-CN') {
	const groups = [];
	for (const column of Array.isArray(columns) ? columns : []) {
		const anchor = column.cells.find((cell) => cell.date.slice(8, 10) === '01')?.date ?? column.startDate;
		const key = anchor.slice(0, 7);
		const previous = groups[groups.length - 1];
		if (previous && previous.key === key) previous.span += 1;
		else groups.push({ key, anchor, span: 1 });
	}

	return groups.map((group, index) => ({
		...group,
		label: index < groups.length - MONTH_LABEL_LIMIT ? '' : formatMonthLabel(group.anchor, locale),
	}));
}

export function buildLayout(width, height) {
	const safeWidth = Math.max(0, Number(width) || 0);
	const safeHeight = Math.max(0, Number(height) || 0);
	return {
		width: safeWidth,
		height: safeHeight,
		left: 10,
		top: 10,
		plotWidth: Math.max(0, safeWidth - 20),
		plotHeight: Math.max(0, safeHeight - 34),
	};
}

// Y 轴上限只取已展示序列的单点峰值：图中是独立折线而不是堆叠，
// 用「当日所有模型总量」当上限会把每条曲线压扁。
export function buildTrendSeries(daily, models, maxModels = TREND_MODEL_LIMIT) {
	const points = Array.isArray(daily) ? daily : [];
	const ranked = (Array.isArray(models) ? models : [])
		.filter((model) => numberOrZero(model?.totalTokens) > 0)
		.slice(0, maxModels);
	const countsByPoint = points.map((point) => {
		const counts = new Map();
		for (const entry of Array.isArray(point?.models) ? point.models : []) {
			counts.set(entry.model, numberOrZero(entry.totalTokens));
		}

		return counts;
	});

	const series = ranked.map((model, index) => ({
		model: model.model,
		label: resolveModelLabel(model.model),
		colorIndex: index,
		color: chartColor(index),
		totalTokens: numberOrZero(model.totalTokens),
		values: countsByPoint.map((counts) => counts.get(model.model) ?? 0),
	}));

	return {
		labels: points.map((point) => point.date),
		series,
		maxTokens: series.reduce(
			(peak, item) => item.values.reduce((inner, value) => Math.max(inner, value), peak),
			0,
		),
	};
}

export function buildSeriesPoints(values, layout, maxTokens) {
	const list = Array.isArray(values) ? values : [];
	const ceiling = Math.max(1, Number(maxTokens) || 0);
	return list.map((value, index) => ({
		index,
		value: numberOrZero(value),
		x: list.length > 1 ? layout.left + (index / (list.length - 1)) * layout.plotWidth : layout.left + layout.plotWidth / 2,
		y: layout.top + layout.plotHeight - (numberOrZero(value) / ceiling) * layout.plotHeight,
	}));
}

// Catmull-Rom 转三次贝塞尔：比折线顺，又不需要引入图表库的曲线拟合。
export function buildSmoothPath(points) {
	if (!Array.isArray(points) || points.length === 0) return '';
	if (points.length === 1) return `M ${round(points[0].x)} ${round(points[0].y)}`;

	let path = `M ${round(points[0].x)} ${round(points[0].y)}`;
	for (let index = 0; index < points.length - 1; index += 1) {
		const previous = points[index - 1] ?? points[index];
		const current = points[index];
		const next = points[index + 1];
		const following = points[index + 2] ?? next;
		const control1X = current.x + (next.x - previous.x) / 6;
		const control1Y = current.y + (next.y - previous.y) / 6;
		const control2X = next.x - (following.x - current.x) / 6;
		const control2Y = next.y - (following.y - current.y) / 6;
		path += ` C ${round(control1X)} ${round(control1Y)}, ${round(control2X)} ${round(control2Y)}, ${round(next.x)} ${round(next.y)}`;
	}

	return path;
}

export function xForIndex(index, count, layout) {
	if (count <= 1) return layout.left + layout.plotWidth / 2;
	return layout.left + (index / (count - 1)) * layout.plotWidth;
}

// x 是相对绘图区左边缘的偏移（覆盖层 rect 的坐标系），不是整张图的绝对坐标。
export function indexFromPlotX(x, count, layout) {
	if (count <= 1) return 0;
	const ratio = Number(x) / (layout.plotWidth || 1);
	return Math.min(count - 1, Math.max(0, Math.round(ratio * (count - 1))));
}

// 日期文案约 56px（含左右留白）。给出绘图区宽度时按宽度定步长，窄容器里不会叠字；
// 不给宽度时退回「7 天全显、更长按 6 段」的旧口径。
const TREND_AXIS_LABEL_WIDTH = 56;

export function buildTrendAxisLabels(labels, plotWidth = 0) {
	const count = Array.isArray(labels) ? labels.length : 0;
	if (count === 0) return [];
	const width = Number(plotWidth) || 0;
	const maxLabels = width > 0 ? Math.max(2, Math.floor(width / TREND_AXIS_LABEL_WIDTH)) : 0;
	const step = maxLabels > 0
		? (count <= maxLabels ? 1 : Math.ceil(count / maxLabels))
		: (count <= 7 ? 1 : Math.ceil(count / 6));
	const indexes = [];
	for (let index = 0; index < count; index += step) indexes.push(index);
	if (indexes[indexes.length - 1] !== count - 1) indexes.push(count - 1);
	return indexes.map((index) => ({ index, label: labels[index] }));
}

export const OTHER_MODEL_LABEL = '其他模型';

function mergeModels(models) {
	return {
		model: OTHER_MODEL_LABEL,
		totalTokens: models.reduce((sum, model) => sum + numberOrZero(model.totalTokens), 0),
		share: models.reduce((sum, model) => sum + numberOrZero(model.share), 0),
	};
}

// 环形与排行共用同一份合并结果。若环形把第 6 名以后压成「其他模型」而列表仍逐行展开，
// 连续几行同色的模型会让用户以为它们是同一条数据。
function mergedEntries(models, limit) {
	const positive = (Array.isArray(models) ? models : []).filter((model) => numberOrZero(model?.totalTokens) > 0);
	const head = Math.max(1, limit);
	if (positive.length <= head) {
		return positive.map((model, index) => ({ ...model, colorIndex: index }));
	}

	return [
		...positive.slice(0, head - 1).map((model, index) => ({ ...model, colorIndex: index })),
		{ ...mergeModels(positive.slice(head - 1)), colorIndex: CHART_COLOR_COUNT },
	];
}

// 环形图：每段 dasharray 占 share*C，dashoffset 是累计占比的反向位移，两段之间扣掉 2° 的间隙。
// 只有一个模型时不留缝，否则 100% 的环会缺一个口子。
export function buildRingSegments(models, limit = RING_SEGMENT_LIMIT, radius = RING_RADIUS) {
	const entries = mergedEntries(models, limit);
	const circumference = 2 * Math.PI * radius;
	const gap = entries.length > 1 ? (circumference * 2) / 360 : 0;
	let offset = 0;
	const segments = entries.map((entry) => {
		const share = numberOrZero(entry.share);
		const segment = {
			model: entry.model,
			label: resolveModelLabel(entry.model),
			colorIndex: entry.colorIndex,
			color: chartColor(entry.colorIndex),
			totalTokens: numberOrZero(entry.totalTokens),
			share,
			dashArray: `${round(Math.max(0, share * circumference - gap))} ${round(circumference)}`,
			dashOffset: round(-offset) || 0,
		};
		offset += share * circumference;
		return segment;
	});

	return {
		radius,
		circumference,
		totalTokens: entries.reduce((sum, entry) => sum + numberOrZero(entry.totalTokens), 0),
		segments,
	};
}

// 排行列表与环形图共用同一套合并结果、色序与序号，两处不会打架。
export function buildRankingRows(models, limit = 10) {
	return mergedEntries(models, RING_SEGMENT_LIMIT)
		.slice(0, Math.max(1, limit))
		.map((entry, index) => {
			const share = numberOrZero(entry.share);
			return {
				rank: String(index + 1).padStart(2, '0'),
				model: entry.model,
				label: resolveModelLabel(entry.model),
				totalTokens: numberOrZero(entry.totalTokens),
				share,
				colorIndex: entry.colorIndex,
				color: chartColor(entry.colorIndex),
				barWidth: `${round(Math.min(100, share * 100))}%`,
			};
		});
}

function fillColumns(columns, max) {
	return columns.map((column) => {
		const filled = max > 0
			? Math.min(HEATMAP_DAYS_PER_WEEK, Math.ceil((column.value / max) * HEATMAP_DAYS_PER_WEEK))
			: 0;
		const level = levelForValue(column.value, max);
		const offset = column.cells.length - filled;
		return {
			...column,
			level,
			filled,
			// 整列填充是按周汇总的柱，不排除「本周还没到的那几天」——那一列代表的是这一周的总量。
			cells: column.cells.map((cell, index) => ({
				...cell,
				level: index < offset ? 0 : level,
			})),
		};
	});
}

function startOfWeek(date) {
	const start = new Date(date.getFullYear(), date.getMonth(), date.getDate());
	return addDays(start, -start.getDay());
}

function addDays(date, days) {
	return new Date(date.getFullYear(), date.getMonth(), date.getDate() + days);
}

function toDateString(date) {
	const year = String(date.getFullYear()).padStart(4, '0');
	const month = String(date.getMonth() + 1).padStart(2, '0');
	const day = String(date.getDate()).padStart(2, '0');
	return `${year}-${month}-${day}`;
}

function numberOrZero(value) {
	const number = Number(value);
	return Number.isFinite(number) ? number : 0;
}

function round(value) {
	return Math.round(Number(value) * 100) / 100;
}
