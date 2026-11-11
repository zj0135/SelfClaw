<script setup>
import { h, onBeforeUnmount, onMounted, ref, render, watch } from 'vue';
import ComposerChip from './ComposerChip.vue';
import { detectMentionTrigger, serializeEditorNodes } from '../../../renderers/composerText.js';

// 可编辑区：contenteditable="plaintext-only" 的 div，里面是文本节点 + 不可编辑的 chip span。
// Chromium（WebView2）对 plaintext-only 的保证（已用 msedge 验证）：打字只产生文本节点、Enter 产生 \n、
// 粘贴降级为纯文本；退格/Delete 把 contenteditable=false 的行内元素当原子块整块删除——这就是「一次退格整块删除」。
const props = defineProps({
	modelValue: { type: String, default: '' },
	placeholder: { type: String, default: '' },
	// 弹层打开时方向键 / Enter / Tab / Esc 先交给它（navigate 事件）；被 preventDefault 的按键这里不再处理。
	mentionOpen: { type: Boolean, default: false },
});
const emit = defineEmits(['update:modelValue', 'submit', 'trigger', 'navigate', 'paste']);

const TEXT_NODE = 3;
const ELEMENT_NODE = 1;
const whitespacePattern = /\s/;

const rootRef = ref(null);
let composing = false;
let observer = null;

function serialize() {
	return serializeEditorNodes(rootRef.value?.childNodes);
}

function syncValue() {
	const root = rootRef.value;
	if (!root) {
		return;
	}

	const value = serialize();
	// 占位符靠 :empty：空值时清掉浏览器可能残留的 <br> / 空文本节点。
	if (value === '' && root.childNodes.length) {
		root.replaceChildren();
	}

	if (value !== props.modelValue) {
		emit('update:modelValue', value);
	}

	emitTrigger();
}

// 父组件只会把值清空（发送成功后）；编辑器不做文本 → chip 的反向解析。
watch(() => props.modelValue, (value) => {
	if (value === '' && rootRef.value?.childNodes.length) {
		rootRef.value.replaceChildren();
	}
});

function currentRange() {
	const selection = window.getSelection();
	const root = rootRef.value;
	if (!selection || selection.rangeCount === 0 || !root) {
		return null;
	}

	const range = selection.getRangeAt(0);
	return root.contains(range.startContainer) ? range : null;
}

function emitTrigger() {
	const range = currentRange();
	if (!range || !range.collapsed || range.startContainer.nodeType !== TEXT_NODE) {
		emit('trigger', null);
		return;
	}

	const node = range.startContainer;
	const trigger = detectMentionTrigger(node.data.slice(0, range.startOffset));
	emit('trigger', trigger ? { kind: trigger.kind, query: trigger.query, anchor: node, start: trigger.start } : null);
}

function onInput() {
	if (!composing) {
		syncValue();
	}
}

function onCompositionStart() {
	composing = true;
}

function onCompositionEnd() {
	composing = false;
	syncValue();
}

function onKeydown(event) {
	if (composing || event.isComposing) {
		return;
	}

	if (props.mentionOpen) {
		emit('navigate', event);
		if (event.defaultPrevented) {
			return;
		}
	}

	if (event.key === 'Enter' && !event.shiftKey && !event.ctrlKey && !event.altKey && !event.metaKey) {
		event.preventDefault();
		emit('submit');
	}
}

// 方向键 / 点击移动光标不产生 input，但会进入或离开 `@query` 区间。
function onSelectionChange() {
	if (document.activeElement === rootRef.value) {
		emitTrigger();
	}
}

function onBlur() {
	emit('trigger', null);
}

function createChipElement(chip) {
	const host = document.createElement('span');
	host.className = `inline-chip inline-chip--${chip.kind}`;
	host.setAttribute('contenteditable', 'false');
	host.setAttribute('data-chip-kind', chip.kind);
	host.setAttribute('data-chip-value', chip.value);
	host.setAttribute('data-chip-label', chip.label);
	if (chip.title) {
		host.title = chip.title;
	}

	mountChip(host);
	return host;
}

function mountChip(host) {
	render(h(ComposerChip, {
		kind: host.getAttribute('data-chip-kind'),
		value: host.getAttribute('data-chip-value') ?? '',
		label: host.getAttribute('data-chip-label') ?? '',
	}), host);
}

function forEachChip(node, callback) {
	if (node.nodeType !== ELEMENT_NODE) {
		return;
	}

	if (node.hasAttribute('data-chip-kind')) {
		callback(node);
	}

	node.querySelectorAll('[data-chip-kind]').forEach(callback);
}

function placeCaret(textNode, offset) {
	const selection = window.getSelection();
	if (!selection) {
		return;
	}

	const range = document.createRange();
	range.setStart(textNode, offset);
	range.collapse(true);
	selection.removeAllRanges();
	selection.addRange(range);
}

function ensureRange() {
	const root = rootRef.value;
	if (!root) {
		return null;
	}

	const selection = window.getSelection();
	// 先记下选区再聚焦：focus() 在部分实现（含 jsdom）里会把光标重置到元素开头，之后再恢复。
	const saved = currentRange()?.cloneRange() ?? null;
	if (document.activeElement !== root) {
		root.focus();
	}

	if (saved) {
		selection?.removeAllRanges();
		selection?.addRange(saved);
	} else {
		// 选区不在编辑器里（例如从「+」菜单插入）：放到末尾。
		const end = document.createRange();
		end.selectNodeContents(root);
		end.collapse(false);
		selection?.removeAllRanges();
		selection?.addRange(end);
	}

	return selection?.rangeCount ? selection.getRangeAt(0) : null;
}

