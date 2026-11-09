<script setup>
import { computed } from 'vue';

const props = defineProps({
	count: { type: Number, default: 1 },
	maxCount: { type: Number, default: 1 },
});

const emit = defineEmits(['update:count']);

const upperBound = computed(() => Math.max(props.maxCount, 1));
const sliderValue = computed(() => Math.min(props.count, upperBound.value));
</script>

<template>
	<div class="sliders-stack">
		<!-- OpenAI Images 协议只接受 n，没有采样步数与 CFG：不画不存在的旋钮。 -->
		<div class="slider-row">
			<span class="row-label">生成数量</span>
			<div class="slider-control">
				<input
					type="range"
					min="1"
					:max="upperBound"
					step="1"
					:value="sliderValue"
					:disabled="upperBound <= 1"
					class="custom-range"
					@input="emit('update:count', Number($event.target.value))"
				/>
				<div class="value-box">{{ sliderValue }}</div>
				<span class="range-hint">1 - {{ upperBound }}</span>
			</div>
		</div>
	</div>
</template>

<style scoped>
.sliders-stack {
	display: flex;
	flex-direction: column;
	gap: 12px;
	width: 100%;
}

.slider-row {
	display: flex;
	flex-direction: column;
	gap: 6px;
}

.row-label {
	font-size: 13px;
	font-weight: 600;
	color: var(--ink-1, #171a1f);
}

.slider-control {
	display: flex;
	align-items: center;
	gap: 12px;
}

.custom-range {
	flex: 1;
	height: 6px;
	border-radius: 3px;
	background: var(--surface-muted, #f1f3f6);
	outline: none;
	appearance: none;
	cursor: pointer;
}

.custom-range:disabled {
	cursor: default;
	opacity: 0.6;
}

.custom-range::-webkit-slider-thumb {
	appearance: none;
	width: 16px;
	height: 16px;
	border-radius: 50%;
	background: var(--accent-base, #3b5bfd);
	cursor: pointer;
	box-shadow: 0 1px 4px rgba(59, 91, 253, 0.4);
	transition: transform 0.1s ease;
}

.custom-range:disabled::-webkit-slider-thumb {
	cursor: default;
}

.custom-range::-webkit-slider-thumb:hover {
	transform: scale(1.15);
}

.value-box {
	min-width: 44px;
	height: 28px;
	display: flex;
	align-items: center;
	justify-content: center;
	background: var(--surface-base, #ffffff);
	border: 1px solid var(--line-card, #e3e6ec);
	border-radius: 6px;
	font-size: 12px;
	font-weight: 600;
	color: var(--ink-1, #171a1f);
	font-variant-numeric: tabular-nums;
}

.range-hint {
	font-size: 11px;
	color: var(--ink-4, #8a929e);
	min-width: 38px;
	text-align: right;
	user-select: none;
}
</style>
