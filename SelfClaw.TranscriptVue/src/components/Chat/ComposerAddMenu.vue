<script setup>
import { onBeforeUnmount, onMounted, ref } from 'vue';
import { BookOpenCheck, Plus, SlidersHorizontal } from 'lucide-vue-next';
import SkillPicker from './SkillPicker.vue';

// 输入框工具栏最左侧的「+」入口：把技能、功能等低频操作收进一个菜单，
// 让工具栏只保留「+ / 代理 / 模型」三个无框入口。
const props = defineProps({
	// 仅 Direct 模式下可插入技能
	skillsEnabled: { type: Boolean, default: false },
	agentId: { type: String, default: '' },
	agentName: { type: String, default: '' },
	capabilityRevision: { type: Number, default: 0 },
});
const emit = defineEmits(['select-skill']);

const open = ref(false);
const rootRef = ref(null);
const skillPickerRef = ref(null);

function toggle() {
	open.value = !open.value;
}

function close() {
	open.value = false;
}

function openSkills() {
	close();
	skillPickerRef.value?.openPicker();
}

function onDocumentPointerDown(event) {
	if (open.value && !rootRef.value?.contains(event.target)) close();
}

function onKeydown(event) {
	if (event.key === 'Escape') close();
}

onMounted(() => {
	document.addEventListener('pointerdown', onDocumentPointerDown);
	document.addEventListener('keydown', onKeydown);
});
onBeforeUnmount(() => {
	document.removeEventListener('pointerdown', onDocumentPointerDown);
	document.removeEventListener('keydown', onKeydown);
});
</script>

<template>
	<div ref="rootRef" class="add-wrap">
		<button class="add-trigger" type="button" title="添加" aria-label="添加" aria-haspopup="menu"
			:aria-expanded="open ? 'true' : 'false'" @click="toggle">
			<Plus :size="17" :stroke-width="1.9" aria-hidden="true" />
		</button>

		<div v-show="open" class="add-menu" role="menu" aria-label="添加">
			<button v-if="props.skillsEnabled" class="add-item" type="button" role="menuitem" @click="openSkills">
				<BookOpenCheck :size="15" :stroke-width="1.8" aria-hidden="true" />
				插入技能
			</button>
			<button class="add-item" type="button" role="menuitem" @click="close">
				<SlidersHorizontal :size="15" :stroke-width="1.8" aria-hidden="true" />
				功能设置
			</button>
		</div>

		<SkillPicker v-if="props.skillsEnabled" ref="skillPickerRef" :show-trigger="false" :agent-id="agentId"
			:agent-name="agentName" :capability-revision="capabilityRevision" @select="emit('select-skill', $event)" />
	</div>
</template>

<style scoped>
.add-wrap {
	position: relative;
	display: inline-flex;
}

.add-trigger {
	display: inline-flex;
	align-items: center;
	justify-content: center;
	width: 30px;
	height: 30px;
	padding: 0;
	border: 0;
	border-radius: 8px;
	background: transparent;
	color: var(--muted);
	cursor: pointer;
	transition: background 0.15s, color 0.15s;
}

.add-trigger:hover,
.add-trigger[aria-expanded='true'] {
	background: var(--panel-muted);
	color: var(--text);
}

.add-menu {
	position: absolute;
	left: 0;
	bottom: calc(100% + 6px);
	width: 188px;
	padding: 4px;
	border: 1px solid var(--border);
	border-radius: 10px;
	background: var(--panel);
	box-shadow: 0 1px 2px rgba(var(--shadow-ink), 0.05), 0 12px 32px rgba(var(--shadow-ink), 0.12);
	z-index: 40;
	transform-origin: bottom left;
	animation: add-menu-in 0.16s cubic-bezier(0.16, 1, 0.3, 1);
}

@keyframes add-menu-in {
	from {
		opacity: 0;
		transform: translateY(6px) scale(0.98);
	}

	to {
		opacity: 1;
		transform: translateY(0) scale(1);
	}
}

@media (prefers-reduced-motion: reduce) {
	.add-menu {
		animation: none;
	}
}

.add-item {
	display: flex;
	align-items: center;
	gap: 9px;
	width: 100%;
	height: 32px;
	padding: 0 8px;
	border: 0;
	border-radius: 7px;
	background: transparent;
	color: var(--text);
	font-size: var(--fs-125);
	font-weight: 500;
	text-align: left;
	cursor: pointer;
	transition: background 0.12s;
}

.add-item:hover {
	background: var(--panel-soft);
}

.add-item svg {
	color: var(--muted);
	flex: none;
}
</style>
