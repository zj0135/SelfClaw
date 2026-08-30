import MarkdownIt from 'markdown-it';
import DOMPurify from 'dompurify';
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
	'C#': 'csharp',
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
const skillTokenPattern = /^\[\/([^\]\r\n]{1,80})\]/;

function highlightCode(code, language) {
	const normalized = String(language || '')
		.trim()
		.toLowerCase();
	const canonical = languageAliases[normalized] || normalized;
	if (!allowedLanguages.has(canonical)) {
		return '';
	}

	try {
		return hljs.highlight(code, { language: canonical, ignoreIllegals: true }).value;
	} catch {
		return '';
	}
}

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

const markdownIt = new MarkdownIt({
	html: false,
	breaks: false,
	linkify: false,
	typographer: false,
	highlight: highlightCode,
});

markdownIt.inline.ruler.before('emphasis', 'skill_token', skillTokenRule);
markdownIt.renderer.rules.skill_token = (tokens, index) => {
	const name = markdownIt.utils.escapeHtml(tokens[index].meta?.name || '');
	return `<span class="composer-inline-skill message-skill-chip" role="text"><span class="composer-inline-skill-name">${name}</span></span>`;
};

const defaultLinkOpen = markdownIt.renderer.rules.link_open;
markdownIt.renderer.rules.link_open = (tokens, index, options, env, self) => {
	const token = tokens[index];
	if (token.attrIndex('target') < 0) token.attrSet('target', '_blank');
	if (token.attrIndex('rel') < 0) token.attrSet('rel', 'noopener noreferrer');
	return defaultLinkOpen(tokens, index, options, env, self);
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

	const rendered = sanitize(markdownIt.render(markdownSource, { context }));
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
