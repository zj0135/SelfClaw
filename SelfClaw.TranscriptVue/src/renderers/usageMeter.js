// 用量相关的纯格式化：k/M 缩写是全局唯一一份，设置页与输入框状态栏都从这里取。
import { formatTokenCount, trimNumber, unknownText } from './usageFormat.js';

export { formatTokenCount };

const unknownTokenCount = unknownText;
const unavailableContextText = '不可用';
const estimatedContextTitle = '由本地计数近似';

export function formatContextUsage(usage) {
	if (!usage) return null;
	const estimated = usage.contextIsEstimated === true;
	const contextTokens = formatTokenCount(usage.contextTokens);
	const known = contextTokens !== unknownTokenCount;
	return {
		contextText: known ? formatContextSize(contextTokens, formatTokenCount(usage.contextWindowTokens), estimated) : unavailableContextText,
		contextTitle: known && estimated ? estimatedContextTitle : '',
		totals: formatUsageTotals(usage),
	};
}

export function calculateUsageProgress(usage, fallbackAbsoluteThreshold = 64_000) {
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

	const tone = ratio >= 0.85 ? 'critical' : ratio >= 0.65 ? 'warm' : 'normal';

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
	};
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

// 近似标记只加在上下文尺寸上：窗口容量是模型声明值，不是计数得来的。
function formatContextSize(tokens, windowTokens, estimated) {
	const size = windowTokens === unknownTokenCount ? tokens : `${tokens} / ${windowTokens}`;
	return estimated ? `~${size}` : size;
}

// 累计为 0（尚无回合）时整段省略，避免状态栏常驻一排破折号。
function formatUsageTotals(usage) {
	return [['输入', usage.inputTokens], ['输出', usage.outputTokens]]
		.map(([label, value]) => ({ label, value: formatTokenCount(value) }))
		.filter((part) => part.value !== unknownTokenCount);
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
