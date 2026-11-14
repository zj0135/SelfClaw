<script setup>
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue';
import { ArrowDown, ChevronDown, ChevronUp } from 'lucide-vue-next';
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

const SCROLL_BOTTOM_THRESHOLD_PX = 120;
const DOT_STRIDE_PX = 16;
const TRACK_PADDING_PX = 6;
const MAX_TRACK_HEIGHT_PX = 146;

const activeId = ref(null);
const hoveredItem = ref(null);
const bottomHovered = ref(false);
const hoverTop = ref(0);
const showScrollBottom = ref(false);
const trackEl = ref(null);
const trackHeight = ref(MAX_TRACK_HEIGHT_PX);
let currentScrollContainer = null;
let trackResizeObserver = null;

const navItems = computed(() => extractUserNavItems(props.items));
// 单个锚点点列无法表达「上下翻页」，反而挡住正文右侧，所以只有一个用户消息时不渲染点列。
// 「定位到底部」服务于滚动位置而非锚点，必须独立于点列继续可用。
const showTrack = computed(() => navItems.value.length > 1);
const showNav = computed(() => showTrack.value || showScrollBottom.value);
const activeIndex = computed(() => navItems.value.findIndex((item) => item.id === activeId.value));
const trackCapacity = computed(() => Math.max(1, Math.floor((trackHeight.value - TRACK_PADDING_PX) / DOT_STRIDE_PX)));
const visibleRange = computed(() => {
	const total = navItems.value.length;
	const capacity = trackCapacity.value;
	if (total <= capacity) {
		return { start: 0, end: total, capacity, total };
	}

	const index = Math.max(0, activeIndex.value);
	const start = Math.max(0, Math.min(total - capacity, index - Math.floor(capacity / 2)));
	return { start, end: start + capacity, capacity, total };
});
const visibleItems = computed(() => navItems.value.slice(visibleRange.value.start, visibleRange.value.end));
const canScrollUp = computed(() => visibleRange.value.start > 0);
const canScrollDown = computed(() => visibleRange.value.end < visibleRange.value.total);

const popoverText = computed(() => {
	if (hoveredItem.value?.text) {
		return hoveredItem.value.text;
	}
	if (bottomHovered.value) {
		return '定位到底部';
	}
	return '';
});

