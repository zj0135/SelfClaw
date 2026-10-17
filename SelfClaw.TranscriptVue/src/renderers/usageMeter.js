// 用量相关的纯格式化：k/M 缩写是全局唯一一份，设置页与输入框状态栏都从这里取。

const unknownTokenCount = '—';
const unavailableContextText = '不可用';
const estimatedContextTitle = '由本地计数近似';

export function formatTokenCount(value) {
	if (typeof value === 'string' && /^(?:\d+(?:\.\d+)?)(?:K|M)$/.test(value)) return value;
	const number = Number(value);
	if (!Number.isFinite(number) || number <= 0) return unknownTokenCount;
	if (number >= 1_000_000) return `${trimNumber(number / 1_000_000)}M`;
	if (number >= 1_000) {
		const thousands = trimNumber(number / 1_000);
		// 四舍五入后量级可能进位：999999 会变成 "1000"，此时必须显示 1M 而不是 1000K。
		return Number(thousands) >= 1_000 ? `${trimNumber(number / 1_000_000)}M` : `${thousands}K`;
	}
	return String(number);
}

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

function trimNumber(value, maximumFractionDigits = 2) {
	return Number(value).toLocaleString('en-US', {
		useGrouping: false,
		maximumFractionDigits,
	});
}
