import { toolStatusLabel } from './shared';

export { toolStatusLabel };

// ── segment 归一化 ────────────────────────────────────────────────
export function getMessageSegments(item) {
	return Array.isArray(item.segments) ? item.segments : [];
}

// ── 稳定 id 派生 ──────────────────────────────────────────────────
// thinking 用出现序 ordinal（markdown 只追加、切分确定，序号稳定）；
// tool 单卡优先用后端稳定的 segmentId（= toolRun.Id）；
// tool group 由首尾成员的 segmentId 派生，消除旧的 index-range 漂移。
export const thinkingBlockId = (messageId, ordinal) => `${messageId}:thinking:${ordinal}`;
export const toolSegmentId = (messageId, segment, index) => segment.segmentId || `${messageId}:tool:${index}`;
export const toolGroupId = (messageId, members) => {
	const first = members[0]?.id ?? 'x';
	const last = members[members.length - 1]?.id ?? 'x';
	return `${messageId}:tool-group:${first}:${last}`;
};

// ── 工具类型/摘要的领域逻辑 ───────────────────────────────────────
const toolActionDescriptors = {
	run: { verb: 'ran', singular: 'command', plural: 'commands' },
	edit: { verb: 'edited', singular: 'file', plural: 'files' },
	read: { verb: 'read', singular: 'file', plural: 'files' },
	search: { verb: 'searched', singular: 'query', plural: 'queries' },
	list: { verb: 'listed', singular: 'directory', plural: 'directories' },
	export: { verb: 'exported', singular: 'document', plural: 'documents' },
	tool: { verb: 'used', singular: 'tool', plural: 'tools' },
};

function capitalize(value) {
	if (!value) {
		return '';
	}

	return value.charAt(0).toUpperCase() + value.slice(1);
}

function resolveToolName(segment) {
	const explicitToolName = String(segment.toolName || '')
		.trim()
		.toLowerCase();
	if (explicitToolName) {
		return explicitToolName;
	}

	const detailTitle = String(segment.detailTitle || '')
		.trim()
		.toLowerCase();
	switch (detailTitle) {
		case 'shell':
			return 'run_shell_command';
		case 'write file':
			return 'write_file';
		case 'edit file':
			return 'edit_file';
		case 'read file':
			return 'read_file';
		case 'search results':
			return 'search_text';
		case 'workspace entries':
			return 'list_files';
		default:
			break;
	}

	const summaryText = String(segment.text || '')
		.trim()
		.toLowerCase();
	if (summaryText.startsWith('run ')) {
		return 'run_shell_command';
	}

	if (summaryText.startsWith('write ') || summaryText.startsWith('create ')) {
		return 'write_file';
	}

	if (summaryText.startsWith('read ')) {
		return 'read_file';
	}

	if (summaryText.startsWith('search ')) {
		return 'search_text';
	}

	if (summaryText.startsWith('list ')) {
		return 'list_files';
	}

	return '';
}

function resolveToolAction(segment) {
	const toolName = resolveToolName(segment);
	switch (toolName) {
		case 'run_shell_command':
			return 'run';
		case 'write_file':
		case 'edit_file':
			return 'edit';
		case 'read_file':
			return 'read';
		case 'search_text':
			return 'search';
		case 'list_files':
		case 'glob_files':
			return 'list';
		default:
			return 'tool';
	}
}

export function buildToolActionSummary(segments) {
	const groups = new Map();
	for (const segment of segments) {
		const action = resolveToolAction(segment);
		const existing = groups.get(action) || { action, count: 0 };
		existing.count += 1;
		groups.set(action, existing);
	}

	const fragments = Array.from(groups.values()).map((group, index) => {
		const descriptor = toolActionDescriptors[group.action] || toolActionDescriptors.tool;
		const verb = index === 0 ? capitalize(descriptor.verb) : descriptor.verb;
		const noun = group.count === 1 ? descriptor.singular : descriptor.plural;
		return `${verb} ${group.count} ${noun}`;
	});

	return fragments.join(', ');
}

