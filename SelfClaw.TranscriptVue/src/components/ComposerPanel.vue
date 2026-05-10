<script setup>
import { computed, onMounted, onUnmounted, ref, watch } from 'vue';
import PlanPanel from './PlanPanel.vue';

const props = defineProps({
	showPlanPanel: {
		type: Boolean,
		default: false,
	},
	planPanel: {
		type: Object,
		default: null,
	},
	planSteps: {
		type: Array,
		default: () => [],
	},
	planPanelCollapsed: {
		type: Boolean,
		default: false,
	},
	collapsedPlanText: {
		type: String,
		default: '',
	},
	composerValue: {
		type: String,
		default: '',
	},
	composerPlaceholder: {
		type: String,
		default: '',
	},
	isChannelMode: {
		type: Boolean,
		default: false,
	},
	profiles: {
		type: Array,
		default: () => [],
	},
	selectedProfileId: {
		type: String,
		default: '',
	},
	toolPermissionModes: {
		type: Array,
		default: () => [],
	},
	selectedToolPermissionModeId: {
		type: String,
		default: '',
	},
	showPlanningToggle: {
		type: Boolean,
		default: false,
	},
	showVisualizationToggle: {
		type: Boolean,
		default: false,
	},
	isBusy: {
		type: Boolean,
		default: false,
	},
	isPlanningModeEnabled: {
		type: Boolean,
		default: false,
	},
	isReasoningEnabled: {
		type: Boolean,
		default: false,
	},
	visualizationEnabled: {
		type: Boolean,
		default: false,
	},
	sendButtonDisabled: {
		type: Boolean,
		default: false,
	},
	contextUsage: {
		type: Object,
		default: null,
	},
	slashPalette: {
		type: Object,
		default: () => ({ open: false, activeIndex: 0 }),
	},
	slashPaletteItems: {
		type: Array,
		default: () => [],
	},
	commandFeedback: {
		type: Object,
		default: null,
	},
	attachments: {
		type: Array,
		default: () => [],
	},
});

const emit = defineEmits([
	'composer-input',
	'composer-keydown',
	'select-profile',
	'select-permission',
	'toggle-reasoning-mode',
	'toggle-planning-mode',
	'toggle-visualization-mode',
	'toggle-plan-panel-collapse',
	'pick-images',
	'capture-screenshot',
	'remove-attachment',
	'select-slash-item',
	'confirm-command',
	'clear-command-feedback',
	'send-click',
]);

const composerEl = ref(null);
const toolsShellEl = ref(null);
const toolsMenuOpen = ref(false);
const isSyncingComposerDom = ref(false);
const contextMessageOverheadTokens = 4;
const contextImageMetadataTokens = 32;

const contextStats = computed(() => {
	const usage = props.contextUsage || {};
	const contextWindowTokens = normalizeTokenCount(usage.contextWindowTokens);
	if (contextWindowTokens <= 0) {
		return { visible: false };
	}

	const baseTokens = normalizeTokenCount(usage.usedTokens);
	const draftTokens = estimateDraftTokens(props.composerValue, props.attachments);
	const usedTokens = Math.max(0, baseTokens + draftTokens);
	const ratio = usedTokens / contextWindowTokens;
	const percent = Math.max(0, Math.round(ratio * 100));
	const ringPercent = `${Math.max(0, Math.min(100, ratio * 100))}%`;
	const autoCompactTokenLimit = normalizeTokenCount(usage.autoCompactTokenLimit);
	const level = contextUsageLevel(ratio);
	const autoCompactText = autoCompactTokenLimit > 0
		? usedTokens >= autoCompactTokenLimit
			? 'SelfClaw 已自动压缩其背景信息'
			: `SelfClaw 达到 ${formatTokenCount(autoCompactTokenLimit)} 标记后自动压缩其背景信息`
		: 'SelfClaw 超过窗口时会尝试压缩其背景信息';

	return {
		visible: true,
		usedTokens,
		contextWindowTokens,
		percent,
		percentText: percent > 999 ? '>999%' : `${percent}%`,
		ringPercent,
		levelText: level.text,
		levelClass: level.className,
		usedText: formatTokenCount(usedTokens),
		windowText: formatTokenCount(contextWindowTokens),
		autoCompactText,
	};
});

function normalizeTokenCount(value) {
	const numeric = Number(value || 0);
	if (!Number.isFinite(numeric) || numeric <= 0) {
		return 0;
	}

	return Math.round(numeric);
}

