<script setup>
import { computed } from 'vue';
import { AlertTriangle } from 'lucide-vue-next';
import CompressionRow from './CompressionRow.vue';

const props = defineProps({
	ratio: { type: Number, default: 0.75 },
	index: { type: Number, default: 0 },
});

const emit = defineEmits(['update:ratio']);

// 与宿主归一化区间一致（10%–95%）。这里只做即时提示，取值仍以宿主回显为准。
const MIN_PERCENT = 10;
const MAX_PERCENT = 95;

const percent = computed(() => {
	const value = Number(props.ratio);
	return Number.isFinite(value) ? Math.round(value * 100) : MIN_PERCENT;
});
const outOfRange = computed(() => percent.value < MIN_PERCENT || percent.value > MAX_PERCENT);

function commitPercent(next) {
	const value = Number(next);
	if (!Number.isFinite(value)) return;
	emit('update:ratio', Math.min(100, Math.max(0, value)) / 100);
}
</script>

<template>
	<CompressionRow label="触发比例" :index="index"
		hint="上下文达到模型窗口的这个比例时压缩。窗口未知的模型改用下方绝对阈值。">
		<div class="stack">
			<div class="field">
				<input class="slider" type="range" :min="MIN_PERCENT" :max="MAX_PERCENT" step="1" :value="percent"
					aria-label="上下文压缩触发比例" @input="commitPercent($event.target.value)" />
				<label class="percent">
					<input class="percent-input" type="number" :min="MIN_PERCENT" :max="MAX_PERCENT" step="1" :value="percent"
						aria-label="上下文压缩触发比例百分比" @change="commitPercent($event.target.value)" />
					<span class="percent-sign" aria-hidden="true">%</span>
				</label>
			</div>
			<span v-if="outOfRange" class="field-warn" role="status">
				<AlertTriangle :size="12" :stroke-width="2" aria-hidden="true" />
				超出 10%–95%，保存后会被归一化
			</span>
		</div>
	</CompressionRow>
</template>

<style scoped>
.stack {
	display: flex;
	flex-direction: column;
	align-items: flex-end;
	gap: 6px;
}

.field {
	display: flex;
	align-items: center;
	gap: 12px;
}

.slider {
	width: clamp(120px, 22vw, 240px);
	accent-color: var(--sc-acid);
}

.slider:focus-visible {
	outline: 2px solid var(--sc-acid);
	outline-offset: 4px;
	border-radius: 999px;
}

.percent {
	display: inline-flex;
	align-items: center;
	gap: 4px;
	height: 34px;
	padding: 0 10px;
	border: 1px solid var(--sc-line-2);
	border-radius: 8px;
	background: var(--sc-panel);
	color: var(--sc-mute);
}

.percent:focus-within {
	border-color: var(--sc-acid);
	box-shadow: 0 0 0 3px var(--sc-acid-soft);
}

.percent-input {
	width: 42px;
	border: 0;
	outline: 0;
	background: transparent;
	color: var(--sc-text);
	font: inherit;
	font-size: var(--fs-12);
	font-variant-numeric: tabular-nums;
	text-align: right;
}

.percent-sign {
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-10);
}

.field-warn {
	display: inline-flex;
	align-items: center;
	gap: 5px;
	color: var(--sc-err);
	font-size: var(--fs-105);
}

@media (max-width: 720px) {
	.stack {
		align-items: flex-start;
	}
}
</style>
