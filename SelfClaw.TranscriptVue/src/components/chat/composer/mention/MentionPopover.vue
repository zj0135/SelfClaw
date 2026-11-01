<script setup>
import { computed, nextTick, ref, watch } from 'vue';
import { AtSign, BookOpenCheck, SquareSlash } from 'lucide-vue-next';
import MentionFileTree from './MentionFileTree.vue';
import MentionSkillList from './MentionSkillList.vue';
import MentionCommandList from './MentionCommandList.vue';
import { createCommandChip, createFileChip, createSkillChip } from '../../../../renderers/composerText.js';

// 快捷弹层的外壳：固定挂在输入框正上方、与输入框同宽；负责键盘导航与活动项，三类内容各自负责数据。
// 行上的 mousedown 被阻止默认行为，点击不会夺走编辑器焦点，选中后光标仍在原位。
const props = defineProps({
	open: { type: Boolean, default: false },
	kind: { type: String, default: '' },
	query: { type: String, default: '' },
	workspaceSelection: { type: Object, default: () => ({}) },
	selectedAgentId: { type: String, default: '' },
	selectedAgentName: { type: String, default: '' },
	capabilityRevision: { type: Number, default: 0 },
	commands: { type: Array, default: () => [] },
});
const emit = defineEmits(['select', 'dismiss']);

const bodyRef = ref(null);
const treeRef = ref(null);
const items = ref([]);
const activeIndex = ref(0);

const glyphs = { file: AtSign, skill: BookOpenCheck, command: SquareSlash };
const titles = { file: '引用文件', skill: '插入技能', command: '指令' };
const glyph = computed(() => glyphs[props.kind] || AtSign);
const title = computed(() => titles[props.kind] || '');
const subtitle = computed(() => {
	if (props.kind === 'file') {
		return props.workspaceSelection?.current?.name || '';
	}

	if (props.kind === 'skill') {
		return props.selectedAgentName || props.selectedAgentId || '';
	}

	return '';
});
const hints = computed(() => (props.kind === 'file'
	? ['↑↓ 移动', '→ 展开', 'Enter 选择', 'Tab 引用目录', 'Esc 关闭']
	: ['↑↓ 移动', 'Enter 选择', 'Esc 关闭']));

function onItems(list) {
	items.value = list;
	if (activeIndex.value >= list.length) {
		activeIndex.value = Math.max(0, list.length - 1);
	}
}

watch(() => [props.kind, props.query], () => {
	activeIndex.value = 0;
});

function toChip(item) {
	if (props.kind === 'file') {
		return createFileChip(item.relativePath, { isDirectory: item.isDirectory });
	}

	if (props.kind === 'skill') {
		return createSkillChip(item.id);
	}

	return createCommandChip(item.name);
}

function choose(item) {
	emit('select', toChip(item));
}

function scrollActiveIntoView() {
	nextTick(() => {
		bodyRef.value?.querySelector(`[data-index="${activeIndex.value}"]`)?.scrollIntoView?.({ block: 'nearest' });
	});
}

// 编辑器在弹层打开时把按键先交到这里；消费了就 preventDefault，编辑器据此不再把 Enter 当发送。
function handleNavigate(event) {
	if (!props.open) {
		return;
	}

	const item = items.value[activeIndex.value];
	switch (event.key) {
		case 'Escape':
			event.preventDefault();
			emit('dismiss');
			return;
		case 'ArrowDown':
		case 'ArrowUp': {
			if (!items.value.length) {
				return;
			}

			event.preventDefault();
			const delta = event.key === 'ArrowDown' ? 1 : -1;
			activeIndex.value = (activeIndex.value + delta + items.value.length) % items.value.length;
			scrollActiveIntoView();
			return;
		}
		case 'Enter':
		case 'Tab':
			// 弹层打开时 Enter 永远不发送：没有可选项就只是吞掉按键。
			event.preventDefault();
			if (!item) {
				return;
			}

			if (props.kind === 'file' && item.isDirectory && !item.flat && event.key === 'Enter') {
				treeRef.value?.toggle(item);
				return;
			}

			choose(item);
			return;
		case 'ArrowRight':
		case 'ArrowLeft':
			// 只有树形模式下的目录才消费左右键；其余情况交给光标移动（光标离开触发区间会自然关闭弹层）。
			if (props.kind !== 'file' || !item?.isDirectory || item.flat) {
				return;
			}

			event.preventDefault();
			if (event.key === 'ArrowRight') {
				treeRef.value?.expand(item);
			} else {
				treeRef.value?.collapse(item);
			}
			return;
		default:
	}
}

defineExpose({ handleNavigate });
</script>