function estimateDraftTokens(text, attachments) {
	const contentTokens = estimateTextTokens(text);
	const attachmentTokens = Array.isArray(attachments) ? attachments.length * contextImageMetadataTokens : 0;
	return contentTokens > 0 || attachmentTokens > 0
		? contextMessageOverheadTokens + contentTokens + attachmentTokens
		: 0;
}

function estimateTextTokens(text) {
	if (!text) {
		return 0;
	}

	return Math.max(1, Math.ceil(String(text).length / 4));
}

function contextUsageLevel(ratio) {
	if (ratio >= 1) {
		return { text: '超限', className: 'over' };
	}

	if (ratio >= 0.9) {
		return { text: '超高', className: 'critical' };
	}

	if (ratio >= 0.75) {
		return { text: '较高', className: 'high' };
	}

	if (ratio >= 0.5) {
		return { text: '中等', className: 'medium' };
	}

	return { text: '正常', className: 'normal' };
}

function formatTokenCount(value) {
	const tokens = normalizeTokenCount(value);
	if (tokens >= 1000000) {
		const scaled = tokens / 1000000;
		return `${scaled.toFixed(scaled >= 10 ? 0 : 1)}m`;
	}

	if (tokens >= 1000) {
		const scaled = tokens / 1000;
		return `${scaled.toFixed(scaled >= 100 ? 0 : 1)}k`;
	}

	return `${tokens}`;
}

function toggleToolsMenu() {
	if (props.isChannelMode) {
		return;
	}

	toolsMenuOpen.value = !toolsMenuOpen.value;
}

function closeToolsMenu() {
	toolsMenuOpen.value = false;
}

function onDocumentPointerDown(event) {
	if (!toolsMenuOpen.value || toolsShellEl.value?.contains(event.target)) {
		return;
	}

	closeToolsMenu();
}

function requestImagePicker() {
	emit('pick-images');
	closeToolsMenu();
}

function requestScreenshotCapture() {
	closeToolsMenu();
	window.setTimeout(() => emit('capture-screenshot'), 0);
}

function formatAttachmentSize(byteLength) {
	const size = Number(byteLength || 0);
	if (size >= 1024 * 1024) {
		return `${(size / (1024 * 1024)).toFixed(size >= 10 * 1024 * 1024 ? 0 : 1)} MB`;
	}

	if (size >= 1024) {
		return `${Math.max(1, Math.round(size / 1024))} KB`;
	}

	return `${Math.max(0, size)} B`;
}

function slashItemTypeLabel(item) {
	return item?.type === 'skill' ? '[skill]' : '[command]';
}

function slashItemDescription(item) {
	if (!item) {
		return '';
	}

	if (item.type === 'command' && item.argumentHint) {
		return `${item.description || ''} ${item.argumentHint}`.trim();
	}

	return item.description || '';
}

function renderComposerDomFromValue(value) {
	const editor = composerEl.value;
	if (!editor || serializeComposerEditor(editor) === String(value || '')) {
		return;
	}

	isSyncingComposerDom.value = true;
	editor.replaceChildren(...createComposerNodes(String(value || '')));
	isSyncingComposerDom.value = false;
}

function createComposerNodes(value) {
	const nodes = [];
	const pattern = /\[\/([^\]\r\n]{1,80})\]/g;
	let lastIndex = 0;
	let match;
	while ((match = pattern.exec(value)) !== null) {
		if (match.index > lastIndex) {
			nodes.push(document.createTextNode(value.slice(lastIndex, match.index)));
		}

		nodes.push(createSkillChipNode(match[1], match[0]));
		lastIndex = match.index + match[0].length;
	}

	if (lastIndex < value.length) {
		nodes.push(document.createTextNode(value.slice(lastIndex)));
	}

	return nodes;
}

function createSkillChipNode(label, rawText) {
	const chip = document.createElement('span');
	chip.className = 'composer-inline-skill';
	chip.contentEditable = 'false';
	chip.dataset.raw = rawText;
	chip.dataset.skillName = label;
	chip.setAttribute('role', 'text');

	const icon = document.createElement('span');
	icon.className = 'composer-inline-skill-icon';
	icon.setAttribute('aria-hidden', 'true');
	icon.innerHTML = '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linejoin="round"><path d="M8 1.8 13 4.6v6.8L8 14.2l-5-2.8V4.6L8 1.8Z"></path><path d="M3.2 4.8 8 7.5l4.8-2.7"></path><path d="M8 7.5v6.2"></path></svg>';

	const name = document.createElement('span');
	name.className = 'composer-inline-skill-name';
	name.textContent = label;

	chip.append(icon, name);
	return chip;
}

