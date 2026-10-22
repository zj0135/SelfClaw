// 用量数字的纯格式化：k/M 缩写是全局唯一一份（usageMeter.js 从这里 re-export），
// 仪表盘另加一套「未知值不冒充 0」的入口 —— 0 token 与「未上报」在界面上是两件事。

export const unknownText = '—';
const defaultLocale = 'zh-CN';

export function formatTokenCount(value) {
	if (typeof value === 'string' && /^(?:\d+(?:\.\d+)?)(?:K|M)$/.test(value)) return value;
	const number = Number(value);
	if (!Number.isFinite(number) || number <= 0) return unknownText;
	if (number >= 1_000_000) return `${trimNumber(number / 1_000_000)}M`;
	if (number >= 1_000) {
		const thousands = trimNumber(number / 1_000);
		// 四舍五入后量级可能进位：999999 会变成 "1000"，此时必须显示 1M 而不是 1000K。
		return Number(thousands) >= 1_000 ? `${trimNumber(number / 1_000_000)}M` : `${thousands}K`;
	}
	return String(number);
}

// 仪表盘用：0 是有效值，只有缺字段/非数字才是未知。
export function formatCompactTokens(value) {
	if (value === null || value === undefined || value === '') return unknownText;
	const number = Number(value);
	if (!Number.isFinite(number)) return unknownText;
	if (number <= 0) return '0';
	return formatTokenCount(number);
}

// 内核把 NULL model 读成空串：显示文案属于展示层，不写进数据层。
// 趋势图按 model 原始值匹配，只在 legend / tooltip / 排行里用 label。
export function resolveModelLabel(model, fallback = '未知模型') {
	const text = typeof model === 'string' ? model.trim() : '';
	return text || fallback;
}

// share 是 0–1 的小数。非零但不足 1% 时显示 "<1%"，否则用户会以为这条模型没有用量。
// 次数类计数：0 是有效值，只有缺字段才是未知。
export function formatCount(value) {
	if (value === null || value === undefined || value === '') return unknownText;
	const number = Number(value);
	if (!Number.isFinite(number)) return unknownText;
	return trimNumber(Math.max(0, Math.round(number)), 0);
}
export function formatPercent(share, fractionDigits = 0) {
	if (share === null || share === undefined || share === '') return unknownText;
	const number = Number(share);
	if (!Number.isFinite(number) || number < 0) return unknownText;
	const percent = number * 100;
	if (percent > 0 && percent < 1) return '<1%';
	return `${trimNumber(percent, fractionDigits)}%`;
}

export function formatDuration(value) {
	const milliseconds = Number(value);
	if (!Number.isFinite(milliseconds) || milliseconds <= 0) return unknownText;
	if (milliseconds < 1_000) return `${Math.round(milliseconds)}ms`;
	const seconds = milliseconds / 1_000;
	if (seconds < 60) return `${trimNumber(seconds, 1)}s`;
	const minutes = Math.floor(seconds / 60);
	if (minutes < 60) return `${minutes}m${trimTimeRemainder(Math.round(seconds % 60), 's')}`;
	const hours = Math.floor(minutes / 60);
	return `${hours}h${trimTimeRemainder(minutes % 60, 'm')}`;
}

// 日期一律是宿主的本地自然日字符串（yyyy-MM-dd），因此只做展示格式化，不做时区换算。
export function formatDayLabel(date, locale = defaultLocale) {
	const parsed = parseLocalDate(date);
	if (!parsed) return typeof date === 'string' && date ? date : unknownText;
	return new Intl.DateTimeFormat(locale, { month: 'short', day: 'numeric' }).format(parsed);
}

export function formatMonthLabel(date, locale = defaultLocale) {
	const parsed = parseLocalDate(date);
	if (!parsed) return typeof date === 'string' && date ? date : unknownText;
	return new Intl.DateTimeFormat(locale, { month: 'short' }).format(parsed);
}

// 两位以内小数、去千分位。用量相关的缩写与百分比共用同一套小数处理。
export function trimNumber(value, maximumFractionDigits = 2) {
	return Number(value).toLocaleString('en-US', {
		useGrouping: false,
		maximumFractionDigits,
	});
}

// 解析成「本地零点」的 Date：直接 new Date('2026-08-31') 会按 UTC 解释，西半球会整体前一天。
export function parseLocalDate(value) {
	if (typeof value !== 'string') return null;
	const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(value);
	if (!match) return null;
	const date = new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3]));
	return Number.isNaN(date.getTime()) ? null : date;
}

function trimTimeRemainder(value, suffix) {
	return value > 0 ? `${value}${suffix}` : '';
}
