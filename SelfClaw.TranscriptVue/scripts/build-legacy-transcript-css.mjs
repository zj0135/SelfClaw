import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
const projectRoot = path.resolve(scriptDirectory, '..');
const legacyTranscriptPath = path.resolve(projectRoot, '../SelfClaw.Desktop/Assets/Transcript/transcript.html');
const generatedDirectory = path.resolve(projectRoot, 'src/generated');
const generatedCssPath = path.resolve(generatedDirectory, 'legacy-transcript.css');
const unusedSelectors = [
	'.composer-meta',
	'.composer-toolbar',
	'.composer-toolbar .composer-meta',
	'.meta-pill',
	'.status-pill',
	'.permission-control',
	'.team-controls',
	'.team-control',
	'.team-control-label',
	"html[data-theme='light'] .ghost-link:hover",
];

function escapeRegExp(value) {
	return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function stripSelectorBlocks(css, selectors) {
	return selectors.reduce((result, selector) => {
		const pattern = new RegExp(`(^|\\n)[\\t ]*${escapeRegExp(selector)}\\s*\\{[^{}]*\\}`, 'g');
		return result.replace(pattern, '$1');
	}, css);
}

const html = await readFile(legacyTranscriptPath, 'utf8');
const styleMatch = html.match(/<style>([\s\S]*?)<\/style>/i);

if (!styleMatch) {
	throw new Error(`Unable to find a <style> block in ${legacyTranscriptPath}.`);
}

const trimmedCss = stripSelectorBlocks(styleMatch[1].trim(), unusedSelectors).replace(/\n{3,}/g, '\n\n');

await mkdir(generatedDirectory, { recursive: true });
await writeFile(generatedCssPath, `${trimmedCss}\n`, 'utf8');