function serializeComposerEditor(editor) {
	let text = '';
	for (const node of editor.childNodes) {
		text += serializeComposerNode(node);
	}

	return text.replace(/\u00a0/g, ' ');
}

function serializeComposerNode(node) {
	if (node.nodeType === Node.TEXT_NODE) {
		return node.nodeValue || '';
	}

	if (!(node instanceof HTMLElement)) {
		return node.textContent || '';
	}

	if (node.classList.contains('composer-inline-skill')) {
		return node.dataset.raw || `[/${node.dataset.skillName || node.textContent || ''}]`;
	}

	if (node.tagName === 'BR') {
		return '\n';
	}

	let text = '';
	for (const child of node.childNodes) {
		text += serializeComposerNode(child);
	}

	if (node.tagName === 'DIV' || node.tagName === 'P') {
		return `${text}\n`;
	}

	return text;
}

function getComposerSelectionOffset() {
	return getComposerSelectionOffsets().start;
}

function getComposerSelectionOffsets() {
	const editor = composerEl.value;
	const selection = window.getSelection();
	if (!editor || !selection || selection.rangeCount === 0) {
		const offset = props.composerValue.length;
		return { start: offset, end: offset };
	}

	const range = selection.getRangeAt(0);
	if (
		(!editor.contains(range.startContainer) && range.startContainer !== editor) ||
		(!editor.contains(range.endContainer) && range.endContainer !== editor)
	) {
		const offset = props.composerValue.length;
		return { start: offset, end: offset };
	}

	return {
		start: getComposerBoundaryOffset(editor, range.startContainer, range.startOffset),
		end: getComposerBoundaryOffset(editor, range.endContainer, range.endOffset),
	};
}

function getComposerBoundaryOffset(editor, container, offset) {
	const before = document.createRange();
	before.selectNodeContents(editor);
	before.setEnd(container, offset);
	return serializeRangeContents(before);
}

function serializeRangeContents(range) {
	const fragment = range.cloneContents();
	let text = '';
	for (const node of fragment.childNodes) {
		text += serializeComposerNode(node);
	}

	return text.replace(/\u00a0/g, ' ').length;
}

function setComposerSelectionRange(start, end = start) {
	const editor = composerEl.value;
	if (!editor) {
		return;
	}

	const selection = window.getSelection();
	if (!selection) {
		return;
	}

	const range = document.createRange();
	const startPoint = findComposerDomPoint(editor, Math.max(0, start));
	const endPoint = findComposerDomPoint(editor, Math.max(0, end));
	range.setStart(startPoint.node, startPoint.offset);
	range.setEnd(endPoint.node, endPoint.offset);
	selection.removeAllRanges();
	selection.addRange(range);
}

function findComposerDomPoint(root, targetOffset) {
	let remaining = targetOffset;
	let lastPoint = { node: root, offset: root.childNodes.length };

	for (let index = 0; index < root.childNodes.length; index += 1) {
		const node = root.childNodes[index];
		const length = serializedNodeLength(node);
		if (remaining <= length) {
			return pointWithinComposerNode(root, node, index, remaining);
		}

		remaining -= length;
		lastPoint = { node: root, offset: index + 1 };
	}

	return lastPoint;
}

function pointWithinComposerNode(parent, node, index, offset) {
	if (node.nodeType === Node.TEXT_NODE) {
		return { node, offset: Math.min(offset, (node.nodeValue || '').length) };
	}

	if (node instanceof HTMLElement && node.classList.contains('composer-inline-skill')) {
		return offset <= 0 ? { node: parent, offset: index } : { node: parent, offset: index + 1 };
	}

	if (node instanceof HTMLElement && node.tagName === 'BR') {
		return offset <= 0 ? { node: parent, offset: index } : { node: parent, offset: index + 1 };
	}

	if (node instanceof HTMLElement) {
		return findComposerDomPoint(node, offset);
	}

	return { node: parent, offset: index };
}

function serializedNodeLength(node) {
	return serializeComposerNode(node).length;
}

