<script setup>
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue';
import { Check, ChevronDown, ShieldAlert, ShieldCheck } from 'lucide-vue-next';

const props = defineProps({
	mode: {
		type: String,
		default: 'require-approval',
	},
	align: {
		type: String,
		default: 'start',
	},
	placement: {
		type: String,
		default: 'up',
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

const current = computed(() =>
	MODES.find((mode) => mode.id === props.mode) ?? MODES[0],
);

function toggle() {
	open.value = !open.value;
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
		<!-- 底部状态栏按钮：图标 + 文本 + 微箭头 -->
		<button
			class="status-perm-btn"
			:class="`status-perm-btn--${current.id}`"
			type="button"
			:aria-expanded="open ? 'true' : 'false'"
			aria-haspopup="menu"
			:title="`工具权限：${current.label}`"
			@click.stop="toggle"
		>
			<component :is="current.icon" :size="13" :stroke-width="2" class="status-perm-icon" aria-hidden="true" />
			<span class="status-perm-label">{{ current.label }}</span>
			<ChevronDown :size="11" class="status-perm-caret" aria-hidden="true" />
		</button>

		<!-- 极简精炼菜单：图二风格设计 -->
		<div
			v-show="open"
			ref="menuRef"
			class="perm-menu"
			:class="[`perm-menu--${placement}`, `perm-menu--${align}`]"
			role="listbox"
			aria-label="工具权限"
		>
			<button
				v-for="mode in MODES"
				:key="mode.id"
				type="button"
				class="perm-opt"
				role="option"
				:aria-selected="current.id === mode.id ? 'true' : 'false'"
				@click="pick(mode.id)"
			>
				<span class="perm-opt-icon" :class="`perm-opt-icon--${mode.id}`" aria-hidden="true">
					<component :is="mode.icon" :size="15" :stroke-width="1.9" />
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
	align-items: center;
}

/* ===== 底部状态栏触发按钮：图标 + 文本（对标模型选择器透明无框质感） ===== */
.status-perm-btn {
	display: inline-flex;
	align-items: center;
	gap: 5px;
	height: 28px;
	padding: 0 8px;
	border: 0;
	border-radius: 8px;
	background: transparent;
	color: var(--text-soft);
	font-size: var(--fs-125);
	font-weight: 500;
	letter-spacing: 0.01em;
	cursor: pointer;
	transition: background 0.15s, color 0.15s;
	outline: none;
	user-select: none;
	white-space: nowrap;
}

.status-perm-btn:hover,
.status-perm-btn[aria-expanded='true'] {
	background: var(--panel-muted);
	color: var(--text-strong);
}

.status-perm-icon {
	flex: none;
}

.status-perm-btn--require-approval .status-perm-icon {
	color: var(--caution-fill);
}

.status-perm-btn--full-access .status-perm-icon {
	color: var(--accent);
}

.status-perm-label {
	color: inherit;
}

.status-perm-caret {
	color: var(--muted);
	flex: none;
	transition: transform 0.16s ease, color 0.15s ease;
}

.status-perm-btn:hover .status-perm-caret,
.status-perm-btn[aria-expanded='true'] .status-perm-caret {
	color: var(--text-strong);
}

.status-perm-btn[aria-expanded='true'] .status-perm-caret {
	transform: rotate(180deg);
}

/* ===== 下拉弹出面板（严格对标图二简洁卡片） ===== */
.perm-menu {
	position: absolute;
	left: 0;
	width: 252px;
	padding: 5px;
	border: 1px solid var(--border);
	border-radius: 12px;
	background: var(--panel);
	box-shadow:
		0 4px 16px rgba(var(--shadow-ink), 0.08),
		0 16px 36px rgba(var(--shadow-ink), 0.16);
	z-index: 90;
}

.perm-menu--down {
	top: calc(100% + 7px);
	transform-origin: top left;
	animation: menu-in-down 0.16s cubic-bezier(0.16, 1, 0.3, 1);
}

.perm-menu--up {
	bottom: calc(100% + 7px);
	transform-origin: bottom left;
	animation: menu-in-up 0.16s cubic-bezier(0.16, 1, 0.3, 1);
}

.perm-menu--end {
	left: auto;
	right: 0;
}

.perm-menu--end.perm-menu--down {
	transform-origin: top right;
}

.perm-menu--end.perm-menu--up {
	transform-origin: bottom right;
}

@keyframes menu-in-down {
	from {
		opacity: 0;
		transform: translateY(-5px) scale(0.98);
	}
	to {
		opacity: 1;
		transform: translateY(0) scale(1);
	}
}

@keyframes menu-in-up {
	from {
		opacity: 0;
		transform: translateY(5px) scale(0.98);
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

/* 列表项：对标图二细致排版 */
.perm-opt {
	display: flex;
	align-items: center;
	gap: 10px;
	width: 100%;
	padding: 8px 10px;
	border: 0;
	border-radius: 8px;
	background: transparent;
	color: var(--text);
	text-align: left;
	cursor: pointer;
	transition: background 0.12s ease;
}

.perm-opt:hover {
	background: var(--panel-soft);
}

.perm-opt[aria-selected='true'] {
	background: var(--panel-soft);
}

.perm-opt-icon {
	display: inline-flex;
	align-items: center;
	justify-content: center;
	color: var(--muted);
	flex: none;
}

.perm-opt-icon--require-approval {
	color: var(--caution-fill);
}

.perm-opt-icon--full-access {
	color: var(--accent);
}

.perm-opt-copy {
	display: grid;
	gap: 2px;
	min-width: 0;
	flex: 1;
}

.perm-opt-copy strong {
	font-size: 13px;
	font-weight: 550;
	color: var(--text);
	line-height: 1.25;
}

.perm-opt-copy small {
	color: var(--muted);
	font-size: 11px;
	line-height: 1.35;
}

.perm-opt-check {
	width: 14px;
	height: 14px;
	color: var(--text-strong);
	flex: none;
	display: none;
}

.perm-opt[aria-selected='true'] .perm-opt-check {
	display: block;
}
</style>
