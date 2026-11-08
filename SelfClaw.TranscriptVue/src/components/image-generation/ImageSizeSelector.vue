<script setup>
import { Square, RectangleHorizontal, RectangleVertical, Sliders, Check } from 'lucide-vue-next';
import { PRESET_SIZES } from '../../composables/useImageStudio.js';

const props = defineProps({
	selectedKey: { type: String, default: '16:9' },
});

const emit = defineEmits(['select-size']);

const iconMap = {
	'1:1': Square,
	'16:9': RectangleHorizontal,
	'9:16': RectangleVertical,
	custom: Sliders,
};
</script>

<template>
	<div class="size-selector-block">
		<label class="section-label">图片尺寸</label>

		<div class="sizes-grid">
			<div
				v-for="item in PRESET_SIZES"
				:key="item.key"
				class="size-card"
				:class="{ active: item.key === selectedKey }"
				@click="emit('select-size', item.key)"
			>
				<!-- 选中对勾小徽标 -->
				<div v-if="item.key === selectedKey" class="active-badge">
					<Check :size="10" stroke-width="3" />
				</div>

				<div class="size-icon-shell">
					<component :is="iconMap[item.key] || Square" :size="20" class="size-icon" />
				</div>

				<div class="size-label">{{ item.label }}</div>
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
	grid-template-columns: repeat(4, 1fr);
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
