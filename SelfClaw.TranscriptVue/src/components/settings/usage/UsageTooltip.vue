<script setup>
import { computed, nextTick, ref, watch } from 'vue';

const props = defineProps({
	x: { type: Number, required: true },
	y: { type: Number, required: true },
	title: { type: String, default: '' },
	rows: { type: Array, default: () => [] },
});

const OFFSET = 14;
const VIEWPORT_MARGIN = 8;

const element = ref(null);
const size = ref({ width: 0, height: 0 });

// 浮层尺寸只有渲染后才知道，所以先测再决定翻转方向：贴右下角时不翻就会出窗。
watch([() => props.x, () => props.y, () => props.rows], async () => {
	await nextTick();
	const rect = element.value?.getBoundingClientRect?.();
	if (rect) size.value = { width: rect.width, height: rect.height };
}, { immediate: true });

const style = computed(() => {
	const viewportWidth = typeof window === 'undefined' ? 0 : window.innerWidth;
	const viewportHeight = typeof window === 'undefined' ? 0 : window.innerHeight;
	const left = props.x + OFFSET + size.value.width > viewportWidth
		? props.x - size.value.width - OFFSET
		: props.x + OFFSET;
	const top = props.y + OFFSET + size.value.height > viewportHeight
		? props.y - size.value.height - OFFSET
		: props.y + OFFSET;
	return {
		left: `${Math.max(VIEWPORT_MARGIN, left)}px`,
		top: `${Math.max(VIEWPORT_MARGIN, top)}px`,
	};
});
</script>

<template>
	<!-- fixed 定位的浮层必须离开 .sc-rise 的 transform 容器，否则坐标会以卡片为参照而不是窗口。 -->
	<Teleport to="body">
		<div ref="element" class="usage-tooltip" role="tooltip" :style="style">
			<p v-if="title" class="tooltip-title">{{ title }}</p>
			<ul v-if="rows.length" class="tooltip-rows">
				<li v-for="row in rows" :key="row.label">
					<span class="tooltip-label">{{ row.label }}</span>
					<span class="tooltip-value">{{ row.value }}</span>
				</li>
			</ul>
		</div>
	</Teleport>
</template>

<style scoped>
@import '../../../styles/usage-dashboard.css';

.usage-tooltip {
	position: fixed;
	z-index: 40;
	min-width: 108px;
	max-width: 240px;
	padding: 8px 10px;
	border: 1px solid var(--sc-line-2);
	border-radius: 8px;
	background: var(--sc-panel);
	box-shadow: var(--shadow);
	pointer-events: none;
}

.tooltip-title {
	margin: 0 0 6px;
	color: var(--sc-text);
	font-family: var(--sc-mono);
	font-size: var(--fs-10);
	font-weight: 650;
	letter-spacing: 0.02em;
}

.tooltip-rows {
	display: flex;
	flex-direction: column;
	gap: 3px;
	margin: 0;
	padding: 0;
	list-style: none;
}

.tooltip-rows li {
	display: flex;
	align-items: baseline;
	justify-content: space-between;
	gap: 12px;
}

.tooltip-label {
	overflow: hidden;
	color: var(--sc-mute);
	font-size: var(--fs-10);
	text-overflow: ellipsis;
	white-space: nowrap;
}

.tooltip-value {
	color: var(--sc-text);
	font-family: var(--sc-mono);
	font-size: var(--fs-10);
	font-variant-numeric: tabular-nums;
}
</style>
