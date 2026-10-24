<script setup>
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue';
import { extractUserNavItems } from '../../../renderers/transcript.js';

const props = defineProps({
	items: {
		type: Array,
		default: () => [],
	},
	getScrollEl: {
		type: Function,
		default: null,
	},
});

const activeId = ref(null);
const hoveredItem = ref(null);
const hoverTop = ref(0);
let currentScrollContainer = null;

const navItems = computed(() => extractUserNavItems(props.items));

function onItemHover(event, item) {
	hoveredItem.value = item;
	const btn = event?.currentTarget;
	if (!btn) {
		return;
	}

	const navEl = btn.closest('.user-message-nav');
	if (navEl) {
		const navRect = navEl.getBoundingClientRect();
		const btnRect = btn.getBoundingClientRect();
		hoverTop.value = btnRect.top - navRect.top + btnRect.height / 2;
	} else {
		hoverTop.value = btn.offsetTop + btn.offsetHeight / 2;
	}
}

function onItemLeave() {
	hoveredItem.value = null;
}

function updateActive() {
	const container = props.getScrollEl ? props.getScrollEl() : null;
	if (!container) {
		return;
	}

	const userRows = container.querySelectorAll('.message-row.user');
	if (userRows.length === 0) {
		activeId.value = null;
		return;
	}

	const containerRect = container.getBoundingClientRect();
	let candidateId = null;

	for (const row of userRows) {
		const rect = row.getBoundingClientRect();
		if (rect.top <= containerRect.top + containerRect.height * 0.45) {
			candidateId = row.dataset.messageId;
		}
	}

	activeId.value = candidateId || userRows[0]?.dataset.messageId || null;
}

function scrollTo(messageId) {
	const container = props.getScrollEl ? props.getScrollEl() : null;
	if (!container) {
		return;
	}

	const targetEl = container.querySelector(`[data-message-id="${messageId}"]`);
	if (!targetEl) {
		return;
	}

	targetEl.scrollIntoView({ behavior: 'smooth', block: 'center' });
	activeId.value = messageId;
}

function bindScrollListener() {
	unbindScrollListener();
	const container = props.getScrollEl ? props.getScrollEl() : null;
	if (container) {
		currentScrollContainer = container;
		currentScrollContainer.addEventListener('scroll', updateActive, { passive: true });
		updateActive();
	}
}

function unbindScrollListener() {
	if (currentScrollContainer) {
		currentScrollContainer.removeEventListener('scroll', updateActive);
		currentScrollContainer = null;
	}
}

watch(() => props.items, () => {
	nextTick(() => {
		if (!currentScrollContainer) {
			bindScrollListener();
		} else {
			updateActive();
		}
	});
}, { deep: true });

onMounted(() => {
	nextTick(() => bindScrollListener());
});

onUnmounted(() => {
	unbindScrollListener();
});

defineExpose({
	updateActive,
	scrollTo,
});
</script>

<template>
	<nav v-if="navItems.length > 0" class="user-message-nav" aria-label="用户消息导航">
		<!-- 悬浮弹框：固定宽度，超出文本显示省略号 -->
		<div v-if="hoveredItem && hoveredItem.text" class="nav-popover" :style="{ top: `${hoverTop}px` }"
			role="tooltip">
			{{ hoveredItem.text }}
		</div>

		<div class="nav-track">
			<button v-for="(item, index) in navItems" :key="item.id" type="button" class="nav-item"
				:class="{ active: item.id === activeId }" :aria-label="`跳转到第 ${index + 1} 条用户消息`"
				@mouseenter="onItemHover($event, item)" @mouseleave="onItemLeave"
				@focus="onItemHover($event, item)" @blur="onItemLeave"
				@click="scrollTo(item.id)">
				<span class="nav-indicator" aria-hidden="true"></span>
			</button>
		</div>
	</nav>
</template>

<style scoped>
.user-message-nav {
	position: absolute;
	right: 12px;
	top: 50%;
	transform: translateY(-50%);
	z-index: 8;
	pointer-events: auto;
	display: flex;
	flex-direction: column;
	align-items: flex-end;
	max-height: calc(100% - 96px);
}

.nav-popover {
	position: absolute;
	right: calc(100% + 10px);
	transform: translateY(-50%);
	width: 240px;
	box-sizing: border-box;
	white-space: nowrap;
	overflow: hidden;
	text-overflow: ellipsis;
	background: var(--surface-base);
	color: var(--text);
	border: 1px solid var(--line-card);
	border-radius: 6px;
	padding: 6px 10px;
	font-size: var(--fs-12);
	font-family: inherit;
	line-height: 1.4;
	box-shadow: 0 4px 18px rgba(var(--shadow-ink), 0.16);
	pointer-events: none;
	user-select: none;
	z-index: 20;
	animation: popover-in 0.15s cubic-bezier(0.22, 1, 0.36, 1);
}

@keyframes popover-in {
	from {
		opacity: 0;
		transform: translateY(-50%) translateX(4px);
	}
	to {
		opacity: 1;
		transform: translateY(-50%) translateX(0);
	}
}

.nav-track {
	display: flex;
	flex-direction: column;
	align-items: flex-end;
	gap: 8px;
	overflow-y: auto;
	overflow-x: visible;
	scrollbar-width: none;
	padding: 6px 4px;
}

.nav-track::-webkit-scrollbar {
	display: none;
}

.nav-item {
	position: relative;
	display: flex;
	align-items: center;
	justify-content: flex-end;
	background: transparent;
	border: none;
	padding: 6px 4px;
	cursor: pointer;
	outline: none;
	border-radius: 4px;
	-webkit-tap-highlight-color: transparent;
}

.nav-indicator {
	display: block;
	width: 14px;
	height: 2.5px;
	border-radius: 9999px;
	background: var(--line-solid-strong);
	opacity: 0.65;
	transition: width 0.22s var(--ease-out), background-color 0.2s ease, opacity 0.2s ease, box-shadow 0.2s ease;
}

.nav-item:hover .nav-indicator {
	width: 22px;
	background: var(--text);
	opacity: 1;
	box-shadow: 0 0 8px rgba(var(--shadow-ink), 0.25);
}

.nav-item.active .nav-indicator {
	width: 20px;
	background: var(--accent-base);
	opacity: 1;
	box-shadow: 0 0 6px rgba(var(--shadow-ink), 0.25);
}

.nav-item:focus-visible {
	outline: 2px solid var(--accent-base);
	outline-offset: 2px;
}

@media (max-width: 640px) {
	.user-message-nav {
		display: none;
	}
}
</style>
