// 输入框行内 chip 与纯文本之间的转换：触发检测、chip 描述符、token 序列化。
// 这里没有 DOM 副作用（serializeEditorNodes 只读节点），组件与单测共用同一份规则。

const triggerKinds = new Map([
	['@', 'file'],
	['/', 'command'],
	['$', 'skill'],
]);

const whitespacePattern = /\s/;

// 裸路径允许的字符，与 renderers/markdown.js 的 file_mention 规则严格对称：
// 路径含空白或这些字符时序列化成 @"..."，转录按同一规则识别回来。
const barePathPattern = /^[^\s"'<>()[\]{}|,;:!?@]+$/;

/**
 * 从「光标所在文本节点里、光标之前的文字」识别触发。
 * 只看最后一个空白之后的那一段：首字符是触发符即触发，其余是 query。
 * `foo@bar`、`a@b.com`、`[/x` 的触发符前不是空白，所以都不触发。
 */
export function detectMentionTrigger(textBeforeCaret) {
	const text = String(textBeforeCaret ?? '');
	let start = text.length;
	while (start > 0 && !whitespacePattern.test(text[start - 1])) {
		start -= 1;
	}

	const token = text.slice(start);
	const kind = triggerKinds.get(token[0]);
	if (!kind) {
		return null;
	}

	return { kind, query: token.slice(1), start };
}

export function createSkillChip(skillId) {
	const id = String(skillId ?? '').trim();
	return { kind: 'skill', value: id, label: id, title: id };
}

export function createFileChip(relativePath, { isDirectory = false } = {}) {
	const normalized = String(relativePath ?? '').replace(/\\/g, '/').replace(/^\/+/, '');
	const value = isDirectory && !normalized.endsWith('/') ? `${normalized}/` : normalized;
	return { kind: 'file', value, label: value, title: value };
}

export function createCommandChip(name) {
	const value = String(name ?? '').trim().replace(/^\/+/, '');
	return { kind: 'command', value, label: `/${value}`, title: `/${value}` };
}

export function formatFileMention(path) {
	const value = String(path ?? '');
	return barePathPattern.test(value) ? `@${value}` : `@"${value}"`;
}

export function formatChipToken(kind, value) {
	switch (kind) {
		case 'skill':
			return `[/${value}]`;
		case 'file':
			return formatFileMention(value);
		case 'command':
			return `/${value}`;
		default:
			return '';
	}
}

const TEXT_NODE = 3;
const ELEMENT_NODE = 1;
const blockTags = new Set(['DIV', 'P', 'LI', 'BLOCKQUOTE']);

// 编辑器 DOM → 发送文本。textarea 语义：换行是 \n；连续空格时 Chromium 会写入 nbsp，还原成普通空格。
export function serializeEditorNodes(nodes) {
	let text = '';
	for (const node of Array.from(nodes ?? [])) {
		if (node.nodeType === TEXT_NODE) {
			text += node.data.replace(/ /g, ' ');
			continue;
		}

		if (node.nodeType !== ELEMENT_NODE) {
			continue;
		}

		const kind = node.getAttribute('data-chip-kind');
		if (kind) {
			text += formatChipToken(kind, node.getAttribute('data-chip-value') ?? '');
			continue;
		}

		if (node.tagName === 'BR') {
			text += '\n';
			continue;
		}

		// 回退模式（contenteditable=true）下浏览器会把分段包成 <div>：块元素之间补换行。
		if (blockTags.has(node.tagName) && text && !text.endsWith('\n')) {
			text += '\n';
		}

		text += serializeEditorNodes(node.childNodes);
	}

	return text;
}
