import { expect, it } from 'vitest';
import {
	escapeHtml,
	guessVueLanguage,
	highlightCode,
	highlightLines,
	languageForLines,
	languageForPath,
} from '../src/renderers/codeHighlight.js';

const spanBalance = (html) => (html.match(/<span /g) ?? []).length - (html.match(/<\/span>/g) ?? []).length;

it('maps file extensions to registered languages and leaves .vue for content guessing', () => {
	expect(languageForPath('src/a/b.ts')).toBe('typescript');
	expect(languageForPath('Foo.CS')).toBe('csharp');
	expect(languageForPath('web.config')).toBe('xml');
	expect(languageForPath('App.vue')).toBe('vue');
	expect(languageForPath('README')).toBe('');
	expect(languageForPath('weird.unknownext')).toBe('');
});

it('guesses the section of a .vue hunk from its lines', () => {
	expect(guessVueLanguage(["import { computed } from 'vue';", 'const a = 1;'])).toBe('javascript');
	expect(guessVueLanguage(['<div class="x">', '  <span>hi</span>', '</div>'])).toBe('xml');
	expect(guessVueLanguage(['.file-row {', '\tcolor: var(--text);', '}'])).toBe('css');
	expect(guessVueLanguage(['', '   '])).toBe('javascript');
	expect(languageForLines('src/App.vue', ['<template>'])).toBe('xml');
	expect(languageForLines('src/a.js', ['<template>'])).toBe('javascript');
});

it('highlights a block once and re-opens spans on every line so multi-line tokens survive the split', () => {
	const lines = highlightLines(['/* start', ' still a comment */', 'const x = 1;'], 'javascript');
	expect(lines).toHaveLength(3);
	expect(lines[0]).toContain('hljs-comment');
	expect(lines[1]).toContain('hljs-comment');
	expect(lines[2]).toContain('hljs-keyword');
	for (const line of lines) expect(spanBalance(line)).toBe(0);
});

it('falls back to escaped plain text for unknown languages and keeps the line count', () => {
	expect(highlightLines(['<a href="x">', "it's & done"], 'nope')).toEqual(['&lt;a href=&quot;x&quot;&gt;', 'it&#39;s &amp; done']);
	expect(highlightLines([], 'javascript')).toEqual([]);
	expect(highlightLines(['', ''], 'javascript')).toHaveLength(2);
});

it('keeps the markdown highlighter entry point working with aliases', () => {
	expect(highlightCode('const a = 1;', 'js')).toContain('hljs-keyword');
	expect(highlightCode('var x = 1;', 'C#')).toContain('hljs-keyword');
	expect(highlightCode('anything', 'nope')).toBe('');
	expect(escapeHtml('<b>&"\'')).toBe('&lt;b&gt;&amp;&quot;&#39;');
});
