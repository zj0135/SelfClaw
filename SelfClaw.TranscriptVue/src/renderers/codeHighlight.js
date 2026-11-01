import hljs from 'highlight.js/lib/core';
import javascript from 'highlight.js/lib/languages/javascript';
import typescript from 'highlight.js/lib/languages/typescript';
import json from 'highlight.js/lib/languages/json';
import bash from 'highlight.js/lib/languages/bash';
import powershell from 'highlight.js/lib/languages/powershell';
import csharp from 'highlight.js/lib/languages/csharp';
import python from 'highlight.js/lib/languages/python';
import java from 'highlight.js/lib/languages/java';
import go from 'highlight.js/lib/languages/go';
import rust from 'highlight.js/lib/languages/rust';
import sql from 'highlight.js/lib/languages/sql';
import css from 'highlight.js/lib/languages/css';
import xml from 'highlight.js/lib/languages/xml';
import markdown from 'highlight.js/lib/languages/markdown';
import yaml from 'highlight.js/lib/languages/yaml';

// 全前端唯一的 highlight.js 实例与语言表：markdown 围栏代码与审查面板的 diff 共用，
// 保证两处的 .hljs-* 类名与配色 token 一致。

const languageAliases = {
	js: 'javascript',
	jsx: 'javascript',
	ts: 'typescript',
	tsx: 'typescript',
	sh: 'bash',
	shell: 'bash',
	pwsh: 'powershell',
	ps: 'powershell',
	cs: 'csharp',
	'c#': 'csharp',
	html: 'xml',
	xhtml: 'xml',
	md: 'markdown',
	yml: 'yaml',
};

const languages = {
	javascript,
	typescript,
	json,
	bash,
	powershell,
	csharp,
	python,
	java,
	go,
	rust,
	sql,
	css,
	xml,
	markdown,
	yaml,
};

for (const [name, definition] of Object.entries(languages)) {
	hljs.registerLanguage(name, definition);
}

for (const [alias, name] of Object.entries(languageAliases)) {
	hljs.registerAliases(alias, { languageName: name });
}

const allowedLanguages = new Set(Object.keys(languages));

// 文件扩展名 → 语言。.vue 没有独立语法，返回 'vue' 交给 guessVueLanguage 按内容挑。
const extensionLanguages = {
	js: 'javascript', mjs: 'javascript', cjs: 'javascript', jsx: 'javascript',
	ts: 'typescript', mts: 'typescript', cts: 'typescript', tsx: 'typescript',
	json: 'json', jsonc: 'json', json5: 'json',
	sh: 'bash', bash: 'bash', zsh: 'bash',
	ps1: 'powershell', psm1: 'powershell', psd1: 'powershell',
	cs: 'csharp', py: 'python', java: 'java', go: 'go', rs: 'rust', sql: 'sql',
	css: 'css', scss: 'css', less: 'css',
	html: 'xml', htm: 'xml', xml: 'xml', svg: 'xml', xaml: 'xml', csproj: 'xml', props: 'xml', targets: 'xml',
	slnx: 'xml', config: 'xml', resx: 'xml',
	md: 'markdown', markdown: 'markdown',
	yml: 'yaml', yaml: 'yaml',
};

const HTML_LINE = /^\s*<\/?[a-zA-Z][\w-]*(?:[\s>]|\/>|$)/;
// 不收 var / class / this：CSS 的 var(--x)、属性选择器 [class=…] 会撞上，而 <script setup> 里几乎不用它们。
const SCRIPT_LINE = /(?:^|[^\w$.])(?:import|export|const|let|function|return|await|async|if|else|for|while|switch|case|try|catch|throw|new)(?![\w$])|=>/;
const CSS_DECLARATION = /^\s*[a-z-]+\s*:\s*[^;{}]+;\s*$/;
const CSS_SELECTOR = /^\s*[.#:&@[\]\w\s,>+~*()="'-]+\{\s*$/;

export function canonicalLanguage(language) {
	const normalized = String(language || '').trim().toLowerCase();
	const canonical = languageAliases[normalized] || normalized;
	return allowedLanguages.has(canonical) ? canonical : '';
}

export function highlightCode(code, language) {
	const canonical = canonicalLanguage(language);
	if (!canonical) return '';
	try {
		return hljs.highlight(code, { language: canonical, ignoreIllegals: true }).value;
	} catch {
		return '';
	}
}

export function escapeHtml(text) {
	return String(text).replace(/[&<>"']/g, (character) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[character]);
}

export function languageForPath(path) {
	const name = String(path || '').split('/').pop().toLowerCase();
	const dot = name.lastIndexOf('.');
	if (dot < 0) return '';
	const extension = name.slice(dot + 1);
	return extension === 'vue' ? 'vue' : extensionLanguages[extension] || '';
}

// .vue 的一个 hunk 通常只落在 script / template / style 之一：数行投票，脚本优先（平局或全空时也取脚本）。
export function guessVueLanguage(lines) {
	let html = 0;
	let script = 0;
	let css = 0;
	for (const line of lines) {
		if (HTML_LINE.test(line)) html++;
		else if (SCRIPT_LINE.test(line)) script++;
		else if (CSS_DECLARATION.test(line) || CSS_SELECTOR.test(line)) css++;
	}
	if (html > script && html >= css) return 'xml';
	if (css > script && css > html) return 'css';
	return 'javascript';
}

export function languageForLines(path, lines) {
	const language = languageForPath(path);
	return language === 'vue' ? guessVueLanguage(lines) : language;
}

// 整段高亮再按行切：逐行跑 hljs 会丢掉跨行结构（块注释、模板字符串）。把这些行拼成一段跑一次，
// 把结果按 \n 切回去，切点处先闭合未闭合的 <span>，下一行开头再按原顺序重新打开。
export function highlightLines(lines, language) {
	const canonical = canonicalLanguage(language);
	if (!canonical || lines.length === 0) return lines.map(escapeHtml);
	let html;
	try {
		html = hljs.highlight(lines.join('\n'), { language: canonical, ignoreIllegals: true }).value;
	} catch {
		return lines.map(escapeHtml);
	}
	return splitHighlightedLines(html, lines.length);
}

function splitHighlightedLines(html, expectedCount) {
	// hljs 的输出只含 <span class="…"> / </span> 与已转义文本，不会出现其他标签。
	const tokenPattern = /<span class="[^"]*">|<\/span>|\n/g;
	const result = [];
	const open = [];
	let current = '';
	let last = 0;
	let match;
	while ((match = tokenPattern.exec(html)) !== null) {
		current += html.slice(last, match.index);
		last = match.index + match[0].length;
		const token = match[0];
		if (token === '\n') {
			result.push(current + '</span>'.repeat(open.length));
			current = open.join('');
		} else if (token === '</span>') {
			open.pop();
			current += token;
		} else {
			open.push(token);
			current += token;
		}
	}
	result.push(current + html.slice(last) + '</span>'.repeat(open.length));
	while (result.length < expectedCount) result.push('');
	return result.length > expectedCount ? result.slice(0, expectedCount) : result;
}
