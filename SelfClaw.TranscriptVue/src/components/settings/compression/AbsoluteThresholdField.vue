<script setup>
import { computed } from 'vue';
import { AlertTriangle } from 'lucide-vue-next';
import CompressionRow from './CompressionRow.vue';

const props = defineProps({
	value: { type: Number, default: 64000 },
	index: { type: Number, default: 0 },
});

const emit = defineEmits(['update:value']);

// 与宿主归一化区间一致（4 000–1 000 000）。只提示，不在这里夹紧。
const MIN_TOKENS = 4000;
const MAX_TOKENS = 1000000;

const outOfRange = computed(() => {
	const tokens = Number(props.value);
	return !Number.isFinite(tokens) || tokens < MIN_TOKENS || tokens > MAX_TOKENS;
});

function commitTokens(next) {
	const value = Number(next);
	if (!Number.isFinite(value)) return;
	emit('update:value', Math.round(value));
}
</script>

<template>
	<CompressionRow label="绝对阈值" :index="index"
		hint="只对未声明上下文窗口的模型生效：计数超过该 token 数就压缩。这类模型没有可用的窗口比例。">
		<div class="stack">
			<label class="tokens">
				<input class="tokens-input" type="number" :min="MIN_TOKENS" :max="MAX_TOKENS" step="1000" :value="props.value"
					aria-label="上下文压缩绝对阈值（token）" @change="commitTokens($event.target.value)" />
				<span class="tokens-unit">token</span>
			</label>
			<span class="tokens-range">有效范围 4000–1000000</span>
			<span v-if="outOfRange" class="tokens-warn" role="status">
				<AlertTriangle :size="12" :stroke-width="2" aria-hidden="true" />
				超出范围，保存后会被归一化
			</span>
		</div>
	</CompressionRow>
</template>

<style scoped>
.stack {
	display: flex;
	flex-direction: column;
	align-items: flex-end;
	gap: 5px;
}

.tokens {
	display: inline-flex;
	align-items: center;
	gap: 6px;
	height: 34px;
	padding: 0 10px;
	border: 1px solid var(--sc-line-2);
	border-radius: 8px;
	background: var(--sc-panel);
}

.tokens:focus-within {
	border-color: var(--sc-acid);
	box-shadow: 0 0 0 3px var(--sc-acid-soft);
}

.tokens-input {
	width: 88px;
	border: 0;
	outline: 0;
	background: transparent;
	color: var(--sc-text);
	font: inherit;
	font-size: var(--fs-12);
	font-variant-numeric: tabular-nums;
	text-align: right;
}

.tokens-unit {
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-10);
	letter-spacing: 0.03em;
}

.tokens-range {
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-10);
}

.tokens-warn {
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