function emitComposerValueFromDom() {
	if (isSyncingComposerDom.value) {
		return;
	}

	const editor = composerEl.value;
	if (!editor) {
		return;
	}

	const selection = getComposerSelectionOffsets();
	emit('composer-input', {
		value: serializeComposerEditor(editor),
		selectionStart: selection.start,
		selectionEnd: selection.end,
		target: getComposerApi(),
	});
}

function onComposerKeydownInternal(event) {
	if (props.isChannelMode) {
		event.preventDefault();
		return;
	}

	emit('composer-keydown', event);
}

function onComposerInputInternal() {
	emitComposerValueFromDom();
}

function onComposerPaste(event) {
	event.preventDefault();
	const text = event.clipboardData?.getData('text/plain') || '';
	document.execCommand('insertText', false, text);
}

function getComposerApi() {
	return {
		focus: () => composerEl.value?.focus(),
		get selectionStart() {
			return getComposerSelectionOffset();
		},
		get selectionEnd() {
			return getComposerSelectionOffsets().end;
		},
		get value() {
			return composerEl.value ? serializeComposerEditor(composerEl.value) : '';
		},
		getSelectionOffset: () => getComposerSelectionOffset(),
		setSelectionRange: (start, end = start) => setComposerSelectionRange(start, end),
	};
}

watch(
	() => props.composerValue,
	(value) => renderComposerDomFromValue(value),
);

onMounted(() => {
	document.addEventListener('pointerdown', onDocumentPointerDown);
	renderComposerDomFromValue(props.composerValue);
});

onUnmounted(() => {
	document.removeEventListener('pointerdown', onDocumentPointerDown);
});

defineExpose({
	getComposerEl: () => getComposerApi(),
});
</script>