export function resolveToolGroupStatus(segments) {
	if (segments.some((segment) => segment.status === 'awaitingapproval')) {
		return 'awaitingapproval';
	}

	if (segments.some((segment) => segment.status === 'running')) {
		return 'running';
	}

	if (segments.some((segment) => segment.status === 'failed')) {
		return 'failed';
	}

	if (segments.some((segment) => segment.status === 'cancelled')) {
		return 'cancelled';
	}

	return 'completed';
}

// 「动词 + 目标」两段式标签：首个空格前作为主标签（深色），其余作为副标签（浅色）。
// 无空格（如中文短语）时整体作为主标签。
export function splitSummaryLabel(text) {
	const value = String(text || '').trim();
	const spaceIndex = value.indexOf(' ');
	if (spaceIndex <= 0) {
		return { primary: value, secondary: '' };
	}

	return { primary: value.slice(0, spaceIndex), secondary: value.slice(spaceIndex + 1) };
}

export function formatAttachmentSize(byteLength) {
	const size = Number(byteLength || 0);
	if (size >= 1024 * 1024) {
		return `${(size / (1024 * 1024)).toFixed(size >= 10 * 1024 * 1024 ? 0 : 1)} MB`;
	}

	if (size >= 1024) {
		return `${Math.max(1, Math.round(size / 1024))} KB`;
	}

	return `${Math.max(0, size)} B`;
}

// ── 编排：raw segments → 有序渲染块 ──────────────────────────────
// 等价于旧 renderMessageContent 的 for 循环，但产出数据而非 HTML 串：
// 连续的 tool 段贪心合并成组（≥2 张才成组，否则单卡），thinking 按出现序编号。
export function buildRenderBlocks(item) {
	const segments = getMessageSegments(item);
	const total = segments.length;
	const blocks = [];
	let thinkingOrdinal = 0;

	for (let index = 0; index < total; index += 1) {
		const segment = segments[index];
		const isFirst = index === 0;

		if (segment.kind === 'thinking') {
			blocks.push({
				type: 'thinking',
				key: segment.segmentId || thinkingBlockId(item.id, thinkingOrdinal),
				id: segment.segmentId || thinkingBlockId(item.id, thinkingOrdinal),
				segment,
				isLast: index === total - 1,
			});
			thinkingOrdinal += 1;
			continue;
		}

		if (segment.kind === 'tool') {
			let endIndex = index;
			while (endIndex + 1 < total && segments[endIndex + 1].kind === 'tool') {
				endIndex += 1;
			}

			const toolSegments = segments.slice(index, endIndex + 1);
			const isLast = endIndex === total - 1;
			if (toolSegments.length > 1) {
				const members = toolSegments.map((toolSegment, offset) => ({
					id: toolSegmentId(item.id, toolSegment, index + offset),
					segment: toolSegment,
				}));
				blocks.push({
					type: 'tool-group',
					key: toolGroupId(item.id, members),
					id: toolGroupId(item.id, members),
					members,
					status: resolveToolGroupStatus(toolSegments),
					summaryLabel: buildToolActionSummary(toolSegments),
					isFirst,
					isLast,
				});
			} else {
				blocks.push({
					type: 'tool',
					key: toolSegmentId(item.id, segment, index),
					id: toolSegmentId(item.id, segment, index),
					segment,
					// 单条工具卡片优先展示具体目标，比聚合计数更有信息量。
					summaryLabel: segment.text || buildToolActionSummary([segment]),
					isFirst,
					isLast,
				});
			}

			index = endIndex;
			continue;
		}

		blocks.push({
			type: 'body',
			key: `${item.id}:body:${index}`,
			segment,
			isFirst,
			isLast: index === total - 1,
		});
	}

	return blocks;
}
