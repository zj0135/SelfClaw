// 用量相关的纯格式化：k/M 缩写是全局唯一一份，设置页与输入框状态栏都从这里取。
import { formatTokenCount, trimNumber, unknownText } from './usageFormat.js';

export { formatTokenCount };

const unknownTokenCount = unknownText;
const unavailableContextText = '不可用';

// 色调分界：知道压缩触发点时按「离触发还有多远」上色，否则退回按窗口饱和度上色。
const triggerToneStops = { warm: 0.85, critical: 1 };
const windowToneStops = { warm: 0.65, critical: 0.85 };

// compactionEnabled 来自设置页的实时状态：宿主下发的触发点是上一回合算出来的，
// 用户刚关掉自动压缩时它还在，刻度必须立刻消失而不是等到下一回合。
export function calculateUsageProgress(usage, fallbackAbsoluteThreshold = 64_000, compactionEnabled = true) {
	if (!usage) return null;

	const hasContextTokens = usage.contextTokens != null && Number.isFinite(Number(usage.contextTokens)) && Number(usage.contextTokens) >= 0;
	const contextTokens = hasContextTokens ? Number(usage.contextTokens) : 0;

	const hasWindow = usage.contextWindowTokens != null && Number.isFinite(Number(usage.contextWindowTokens)) && Number(usage.contextWindowTokens) > 0;
	const rawWindowTokens = hasWindow ? Number(usage.contextWindowTokens) : null;

	const fallbackThreshold = Number.isFinite(Number(fallbackAbsoluteThreshold)) && Number(fallbackAbsoluteThreshold) > 0
		? Number(fallbackAbsoluteThreshold)
		: 64_000;

	const limitTokens = hasWindow ? rawWindowTokens : fallbackThreshold;
	const isAbsoluteThreshold = !hasWindow;

	const rawRatio = limitTokens > 0 && hasContextTokens ? (contextTokens / limitTokens) : 0;
	const ratio = Math.min(1, Math.max(0, rawRatio));
	const percentage = Math.round(rawRatio * 100);

	let percentageText = '0%';
	if (hasContextTokens && contextTokens > 0) {
		percentageText = percentage === 0 ? '<1%' : `${percentage}%`;
	}

	// 触发点由宿主按压缩公式算好下发，与 contextTokens 同口径；前端不复制公式。
	const triggerTokens = compactionEnabled ? positiveNumber(usage.compactionTriggerTokens) : null;
	// 没有声明窗口时进度的上限本身就是压缩阈值，再画一个刻度只是重复。
	const triggerRatio = triggerTokens !== null && hasWindow && triggerTokens < limitTokens
		? triggerTokens / limitTokens
		: null;
	const tone = resolveTone(
		triggerTokens !== null && hasContextTokens ? contextTokens / triggerTokens : ratio,
		triggerTokens !== null ? triggerToneStops : windowToneStops,
	);

	const estimated = usage.contextIsEstimated === true;
	let contextText = unavailableContextText;
	if (hasContextTokens) {
		const formatted = contextTokens === 0 ? '0' : formatTokenCount(contextTokens);
		contextText = estimated ? `~${formatted}` : formatted;
	}

	const limitText = formatTokenCount(limitTokens);
	const inputTokensText = formatTokenCount(usage.inputTokens);
	const outputTokensText = formatTokenCount(usage.outputTokens);
	const cacheHitText = formatCacheHitRate(usage.cachedInputTokens, usage.inputTokens);

	const rawTotal = (Number(usage.inputTokens) || 0) + (Number(usage.outputTokens) || 0);
	const totalTokensText = rawTotal > 0 ? formatTokenCount(rawTotal) : '—';

	return {
		hasContextTokens,
		contextTokens,
		limitTokens,
		isAbsoluteThreshold,
		ratio,
		percentage,
		percentageText,
		tone,
		contextText,
		limitText,
		inputTokensText: inputTokensText === unknownTokenCount ? '—' : inputTokensText,
		outputTokensText: outputTokensText === unknownTokenCount ? '—' : outputTokensText,
		totalTokensText,
		cacheHitText,
		contextIsEstimated: estimated,
		triggerTokens,
		triggerRatio,
		triggerText: triggerRatio === null ? null : formatTokenCount(triggerTokens),
		triggerShareText: triggerRatio === null ? null : `${Math.round(triggerRatio * 100)}%`,
	};
}

function positiveNumber(value) {
	if (value == null) return null;
	const number = Number(value);
	return Number.isFinite(number) && number > 0 ? number : null;
}

function resolveTone(fill, stops) {
	if (fill >= stops.critical) return 'critical';
	return fill >= stops.warm ? 'warm' : 'normal';
}

// 圆环上一道径向刻度的两个端点。角度与进度弧同源：0 在弧的起点，顺着弧的方向增长，
// 所以刻度落在「进度弧长到这里就触发」的位置上。
export function ringTick(ratio, { center, inner, outer }) {
	if (ratio == null || !Number.isFinite(ratio)) return null;
	const angle = 2 * Math.PI * Math.min(1, Math.max(0, ratio));
	const point = (radius) => ({
		x: Number((center + radius * Math.cos(angle)).toFixed(3)),
		y: Number((center + radius * Math.sin(angle)).toFixed(3)),
	});
	const from = point(inner);
	const to = point(outer);
	return { x1: from.x, y1: from.y, x2: to.x, y2: to.y };
}

// 缓存命中率 = 缓存命中的输入 token 占全部输入 token 的比例；任一侧未知或为零时不显示，
// 因为 0% 与“未上报”在界面上是两种不同的意思。
function formatCacheHitRate(cachedTokens, inputTokens) {
	if (cachedTokens == null || inputTokens == null) return null;
	const cached = Number(cachedTokens);
	const input = Number(inputTokens);
	if (!Number.isFinite(cached) || !Number.isFinite(input) || cached <= 0 || input <= 0) return null;
	return `${trimNumber(Math.min(100, (cached / input) * 100), 1)}%`;
}

export function formatCompressionNotice(notice) {
	if (!notice) return null;
	const messages = formatCompressedMessages(notice.messageCount);
	const source = knownTokenText(notice.sourceTokens);
	const summary = knownTokenText(notice.summaryTokens);
	const model = typeof notice.model === 'string' ? notice.model.trim() : '';
	return {
		label: '上下文压缩',
		subtitle: formatCompressionSubtitle(messages, source, summary),
		hasSummary: typeof notice.summary === 'string' && notice.summary.trim().length > 0,
		title: model ? `压缩模型：${model}` : '',
	};
}

function formatCompressedMessages(messageCount) {
	const number = Number(messageCount);
	return Number.isFinite(number) && number >= 1
		? `已压缩 ${number.toLocaleString()} 条消息`
		: '已压缩历史消息';
}

function formatCompressionSubtitle(messages, source, summary) {
	if (source && summary) return `${messages} · ${source} 到 ${summary} tokens`;
	if (source) return `${messages} · 压缩前 ${source} tokens`;
	if (summary) return `${messages} · 压缩后 ${summary} tokens`;
	return messages;
}

// 未知用量返回破折号；副标题里破折号没有信息量，改由文案本身表达未知。
function knownTokenText(value) {
	const text = formatTokenCount(value);
	return text === unknownTokenCount ? '' : text;
}