function onItemHover(event, item) {
	bottomHovered.value = false;
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

function onBottomHover(event) {
	hoveredItem.value = null;
	bottomHovered.value = true;
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
	bottomHovered.value = false;
}

function measureTrack() {
	const nav = trackEl.value?.parentElement;
	const measured = nav ? Math.round(nav.getBoundingClientRect().height) : 0;
	if (measured > 0) {
		trackHeight.value = measured;
	}
}

function checkScrollBottom(container) {
	if (!container) {
		showScrollBottom.value = false;
		return;
	}

	const distanceToBottom = container.scrollHeight - container.scrollTop - container.clientHeight;
	showScrollBottom.value = distanceToBottom > SCROLL_BOTTOM_THRESHOLD_PX;
}

function updateActive() {
	const container = props.getScrollEl ? props.getScrollEl() : null;
	if (!container) {
		showScrollBottom.value = false;
		return;
	}

	checkScrollBottom(container);

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

function scrollToBottom() {
	hoveredItem.value = null;
	bottomHovered.value = false;
	const container = props.getScrollEl ? props.getScrollEl() : null;
	if (!container) {
		return;
	}

	if (typeof container.scrollTo === 'function') {
		container.scrollTo({ top: container.scrollHeight, behavior: 'smooth' });
	} else {
		container.scrollTop = container.scrollHeight;
	}
}

function stepWindow(direction) {
	const total = navItems.value.length;
	const capacity = visibleRange.value.capacity;
	if (total <= capacity) {
		return;
	}

	const nextIndex = Math.max(0, Math.min(total - 1, (activeIndex.value < 0 ? 0 : activeIndex.value) + direction * capacity));
	const target = navItems.value[nextIndex];
	if (target) {
		scrollTo(target.id);
	}
}

function onTrackWheel(event) {
	if (!canScrollUp.value && !canScrollDown.value) {
		return;
	}

	event.preventDefault();
	const direction = Math.sign(event.deltaY);
	if (direction) {
		stepWindow(direction);
	}
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

function observeTrack() {
	trackResizeObserver?.disconnect();
	if (typeof ResizeObserver !== 'function') {
		measureTrack();
		return;
	}

	const nav = trackEl.value?.parentElement;
	if (!nav) {
		return;
	}

	trackResizeObserver = new ResizeObserver(measureTrack);
	trackResizeObserver.observe(nav);
	measureTrack();
}

watch(() => props.items, () => {
	nextTick(() => {
		if (!currentScrollContainer) {
			bindScrollListener();
		} else {
			updateActive();
		}
		// 点列被隐藏时（只剩一个用户消息）不能继续观察已经卸载的 nav 元素，
		// 否则后续再出现第二个用户消息时会沿用失效的 observer。
		if (!trackEl.value) {
			trackResizeObserver?.disconnect();
			trackResizeObserver = null;
			trackHeight.value = MAX_TRACK_HEIGHT_PX;
			return;
		}
		if (!trackResizeObserver) {
			observeTrack();
		} else {
			measureTrack();
		}
	});
}, { deep: true });

onMounted(() => {
	nextTick(() => {
		bindScrollListener();
		observeTrack();
	});
});

onUnmounted(() => {
	unbindScrollListener();
	trackResizeObserver?.disconnect();
});

defineExpose({
	updateActive,
	scrollTo,
	scrollToBottom,
});
</script>

<template>
	<nav v-if="showNav" class="user-message-nav" :class="{ 'nav-bottom-only': navItems.length === 0 }"
		aria-label="用户消息导航">
		<!-- 悬浮弹框：固定最大宽度，超出文本显示省略号 -->
		<div v-if="popoverText" class="nav-popover" :style="{ top: `${hoverTop}px` }"
			role="tooltip">
			{{ popoverText }}
		</div>

		<div v-if="showTrack" ref="trackEl" class="nav-track"
			:aria-label="`用户消息导航，共 ${visibleRange.total} 轮，当前显示第 ${visibleRange.start + 1} 到 ${visibleRange.end} 轮`"
			@wheel="onTrackWheel">
			<button v-if="canScrollUp" type="button" class="nav-step" aria-label="查看更早的用户消息"
				@click="stepWindow(-1)">
				<ChevronUp :size="12" :stroke-width="2" aria-hidden="true" />
			</button>
			<button v-for="(item, index) in visibleItems" :key="item.id" type="button" class="nav-item"
				:class="{ active: item.id === activeId }" :aria-label="`跳转到第 ${visibleRange.start + index + 1} 条用户消息`"
				:aria-current="item.id === activeId ? 'true' : undefined"
				@mouseenter="onItemHover($event, item)" @mouseleave="onItemLeave"
				@focus="onItemHover($event, item)" @blur="onItemLeave"
				@click="scrollTo(item.id)">
				<span class="nav-indicator" aria-hidden="true"></span>
			</button>
			<button v-if="canScrollDown" type="button" class="nav-step" aria-label="查看更晚的用户消息"
				@click="stepWindow(1)">
				<ChevronDown :size="12" :stroke-width="2" aria-hidden="true" />
			</button>
		</div>

		<Transition name="nav-bottom-fade">
			<button v-if="showScrollBottom" type="button" class="nav-bottom-btn" aria-label="定位到底部"
				@mouseenter="onBottomHover($event)" @mouseleave="onItemLeave"
				@focus="onBottomHover($event)" @blur="onItemLeave"
				@click="scrollToBottom">
				<ArrowDown :size="14" :stroke-width="2" aria-hidden="true" />
			</button>
		</Transition>
	</nav>
</template>

<style scoped>
.user-message-nav {
	position: absolute;
	right: 28px;
	top: 50%;
	transform: translateY(-50%);
	z-index: 8;
	pointer-events: auto;
	display: flex;
	flex-direction: column;
	align-items: center;
	/* 点列的固定高度是活跃窗口的容量来源；长会话不再靠内部滚动把 40+ 个点挤进轨道。 */
	height: min(146px, max(0px, 100% - 160px));
	/* 宽度必须自己钉住而不是由点列撑开：点列被隐藏时（只剩一个锚点）宽度塌成 0，
	   会让 left: 50% 的「定位到底部」按钮水平偏移半列。 */
	width: 20px;
}

.nav-popover {
	position: absolute;
	right: calc(100% + 10px);
	transform: translateY(-50%);
	width: max-content;
	max-width: 240px;
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
	align-items: center;
	justify-content: center;
	gap: 2px;
	width: 20px;
	height: 100%;
	min-height: 0;
	padding: 3px 0;
}

.nav-item,
.nav-step {
	position: relative;
	display: flex;
	align-items: center;
	justify-content: center;
	flex: 0 0 auto;
	width: 14px;
	height: 14px;
	background: transparent;
	border: none;
	padding: 0;
	cursor: pointer;
	outline: none;
	border-radius: 50%;
	-webkit-tap-highlight-color: transparent;
}

.nav-step {
	color: var(--muted-soft);
	opacity: 0.75;
	transition: color 0.18s ease, opacity 0.18s ease;
}

.nav-step:hover {
	color: var(--text);
	opacity: 1;
}

.nav-indicator {
	display: block;
	box-sizing: border-box;
	width: 7px;
	height: 7px;
	border: 1.5px solid var(--line-solid-strong);
	border-radius: 50%;
	background: transparent;
	opacity: 0.75;
	transition: border-color 0.18s ease, background-color 0.18s ease, opacity 0.18s ease, transform 0.18s var(--ease-out);
}

.nav-item:hover .nav-indicator {
	border-color: var(--text);
	background: transparent;
	opacity: 1;
	transform: scale(1.2);
}

.nav-item.active .nav-indicator {
	border-color: var(--accent-base);
	background: var(--accent-base);
	opacity: 1;
	transform: scale(1.15);
}

.nav-item.active:hover .nav-indicator {
	transform: scale(1.25);
}

.nav-item:focus-visible,
.nav-step:focus-visible {
	outline: 2px solid var(--accent-base);
	outline-offset: 2px;
}

/* 导航到底部按钮：居中挂在锚点轴正下方，出现/隐藏不改变轨道高度。 */
.nav-bottom-btn {
	position: absolute;
	top: 100%;
	left: 50%;
	margin-top: 8px;
	display: inline-flex;
	align-items: center;
	justify-content: center;
	width: 24px;
	height: 24px;
	padding: 0;
	border: 1px solid var(--line-card);
	border-radius: 50%;
	background: var(--surface-base);
	box-shadow: 0 3px 10px rgba(var(--shadow-ink), 0.1);
	color: var(--muted);
	cursor: pointer;
	outline: none;
	flex-shrink: 0;
	transform: translateX(-50%);
	transition: color 0.18s ease, border-color 0.18s ease, box-shadow 0.18s ease, transform 0.18s var(--ease-out);
}

/* 完全没有锚点时（纯「定位到底部」）按钮升到锚点轴中央。只有一个锚点时点列虽被隐藏，
   按钮仍留在轨道正下方的原位——隐藏点列不应改变按钮坐标。 */
.user-message-nav.nav-bottom-only .nav-bottom-btn {
	top: 50%;
	margin-top: 0;
	transform: translate(-50%, -50%);
}

.user-message-nav.nav-bottom-only .nav-bottom-btn:hover {
	transform: translate(-50%, -50%) translateY(2px);
}

.nav-bottom-btn:hover {
	color: var(--text);
	border-color: var(--line-solid-strong);
	box-shadow: 0 4px 14px rgba(var(--shadow-ink), 0.16);
	transform: translateX(-50%) translateY(2px);
}

.nav-bottom-btn:focus-visible {
	outline: 2px solid var(--accent-base);
	outline-offset: 2px;
}

.nav-bottom-fade-enter-active,
.nav-bottom-fade-leave-active {
	transition: opacity 0.16s ease, transform 0.16s var(--ease-out);
}

.nav-bottom-fade-enter-from,
.nav-bottom-fade-leave-to {
	opacity: 0;
	transform: translateX(-50%) translateY(-4px);
}

@media (max-width: 640px) {
	.user-message-nav {
		display: none;
	}
}
</style>
