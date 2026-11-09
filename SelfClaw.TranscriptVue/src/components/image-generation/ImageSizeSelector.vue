<script setup>
import { Square, RectangleHorizontal, RectangleVertical, Check } from 'lucide-vue-next';

defineProps({
	// 宿主按模型能力回传的尺寸清单，形如 { value, ratio, orientation, desc }。
	sizes: { type: Array, required: true },
	selected: { type: String, default: '' },
});

const emit = defineEmits(['select-size']);

const iconMap = {
	square: Square,
	landscape: RectangleHorizontal,
	portrait: RectangleVertical,
};
</script>

<template>
	<div class="size-selector-block">
		<label class="section-label">图片尺寸</label>

		<div class="sizes-grid" :style="{ '--columns': Math.min(sizes.length || 1, 4) }">
			<div
				v-for="item in sizes"
				:key="item.value"
				class="size-card"
				:class="{ active: item.value === selected }"
				role="button"
				:aria-pressed="item.value === selected"
				tabindex="0"
				@click="emit('select-size', item.value)"
				@keydown.enter.prevent="emit('select-size', item.value)"
				@keydown.space.prevent="emit('select-size', item.value)"
			>
				<div v-if="item.value === selected" class="active-badge">
					<Check :size="10" stroke-width="3" />
				</div>

				<div class="size-icon-shell">
					<component :is="iconMap[item.orientation] || Square" :size="20" class="size-icon" />
				</div>

				<div class="size-label">{{ item.ratio }}</div>
				<div class="size-desc">{{ item.desc }}</div>
			</div>
		</div>
	</div>
</template>

<style scoped>
.size-selector-block {
	display: flex;
	flex-direction: column;
	gap: 8px;
	width: 100%;
}

.section-label {
	font-size: 13px;
	font-weight: 600;
	color: var(--ink-1, #171a1f);
}

.sizes-grid {
	display: grid;
	grid-template-columns: repeat(var(--columns, 4), minmax(0, 1fr));
	gap: 10px;
}

.size-card {
	position: relative;
	display: flex;
	flex-direction: column;
	align-items: center;
	justify-content: center;
	padding: 12px 6px 10px;
	background: var(--surface-base, #ffffff);
	border: 1px solid var(--line-card, #e3e6ec);
	border-radius: 12px;
	cursor: pointer;
	transition: all 0.15s ease;
	text-align: center;
}

.size-card:hover {
	border-color: var(--line-solid-strong, #d8dde5);
	background: var(--surface-soft, #f7f8fa);
}

.size-card:focus-visible {
	outline: 2px solid var(--accent-base, #3b5bfd);
	outline-offset: 2px;
}

.size-card.active {
	border-color: var(--accent-base, #3b5bfd);
	background: color-mix(in srgb, var(--accent-base, #3b5bfd) 5%, var(--surface-base, #ffffff));
	box-shadow: 0 0 0 1px var(--accent-base, #3b5bfd);
}

.active-badge {
	position: absolute;
	top: -1px;
	right: -1px;
	width: 16px;
	height: 16px;
	background: var(--accent-base, #3b5bfd);
	color: #ffffff;
	border-top-right-radius: 11px;
	border-bottom-left-radius: 6px;
	display: flex;
	align-items: center;
	justify-content: center;
}

.size-icon-shell {
	display: flex;
	align-items: center;
	justify-content: center;
	height: 24px;
	margin-bottom: 6px;
	color: var(--ink-3, #6b7280);
}

.size-card.active .size-icon-shell {
	color: var(--accent-base, #3b5bfd);
}

.size-label {
	font-size: 13px;
	font-weight: 600;
	color: var(--ink-1, #171a1f);
	line-height: 1.2;
}

.size-desc {
	font-size: 10px;
	color: var(--ink-4, #8a929e);
	margin-top: 3px;
	white-space: nowrap;
}
</style>