function endsWithoutWhitespace(node) {
	if (!node) {
		return false;
	}

	if (node.nodeType === TEXT_NODE) {
		return node.data.length > 0 && !whitespacePattern.test(node.data[node.data.length - 1]);
	}

	return node.nodeType === ELEMENT_NODE && node.tagName !== 'BR';
}

// chip 紧贴前文会让 token 粘在单词上（`hello@a.cs`、`[chip]@a.cs`），转录与模型都认不出来：补一个空格。
function needsLeadingSpace(range) {
	const { startContainer, startOffset } = range;
	if (startContainer.nodeType === TEXT_NODE) {
		return startOffset > 0
			? !whitespacePattern.test(startContainer.data[startOffset - 1])
			: endsWithoutWhitespace(startContainer.previousSibling);
	}

	return endsWithoutWhitespace(startContainer.childNodes[startOffset - 1] ?? null);
}

function insertChipAt(range, chip) {
	range.deleteContents();
	const leading = needsLeadingSpace(range);
	const space = document.createTextNode(' ');
	range.insertNode(space);
	const host = createChipElement(chip);
	space.before(host);
	if (leading) {
		host.before(document.createTextNode(' '));
	}

	// Range 操作会留下拆分出的空文本节点：合并掉，让后续的触发检测只看一个连续文本节点。
	// space 的前一个兄弟是 chip 元素，不会被合并进别的节点，光标引用仍然有效。
	rootRef.value?.normalize();
	placeCaret(space, 1);
	syncValue();
}

function insertChip(chip) {
	const range = ensureRange();
	if (range) {
		insertChipAt(range, chip);
	}
}

// 用 chip 替换触发文本（如 `@src/a`）：anchor/start 来自 trigger 事件；光标须仍在同一文本节点且位于 start 之后，
// 否则退化成在光标处插入。
function replaceTriggerWithChip(chip, trigger) {
	const range = ensureRange();
	if (!range) {
		return;
	}

	const node = range.startContainer;
	if (trigger && range.collapsed && node === trigger.anchor && node.nodeType === TEXT_NODE && range.startOffset >= trigger.start) {
		range.setStart(node, trigger.start);
	}

	insertChipAt(range, chip);
}

function clear() {
	rootRef.value?.replaceChildren();
	emit('update:modelValue', '');
	emit('trigger', null);
}

function setText(text) {
	const root = rootRef.value;
	if (!root) {
		return;
	}

	root.replaceChildren(document.createTextNode(text || ''));
	syncValue();
	focus();
	const selection = window.getSelection();
	if (selection) {
		const range = document.createRange();
		range.selectNodeContents(root);
		range.collapse(false);
		selection.removeAllRanges();
		selection.addRange(range);
	}
}

function focus() {
	rootRef.value?.focus();
}

onMounted(() => {
	observer = new MutationObserver((records) => {
		for (const record of records) {
			// 删除的 chip 要卸掉 Vue 渲染实例；撤销（Ctrl+Z）放回来的空壳要重新渲染内容。
			record.removedNodes.forEach((node) => forEachChip(node, (chip) => render(null, chip)));
			record.addedNodes.forEach((node) => forEachChip(node, (chip) => {
				if (!chip.firstChild) {
					mountChip(chip);
				}
			}));
		}
	});
	observer.observe(rootRef.value, { childList: true, subtree: true });
	document.addEventListener('selectionchange', onSelectionChange);
});

onBeforeUnmount(() => {
	observer?.disconnect();
	document.removeEventListener('selectionchange', onSelectionChange);
	rootRef.value?.querySelectorAll('[data-chip-kind]').forEach((chip) => render(null, chip));
});

defineExpose({ insertChip, replaceTriggerWithChip, clear, focus, setText });
</script>

<template>
	<div ref="rootRef" class="composer-editor" contenteditable="plaintext-only" role="textbox"
		aria-multiline="true" aria-label="消息输入" aria-autocomplete="list" :aria-expanded="props.mentionOpen ? 'true' : 'false'"
		:data-placeholder="props.placeholder" @input="onInput" @keydown="onKeydown" @compositionstart="onCompositionStart"
		@compositionend="onCompositionEnd" @paste="emit('paste', $event)" @blur="onBlur"></div>
</template>

<style scoped>
/* 外观必须由编辑器自己持有：ComposerInput 是多根节点组件（编辑器 + 弹层），父组件的 scoped 样式
   到不了这里；textarea 时代的字体、行高、最小高度与「无边框无 outline」约定全部照搬。
   行内 chip 样式在 styles/inline-chip.css。 */
.composer-editor {
	width: 100%;
	min-height: 64px;
	flex: 1 1 auto;
	max-height: 40vh;
	overflow-y: auto;
	padding: 0 2px;
	border: 0;
	outline: none;
	background: transparent;
	color: var(--text-strong);
	font: 14px/1.65 var(--font-ui);
	cursor: text;
	white-space: pre-wrap;
	overflow-wrap: anywhere;
	caret-color: var(--accent);
}

.composer-editor:empty::before {
	content: attr(data-placeholder);
	color: var(--muted-soft);
	pointer-events: none;
}
</style>