<template>
	<transition name="mention-pop">
		<div v-if="props.open" class="mention-popover" role="dialog" aria-label="快捷选择" @mousedown.prevent>
			<header class="mention-head">
				<span class="mention-glyph" aria-hidden="true">
					<component :is="glyph" :size="13" :stroke-width="2" />
				</span>
				<div class="mention-title">
					<strong>{{ title }}</strong>
					<span v-if="subtitle" class="mention-subtitle" :title="subtitle">{{ subtitle }}</span>
				</div>
				<code v-if="props.query" class="mention-query">{{ props.query }}</code>
			</header>
			<div ref="bodyRef" class="mention-body">
				<MentionFileTree v-if="props.kind === 'file'" ref="treeRef" :query="props.query"
					:workspace-selection="props.workspaceSelection" :active-index="activeIndex"
					@update:items="onItems" @activate="activeIndex = $event" @choose="choose" />
				<MentionSkillList v-else-if="props.kind === 'skill'" :query="props.query" :agent-id="props.selectedAgentId"
					:capability-revision="props.capabilityRevision" :active-index="activeIndex"
					@update:items="onItems" @activate="activeIndex = $event" @choose="choose" />
				<MentionCommandList v-else :query="props.query" :commands="props.commands" :active-index="activeIndex"
					@update:items="onItems" @activate="activeIndex = $event" @choose="choose" />
			</div>
			<footer class="mention-foot">
				<span v-for="hint in hints" :key="hint">{{ hint }}</span>
			</footer>
		</div>
	</transition>
</template>

<style scoped>
/* 固定挂在 .composer-shell 正上方，左右边缘与输入框对齐（不跟随光标）。 */
.mention-popover {
	position: absolute;
	z-index: 60;
	right: 0;
	bottom: calc(100% + 8px);
	left: 0;
	display: flex;
	max-height: min(340px, 48vh);
	flex-direction: column;
	overflow: hidden;
	border: 1px solid var(--border);
	border-radius: 12px;
	background: var(--panel);
	box-shadow:
		0 4px 16px rgba(var(--shadow-ink), 0.08),
		0 18px 44px rgba(var(--shadow-ink), 0.16);
	transform-origin: bottom center;
}

.mention-head {
	display: flex;
	align-items: center;
	gap: 8px;
	min-height: 38px;
	padding: 7px 10px 7px 11px;
	border-bottom: 1px solid var(--border);
	background: color-mix(in srgb, var(--panel) 92%, var(--bg-canvas));
}

.mention-glyph {
	display: grid;
	width: 20px;
	height: 20px;
	flex: none;
	place-items: center;
	border-radius: 6px;
	background: var(--accent-soft);
	color: var(--accent-2);
}

.mention-title {
	display: flex;
	flex: 1;
	min-width: 0;
	flex-direction: column;
}

.mention-title strong {
	color: var(--text-strong);
	font-size: var(--fs-12);
	font-weight: 600;
	line-height: 1.25;
}

.mention-subtitle {
	overflow: hidden;
	color: var(--muted-soft);
	font-size: var(--fs-10);
	text-overflow: ellipsis;
	white-space: nowrap;
}

.mention-query {
	max-width: 40%;
	overflow: hidden;
	padding: 1px 6px;
	border-radius: 5px;
	background: color-mix(in srgb, var(--text) 6%, transparent);
	color: var(--text-soft);
	font-family: var(--font-mono);
	font-size: var(--fs-10);
	text-overflow: ellipsis;
	white-space: nowrap;
}

.mention-body {
	flex: 1 1 auto;
	min-height: 0;
	overflow-y: auto;
	overscroll-behavior: contain;
	padding: 4px;
}

.mention-body::-webkit-scrollbar {
	width: 4px;
}

.mention-body::-webkit-scrollbar-thumb {
	border-radius: 99px;
	background: var(--border-strong);
}

.mention-foot {
	display: flex;
	flex-wrap: wrap;
	gap: 4px 10px;
	padding: 5px 11px 6px;
	border-top: 1px solid var(--border);
	color: var(--muted-soft);
	font-size: var(--fs-10);
}

.mention-pop-enter-active,
.mention-pop-leave-active {
	transition: opacity 0.14s ease, transform 0.14s cubic-bezier(0.16, 1, 0.3, 1);
}

.mention-pop-enter-from,
.mention-pop-leave-to {
	opacity: 0;
	transform: translateY(6px) scale(0.98);
}

@media (prefers-reduced-motion: reduce) {
	.mention-pop-enter-active,
	.mention-pop-leave-active {
		transition: none;
	}
}
</style>