<template>
	<section class="panel composer-panel">
		<PlanPanel
			v-if="showPlanPanel && planPanel"
			:plan-panel="planPanel"
			:plan-steps="planSteps"
			:collapsed="planPanelCollapsed"
			:collapsed-plan-text="collapsedPlanText"
			@toggle-collapse="emit('toggle-plan-panel-collapse')"
		/>

		<div class="composer-grid">
			<div v-if="commandFeedback" class="command-feedback" :class="commandFeedback.level || 'info'">
				<span class="command-feedback-message">{{ commandFeedback.message }}</span>
				<button
					v-if="commandFeedback.requiresConfirmation"
					class="command-feedback-action"
					type="button"
					@click="emit('confirm-command')"
				>
					Confirm
				</button>
				<button
					class="command-feedback-dismiss"
					type="button"
					aria-label="Dismiss command feedback"
					title="Dismiss"
					@click="emit('clear-command-feedback')"
				>
					×
				</button>
			</div>
			<div class="composer-surface">
				<div class="composer-stack">
					<div
						id="composer"
						ref="composerEl"
						class="composer-box"
						:class="{ empty: !composerValue, disabled: isChannelMode }"
						:contenteditable="isChannelMode ? 'false' : 'true'"
						:data-placeholder="composerPlaceholder"
						role="textbox"
						aria-multiline="true"
						spellcheck="false"
						@input="onComposerInputInternal"
						@keydown="onComposerKeydownInternal"
						@paste="onComposerPaste"
					></div>
					<div v-if="slashPalette.open" class="slash-palette" role="listbox" aria-label="Slash commands">
						<button
							v-for="(item, index) in slashPaletteItems"
							:key="`${item.type}-${item.id}`"
							class="slash-palette-row"
							:class="{ active: index === slashPalette.activeIndex }"
							type="button"
							role="option"
							:aria-selected="index === slashPalette.activeIndex ? 'true' : 'false'"
							@mousedown.prevent="emit('select-slash-item', item)"
						>
							<span class="slash-palette-token">{{ item.type === 'command' ? item.command : item.name }}</span>
							<span class="slash-palette-description">{{ slashItemDescription(item) }}</span>
							<span class="slash-palette-kind">{{ slashItemTypeLabel(item) }}</span>
						</button>
						<div v-if="slashPaletteItems.length === 0" class="slash-palette-empty">No matches</div>
					</div>
					<div v-if="attachments.length > 0" class="composer-attachments" aria-label="待发送图片">
						<div v-for="attachment in attachments" :key="attachment.id" class="composer-attachment">
							<img v-if="attachment.dataUrl" class="composer-attachment-preview" :src="attachment.dataUrl" :alt="attachment.fileName" />
							<div v-else class="composer-attachment-preview empty" aria-hidden="true">
								<svg viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.5">
									<path d="M4.5 15.5h11v-11h-11v11Z"></path>
									<path d="m6.5 13 3-3 2 2 1-1 2 2"></path>
									<circle cx="7.5" cy="7.5" r="1"></circle>
								</svg>
							</div>
							<div class="composer-attachment-meta">
								<span class="composer-attachment-name">{{ attachment.fileName }}</span>
								<span class="composer-attachment-size">{{ formatAttachmentSize(attachment.byteLength) }}</span>
							</div>
							<button
								class="composer-attachment-remove"
								type="button"
								:aria-label="`移除 ${attachment.fileName}`"
								:title="`移除 ${attachment.fileName}`"
								@click="emit('remove-attachment', attachment.id)"
							>
								<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round">
									<path d="M4.5 4.5 11.5 11.5"></path>
									<path d="M11.5 4.5 4.5 11.5"></path>
								</svg>
							</button>
						</div>
					</div>
				</div>
				<div class="composer-footer">
					<div class="composer-controls">
						<div ref="toolsShellEl" class="composer-tools-shell">
							<button
								class="composer-tools-trigger"
								:class="{ active: toolsMenuOpen }"
								type="button"
								:disabled="isChannelMode"
								:aria-expanded="toolsMenuOpen ? 'true' : 'false'"
								aria-haspopup="menu"
								aria-label="展开输入工具"
								title="展开输入工具"
								@click.stop="toggleToolsMenu"
							>
								<svg viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round">
									<path d="M10 4v12"></path>
									<path d="M4 10h12"></path>
								</svg>
							</button>
							<div v-if="toolsMenuOpen" class="composer-tools-menu" role="menu" @click.stop>
								<button class="composer-tools-menu-row" type="button" role="menuitem" @click="requestImagePicker">
									<span class="composer-tools-menu-icon" aria-hidden="true">
										<svg viewBox="0 0 18 18" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">
											<path d="M6.25 9.25 9 6.5a3 3 0 1 1 4.25 4.25l-4 4a4.25 4.25 0 0 1-6-6l5.25-5.25"></path>
										</svg>
									</span>
									<span class="composer-tools-menu-label">添加图片</span>
								</button>
								<button class="composer-tools-menu-row" type="button" role="menuitem" @click="requestScreenshotCapture">
									<span class="composer-tools-menu-icon" aria-hidden="true">
										<svg viewBox="0 0 18 18" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">
											<path d="M5.75 5.25 7 3.5h4l1.25 1.75H15a1.5 1.5 0 0 1 1.5 1.5v6.5a1.5 1.5 0 0 1-1.5 1.5H3a1.5 1.5 0 0 1-1.5-1.5v-6.5A1.5 1.5 0 0 1 3 5.25h2.75Z"></path>
											<circle cx="9" cy="10" r="3"></circle>
											<path d="M13.75 7.25h.01"></path>
										</svg>
									</span>
									<span class="composer-tools-menu-label">添加截图</span>
								</button>
								<label
									v-if="showPlanningToggle"
									class="composer-tools-menu-row composer-tools-menu-toggle"
									:class="{ disabled: isBusy }"
									role="menuitemcheckbox"
									:aria-checked="isPlanningModeEnabled ? 'true' : 'false'"
								>
									<span class="composer-tools-menu-icon" aria-hidden="true">
										<svg viewBox="0 0 18 18" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">
											<path d="M3.25 4.5h6.5"></path>
											<path d="M3.25 9h5"></path>
											<path d="M3.25 13.5h6"></path>
											<path d="m12.25 4.25 2 2-4 4H8.5V8.5l3.75-4.25Z"></path>
										</svg>
									</span>
									<span class="composer-tools-menu-label">计划模式</span>
									<input
										class="toggle-input"
										type="checkbox"
										:checked="isPlanningModeEnabled"
										:disabled="isBusy"
										@change="emit('toggle-planning-mode', $event.target.checked)"
									/>
									<span class="toggle-switch"></span>
								</label>
								<label
									v-if="showVisualizationToggle"
									class="composer-tools-menu-row composer-tools-menu-toggle"
									role="menuitemcheckbox"
									:aria-checked="visualizationEnabled ? 'true' : 'false'"
								>
									<span class="composer-tools-menu-icon" aria-hidden="true">
										<svg viewBox="0 0 18 18" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">
											<circle cx="4" cy="9" r="1.75"></circle>
											<circle cx="14" cy="4" r="1.75"></circle>
											<circle cx="14" cy="14" r="1.75"></circle>
											<path d="M5.6 8.2 12.35 4.8"></path>
											<path d="m5.6 9.8 6.75 3.4"></path>
										</svg>
									</span>
									<span class="composer-tools-menu-label">可视化</span>
									<input
										class="toggle-input"
										type="checkbox"
										:checked="visualizationEnabled"
										@change="emit('toggle-visualization-mode', $event.target.checked)"
									/>
									<span class="toggle-switch"></span>
								</label>
							</div>
						</div>
						<button
							class="composer-tools-trigger composer-reasoning-toggle"
							:class="{ active: isReasoningEnabled }"
							type="button"
							:disabled="isChannelMode || isBusy"
							:aria-pressed="isReasoningEnabled ? 'true' : 'false'"
							:aria-label="isReasoningEnabled ? '关闭思考模式' : '开启思考模式'"
							:title="isReasoningEnabled ? '关闭思考模式' : '开启思考模式'"
							@click="emit('toggle-reasoning-mode', !isReasoningEnabled)"
						>
							<svg viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
								<path d="M7.5 4.25a2.5 2.5 0 0 0-2.5 2.5v.25a2 2 0 0 0-1.75 1.98v.04a2 2 0 0 0 1.61 1.96v.77A2.75 2.75 0 0 0 7.61 14.5h.39v1.5"></path>
								<path d="M12.5 4.25a2.5 2.5 0 0 1 2.5 2.5v.25a2 2 0 0 1 1.75 1.98v.04a2 2 0 0 1-1.61 1.96v.77a2.75 2.75 0 0 1-2.75 2.75H12v1.5"></path>
								<path d="M8.75 8.5h2.5"></path>
								<path d="M8.5 11h3"></path>
								<path v-if="!isReasoningEnabled" d="M4 4l12 12"></path>
							</svg>
						</button>
						<select
							id="composer-profile-select"
							class="composer-inline-select"
							aria-label="当前模型配置"
							:value="selectedProfileId || ''"
							@change="emit('select-profile', $event.target.value)"
						>
							<option value="">选择模型</option>
							<option v-for="option in profiles" :key="option.id" :value="option.id">{{ option.label }}</option>
						</select>					<select
						id="composer-permission-select"
						class="composer-inline-select"
						aria-label="工具权限模式"
						:value="selectedToolPermissionModeId"
						@change="emit('select-permission', $event.target.value)"
					>
						<option v-for="option in toolPermissionModes" :key="option.id" :value="option.id">{{ option.label }}</option>
					</select>
					</div>
					<div class="composer-actions">
						<div
							v-if="contextStats.visible"
							class="composer-context-meter"
							:class="contextStats.levelClass"
							:style="{ '--context-used': contextStats.ringPercent }"
							tabindex="0"
							:aria-label="`背景信息窗口 ${contextStats.percentText} 已用，已用 ${contextStats.usedText} 标记，共 ${contextStats.windowText} 标记`"
						>
							<span class="composer-context-ring" aria-hidden="true"></span>
							<div class="composer-context-popover" role="tooltip">
								<div class="composer-context-popover-title">背景信息窗口:</div>
								<div class="composer-context-popover-percent">{{ contextStats.percentText }} 已用</div>
								<div>已用 {{ contextStats.usedText }} 标记，共 {{ contextStats.windowText }} 标记</div>
								<strong>{{ contextStats.autoCompactText }}</strong>
							</div>
						</div>
						<button
							id="send-button"
						class="send-btn"
						:class="{ loading: isBusy, idle: !isBusy }"
						type="button"
						:disabled="sendButtonDisabled"
						:aria-label="isBusy ? '停止生成' : '发送消息'"
						:title="isBusy ? '停止生成' : '发送消息'"
						@click="emit('send-click')"
					>
						<span v-if="isBusy" class="send-btn-spinner" aria-hidden="true">
							<span class="send-btn-spinner-ring"></span>
							<span class="send-btn-spinner-core"></span>
						</span>
						<span v-else class="send-btn-arrow" aria-hidden="true">
							<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round">
								<path d="M12 19V7"></path>
								<path d="m6 11 6-6 6 6"></path>
							</svg>
						</span>
						</button>
					</div>
				</div>
			</div>
		</div>
	</section>
</template>



