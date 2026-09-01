<script setup>
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue';
import { Check, ShieldAlert, ShieldCheck } from 'lucide-vue-next';

const props = defineProps({
	mode: {
		type: String,
		default: 'require-approval',
	},
});
const emit = defineEmits(['select']);

const MODES = [
	{
		id: 'require-approval',
		label: '需确认',
		icon: ShieldAlert,
		hint: '写入、Shell 与 MCP 工具调用前逐一确认',
	},
	{
		id: 'full-access',
		label: '自动允许',
		icon: ShieldCheck,
		hint: '自动执行所有工具调用，无需逐一确认',
	},
];

const open = ref(false);
const rootRef = ref(null);
const menuRef = ref(null);
const placement = ref('down');
const estimatedMenuHeight = 168;

const current = computed(() =>
	MODES.find((mode) => mode.id === props.mode) ?? MODES[0],
);

function updatePlacement() {
	const rect = rootRef.value?.getBoundingClientRect();
	if (!rect) {
		return;
	}

	const menuHeight = menuRef.value?.offsetHeight || estimatedMenuHeight;
	const spaceBelow = window.innerHeight - rect.bottom;
	const spaceAbove = rect.top;
	placement.value = spaceBelow < menuHeight + 16 && spaceAbove > spaceBelow ? 'up' : 'down';
}

function toggle() {
	open.value = !open.value;
	if (!open.value) {
		return;
	}

	updatePlacement();
	nextTick(updatePlacement);
}

function close() {
	open.value = false;
}

function pick(modeId) {
	if (modeId !== props.mode) {
		emit('select', modeId);
	}
	close();
}

function onDocClick(event) {
	if (rootRef.value && !rootRef.value.contains(event.target)) {
		close();
	}
}

function onKeydown(event) {
	if (event.key === 'Escape') {
		close();
	}
}

onMounted(() => {
	document.addEventListener('click', onDocClick);
	document.addEventListener('keydown', onKeydown);
});
onBeforeUnmount(() => {
	document.removeEventListener('click', onDocClick);
	document.removeEventListener('keydown', onKeydown);
});
</script>

<template>
	<div ref="rootRef" class="perm-wrap">
		<button class="icon-btn" type="button" :aria-expanded="open ? 'true' : 'false'" aria-haspopup="menu"
			:title="`工具权限：${current.label}`" @click.stop="toggle">
			<component :is="current.icon" :size="16" :stroke-width="1.8" aria-hidden="true" />
		</button>

		<div v-show="open" ref="menuRef" class="perm-menu" :class="`perm-menu--${placement}`" role="listbox"
			aria-label="工具权限">
			<button v-for="mode in MODES" :key="mode.id" type="button" class="perm-opt" role="option"
				:aria-selected="current.id === mode.id ? 'true' : 'false'" @click="pick(mode.id)">
				<span class="perm-opt-icon" :class="`perm-opt-icon--${mode.id}`" aria-hidden="true">
					<component :is="mode.icon" :size="14" :stroke-width="2" />
				</span>
				<span class="perm-opt-copy">
					<strong>{{ mode.label }}</strong>
					<small>{{ mode.hint }}</small>
				</span>
				<Check class="perm-opt-check" :size="14" :stroke-width="2.4" aria-hidden="true" />
			</button>
		</div>
	</div>
</template>

<style scoped>
.perm-wrap {
	position: relative;
	display: inline-flex;
}

/* ===== 图标触发按钮（与 composer icon-btn 一致） ===== */
.icon-btn {
	display: inline-flex;
	align-items: center;
	justify-content: center;
	width: 32px;
	height: 32px;
	padding: 0;
	border: 0;
	border-radius: 8px;
	background: transparent;
	color: var(--muted);
	cursor: pointer;
	transition: background 0.15s, color 0.15s;
}

.icon-btn:hover {
	background: var(--panel-muted);
	color: var(--text);
}

.icon-btn[aria-expanded='true'] {
	background: var(--panel-muted);
	color: var(--text);
}

/* ===== 下拉菜单 ===== */
.perm-menu {
	position: absolute;
	left: 0;
	width: 248px;
	padding: 4px;
	border: 1px solid var(--border);
	border-radius: 10px;
	background: var(--panel);
	box-shadow: 0 1px 2px rgba(var(--shadow-ink), 0.05), 0 12px 32px rgba(var(--shadow-ink), 0.12);
	z-index: 40;
}

.perm-menu--down {
	top: calc(100% + 6px);
	transform-origin: top left;
	animation: menu-in-down 0.16s cubic-bezier(0.16, 1, 0.3, 1);
}

.perm-menu--up {
	bottom: calc(100% + 6px);
	transform-origin: bottom left;
	animation: menu-in-up 0.16s cubic-bezier(0.16, 1, 0.3, 1);
}

@keyframes menu-in-down {
	from {
		opacity: 0;
		transform: translateY(-6px) scale(0.98);
	}

	to {
		opacity: 1;
		transform: translateY(0) scale(1);
	}
}

@keyframes menu-in-up {
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

	.perm-menu--down,
	.perm-menu--up {
		animation: none;
	}
}

.perm-opt {
	display: flex;
	align-items: center;
	gap: 10px;
	width: 100%;
	padding: 8px 9px;
	border: 0;
	border-radius: 6px;
	background: transparent;
	color: var(--text);
	text-align: left;
	cursor: pointer;
	transition: background 0.15s;
}

.perm-opt:hover {
	background: var(--bg-canvas);
}

.perm-opt[aria-selected='true'] {
	background: var(--accent-soft, color-mix(in srgb, var(--accent) 8%, transparent));
}

.perm-opt-icon {
	display: inline-grid;
	place-items: center;
	width: 22px;
	height: 22px;
	border-radius: 6px;
	flex: none;
}

.perm-opt-icon svg {
	width: 14px;
	height: 14px;
}

.perm-opt-icon--require-approval {
	background: var(--caution-icon-bg);
	color: var(--caution-fill);
}

.perm-opt-icon--full-access {
	background: color-mix(in srgb, var(--success) 14%, transparent);
	color: var(--success);
}

.perm-opt-copy {
	display: grid;
	gap: 2px;
	min-width: 0;
	flex: 1;
}

.perm-opt-copy strong {
	font: inherit;
	font-weight: 600;
	font-size: var(--fs-125);
}

.perm-opt-copy small {
	overflow: hidden;
	color: var(--muted-soft);
	font-size: var(--fs-105);
	font-weight: 500;
	line-height: 1.35;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.perm-opt-check {
	width: 14px;
	height: 14px;
	color: var(--accent);
	flex: none;
	display: none;
}

.perm-opt[aria-selected='true'] .perm-opt-check {
	display: block;
}
</style>
