import MarkdownIt from 'markdown-it';
import DOMPurify from 'dompurify';
import { highlightCode } from './codeHighlight.js';

const skillTokenPattern = /^\[\/([^\]\r\n]{1,80})\]/;

function skillTokenRule(state, silent) {
	if (state.env?.context !== 'user') {
		return false;
	}

	const match = skillTokenPattern.exec(state.src.slice(state.pos));
	if (!match) {
		return false;
	}

	if (!silent) {
		const token = state.push('skill_token', 'span', 0);
		token.meta = { name: match[1] };
	}

	state.pos += match[0].length;
	return true;
}

// 与 renderers/composerText.js 的裸路径字符类严格对称：开头或空白后的 `@`，接 `"..."` 或裸路径。
const fileMentionPattern = /^@(?:"([^"\r\n]+)"|([^\s"'<>()[\]{}|,;:!?@]+))/;

function fileMentionRule(state, silent) {
	if (state.env?.context !== 'user') {
		return false;
	}

	// 只认开头或空白后的 @：`a@b.com` 这类词中 @ 不是文件引用。
	if (state.pos > 0 && !/\s/.test(state.src[state.pos - 1])) {
		return false;
	}

	const match = fileMentionPattern.exec(state.src.slice(state.pos));
	if (!match) {
		return false;
	}

	const quoted = match[1] !== undefined;
	// 句尾的 `.` 不算路径：`看 @a/b.md.` 识别成 a/b.md，句点留给正文。
	const path = quoted ? match[1] : match[2].replace(/\.+$/, '');
	// 必须像路径（含 / 或 .）：`@张三`、`@everyone` 不是文件。
	if (!path || !/[./]/.test(path)) {
		return false;
	}

	if (!silent) {
		const token = state.push('file_mention', 'span', 0);
		token.meta = { path };
	}

	state.pos += quoted ? match[0].length : path.length + 1;
	return true;
}

const markdownIt = new MarkdownIt({
	html: false,
	breaks: false,
	linkify: false,
	typographer: false,
	highlight: highlightCode,
});

markdownIt.inline.ruler.before('emphasis', 'skill_token', skillTokenRule);
markdownIt.inline.ruler.before('emphasis', 'file_mention', fileMentionRule);
// chip 样式在 styles/inline-chip.css，与输入框里的 chip 同一套；转录侧是 HTML 串，不带图标。
markdownIt.renderer.rules.skill_token = (tokens, index) => {
	const name = markdownIt.utils.escapeHtml(tokens[index].meta?.name || '');
	return `<span class="inline-chip inline-chip--skill" role="text"><span class="inline-chip-label">${name}</span></span>`;
};
markdownIt.renderer.rules.file_mention = (tokens, index) => {
	const path = markdownIt.utils.escapeHtml(tokens[index].meta?.path || '');
	return `<span class="inline-chip inline-chip--file" role="text" title="${path}"><span class="inline-chip-label">${path}</span></span>`;
};

// markdown-it 默认规则表里没有 link_open（只有 image/code/fence 等），所以这里不能用
// "保存默认规则再调用" 的写法——那会调用 undefined。renderToken 才是 link_open 的默认渲染。
markdownIt.renderer.rules.link_open = (tokens, index, options, env, self) => {
	const token = tokens[index];
	if (token.attrIndex('target') < 0) token.attrSet('target', '_blank');
	if (token.attrIndex('rel') < 0) token.attrSet('rel', 'noopener noreferrer');
	return self.renderToken(tokens, index, options);
};

const defaultImage = markdownIt.renderer.rules.image;
markdownIt.renderer.rules.image = (tokens, index, options, env, self) => {
	const token = tokens[index];
	if (token.attrIndex('loading') < 0) token.attrSet('loading', 'lazy');
	if (token.attrIndex('class') < 0) token.attrSet('class', 'markdown-image');
	return defaultImage(tokens, index, options, env, self);
};

const sanitizeOptions = {
	ALLOWED_TAGS: [
		'p',
		'br',
		'hr',
		'h1',
		'h2',
		'h3',
		'h4',
		'h5',
		'h6',
		'ul',
		'ol',
		'li',
		'blockquote',
		'pre',
		'code',
		'table',
		'thead',
		'tbody',
		'tr',
		'th',
		'td',
		'a',
		'img',
		'em',
		'strong',
		'del',
		'span',
		'div',
	],
	ALLOWED_ATTR: ['class', 'href', 'src', 'alt', 'title', 'loading', 'rel', 'target', 'role'],
	// DOMPurify 对不在 URI_SAFE_ATTRIBUTES 里的属性也拿 URI 正则校验值，target/rel/loading
	// 的值不是 URL，不登记就会被静默删除——上面 link_open/image 规则加的目标属性等于白加。
	ADD_URI_SAFE_ATTR: ['target', 'rel', 'loading'],
	FORBID_TAGS: ['script', 'style', 'iframe', 'object', 'embed', 'form', 'input', 'textarea'],
	FORBID_ATTR: [/^on/i],
	ALLOW_UNKNOWN_PROTOCOLS: false,
	ALLOWED_URI_REGEXP: /^(?:(?:https?|mailto):|https:\/\/attachments\.selfclaw\.local\/)/i,
};

function sanitize(html) {
	return DOMPurify.sanitize(html, sanitizeOptions);
}

const cache = new Map();
const cacheLimit = 300;

export function renderMarkdown(source, { context = 'content' } = {}) {
	const markdownSource = String(source || '');
	const cacheKey = `${context}\u0000${markdownSource}`;
	const cached = cache.get(cacheKey);
	if (cached !== undefined) {
		return cached;
	}

	let rendered;
	try {
		// 用户消息里的单个换行是 Shift+Enter 刻意换的行，按硬换行渲染；助手正文仍遵循 CommonMark 软换行。
		markdownIt.set({ breaks: context === 'user' });
		rendered = sanitize(markdownIt.render(markdownSource, { context }));
	} catch (error) {
		// Markdown 渲染发生在每个正文段的 setup/watch 里：抛异常会被 Vue 换成注释节点，
		// 且流式更新时反复重抛，最终让整条转录卡死。单段渲染失败只降级成纯文本。
		console.error('Markdown render failed; falling back to plain text.', error);
		rendered = `<p>${markdownIt.utils.escapeHtml(markdownSource).replace(/\r?\n/g, '<br>')}</p>`;
	}

	if (cache.size >= cacheLimit) {
		const first = cache.keys().next().value;
		if (first !== undefined) cache.delete(first);
	}
	cache.set(cacheKey, rendered);
	return rendered;
}

export function clearMarkdownCache() {
	cache.clear();
}
