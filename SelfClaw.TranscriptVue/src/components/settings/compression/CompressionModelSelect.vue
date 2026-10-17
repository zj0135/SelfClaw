<script setup>
import { computed } from 'vue';
import CompressionRow from './CompressionRow.vue';

const props = defineProps({
	modelProfileId: { type: String, default: null },
	models: { type: Array, default: () => [] },
	loading: { type: Boolean, default: false },
	error: { type: String, default: '' },
	index: { type: Number, default: 0 },
});

const emit = defineEmits(['update:modelProfileId']);

const selected = computed({
	get: () => props.modelProfileId || '',
	set: (next) => emit('update:modelProfileId', next || null),
});

// 模型被删掉/停用后宿主会把它归一化成 null；万一列表还没回来，也不能让下拉默默显示成“跟随”。
const unavailable = computed(
	() => Boolean(props.modelProfileId) && !props.models.some((model) => model.modelProfileId === props.modelProfileId),
);

function optionLabel(model) {
	return model.providerName ? `${model.name} · ${model.providerName}` : model.name;
}
</script>

<template>
	<CompressionRow label="压缩模型" :index="index"
		hint="摘要由这个模型生成；选「跟随当前会话模型」时用本回合的模型，不可用则回退本回合模型。">
		<div class="stack">
			<select class="model-select" v-model="selected" :disabled="loading" aria-label="上下文压缩模型选择">
				<option value="">跟随当前会话模型</option>
				<option v-for="model in models" :key="model.modelProfileId" :value="model.modelProfileId">
					{{ optionLabel(model) }}
				</option>
				<option v-if="unavailable" :value="modelProfileId">当前配置的模型已不可用</option>
			</select>
			<span v-if="loading" class="select-note">正在加载已启用模型…</span>
			<span v-else-if="error" class="select-error" role="alert">{{ error }}</span>
			<span v-else-if="unavailable" class="select-note">保存后会回退到「跟随当前会话模型」</span>
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

.model-select {
	min-width: 220px;
	max-width: 320px;
	height: 34px;
	padding: 0 10px;
	border: 1px solid var(--sc-line-2);
	border-radius: 8px;
	outline: 0;
	background: var(--sc-panel);
	color: var(--sc-text);
	font: inherit;
	font-size: var(--fs-12);
	cursor: pointer;
}

.model-select:disabled {
	cursor: default;
	opacity: 0.6;
}

.model-select:focus-visible {
	border-color: var(--sc-acid);
	box-shadow: 0 0 0 3px var(--sc-acid-soft);
}

.select-note {
	color: var(--sc-faint);
	font-size: var(--fs-105);
}

.select-error {
	color: var(--sc-err);
	font-size: var(--fs-105);
}

@media (max-width: 720px) {
	.stack {
		align-items: flex-start;
	}

	.model-select {
		min-width: 0;
		width: 100%;
	}
}
</style>
