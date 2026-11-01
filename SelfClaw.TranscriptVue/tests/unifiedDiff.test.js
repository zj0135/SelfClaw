import { expect, it } from 'vitest';
import { parseUnifiedDiff } from '../src/renderers/unifiedDiff.js';

const patch = [
	'diff --git a/src/App.vue b/src/App.vue',
	'index 1111111..2222222 100644',
	'--- a/src/App.vue',
	'+++ b/src/App.vue',
	'@@ -10,3 +10,4 @@ import X',
	" import X from './x';",
	'-const a = 1;',
	'+const a = 2;',
	'+const b = 3;',
	' export default a;',
	'@@ -40,2 +41,2 @@',
	' tail',
	'-old',
	'+new',
	'\\ No newline at end of file',
	'',
].join('\n');

it('skips the file header, numbers lines per hunk, and marks the missing trailing newline', () => {
	const { hunks } = parseUnifiedDiff(patch);
	expect(hunks).toHaveLength(2);
	expect(hunks[0].header).toBe('@@ -10,3 +10,4 @@ import X');
	expect(hunks[0]).toMatchObject({ context: 'import X', oldStart: 10, newStart: 10 });
	expect(hunks[1]).toMatchObject({ context: '', oldStart: 40, newStart: 41 });
	expect(hunks[0].lines.map((line) => [line.kind, line.oldNumber, line.newNumber, line.text])).toEqual([
		['context', 10, 10, "import X from './x';"],
		['remove', 11, null, 'const a = 1;'],
		['add', null, 11, 'const a = 2;'],
		['add', null, 12, 'const b = 3;'],
		['context', 12, 13, 'export default a;'],
	]);
	expect(hunks[1].lines.map((line) => [line.kind, line.oldNumber, line.newNumber])).toEqual([
		['context', 40, 41],
		['remove', 41, null],
		['add', null, 42],
	]);
	expect(hunks[1].lines[2].noNewline).toBe(true);
	expect(hunks[0].lines.some((line) => line.noNewline)).toBe(false);
});

it('strips the CR of CRLF patches from headers and lines', () => {
	const { hunks } = parseUnifiedDiff('@@ -1,1 +1,1 @@\r\n-a\r\n+b\r\n');
	expect(hunks[0].header).toBe('@@ -1,1 +1,1 @@');
	expect(hunks[0].lines.map((line) => line.text)).toEqual(['a', 'b']);
});

it('degrades the combined diff of a conflicted file by either marker column', () => {
	const combined = ['@@@ -1,3 -1,3 +1,4 @@@', '  same', '- ours', ' -theirs', '++merged', ''].join('\n');
	const { hunks } = parseUnifiedDiff(combined);
	expect(hunks[0].lines.map((line) => [line.kind, line.text])).toEqual([
		['context', 'same'],
		['remove', 'ours'],
		['remove', 'theirs'],
		['add', 'merged'],
	]);
});

it('returns no hunks for empty, non-string, or header-only input such as a pure rename', () => {
	expect(parseUnifiedDiff('').hunks).toEqual([]);
	expect(parseUnifiedDiff(null).hunks).toEqual([]);
	expect(parseUnifiedDiff('diff --git a/old b/new\nsimilarity index 100%\nrename from old\nrename to new\n').hunks).toEqual([]);
});

it('treats a bare empty line inside a hunk as empty context', () => {
	const { hunks } = parseUnifiedDiff('@@ -1,2 +1,2 @@\n \n+x\n');
	expect(hunks[0].lines[0]).toMatchObject({ kind: 'context', text: '', oldNumber: 1, newNumber: 1 });
	expect(hunks[0].lines[1]).toMatchObject({ kind: 'add', text: 'x', newNumber: 2 });
});
