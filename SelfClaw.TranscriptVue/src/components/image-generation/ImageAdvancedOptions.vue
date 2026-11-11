<script setup>
import { computed, ref } from 'vue';
import { ChevronDown } from 'lucide-vue-next';
import {
	QUALITY_LABELS,
	STYLE_LABELS,
	BACKGROUND_LABELS,
	OUTPUT_FORMAT_LABELS,
	MODERATION_LABELS,
} from '../../renderers/imageStudio.js';

const props = defineProps({
	qualities: { type: Array, default: () => [] },
	styles: { type: Array, default: () => [] },
	backgrounds: { type: Array, default: () => [] },
	outputFormats: { type: Array, default: () => [] },
	moderations: { type: Array, default: () => [] },
	quality: { type: String, default: '' },
	styleVal: { type: String, default: '' },
	background: { type: String, default: '' },
	outputFormat: { type: String, default: '' },
	outputCompression: { type: Number, default: 80 },
	moderation: { type: String, default: '' },
	supportsOutputCompression: { type: Boolean, default: false },
});

const emit = defineEmits([
	'update:quality',
	'update:styleVal',
	'update:background',
	'update:outputFormat',
	'update:outputCompression',
	'update:moderation',
]);

const isExpanded = ref(false);

// 每一行都由模型能力决定：能力清单为空或只有一档就不渲染，免得给用户一组点不动的开关。
// JPEG 没有 alpha 通道，透明背景时从选项里摘掉，避免用户选完才被宿主拒绝。
const rows = computed(() => [
	{ key: 'quality', label: '画质档位', values: props.qualities, current: props.quality, labels: QUALITY_LABELS, event: 'update:quality' },
	{ key: 'styleVal', label: '生成风格', values: props.styles, current: props.styleVal, labels: STYLE_LABELS, event: 'update:styleVal' },
	{ key: 'background', label: '背景', values: props.backgrounds, current: props.background, labels: BACKGROUND_LABELS, event: 'update:background' },
	{
		key: 'outputFormat', label: '输出格式',
		values: props.background === 'transparent'
			? props.outputFormats.filter((value) => value !== 'jpeg')
			: props.outputFormats,
		current: props.outputFormat, labels: OUTPUT_FORMAT_LABELS, event: 'update:outputFormat',
	},
	{ key: 'moderation', label: '内容过滤', values: props.moderations, current: props.moderation, labels: MODERATION_LABELS, event: 'update:moderation' },
].filter((row) => row.values.length > 1));

// output_compression 只对 jpeg / webp 生效，png 下不展示该行。
const showCompression = computed(() => props.supportsOutputCompression
	&& props.outputFormat
	&& props.outputFormat !== 'png');
const isVisible = computed(() => rows.value.length > 0 || showCompression.value);
</script>

<template>
	<div v-if="isVisible" class="advanced-wrapper">
		<button type="button" class="advanced-toggle" :aria-expanded="isExpanded" @click="isExpanded = !isExpanded">
			<span class="toggle-text">高级设置</span>
			<ChevronDown :size="15" class="toggle-arrow" :class="{ rotated: isExpanded }" />
		</button>

		<div v-if="isExpanded" class="advanced-body">
			<div v-for="row in rows" :key="row.key" class="opt-row">
				<span class="opt-label">{{ row.label }}</span>
				<div class="segmented-control">
					<button
						v-for="value in row.values"
						:key="value"
						type="button"
						class="seg-btn"
						:class="{ active: row.current === value }"
						:aria-pressed="row.current === value"
						@click="emit(row.event, value)"
					>
						{{ row.labels[value] || value }}
					</button>
				</div>
			</div>

			<div v-if="showCompression" class="opt-row">
				<span class="opt-label">压缩质量</span>
				<div class="compression-control">
					<input
						type="range"
						min="0"
						max="100"
						step="1"
						:value="outputCompression"
						class="compression-range"
						aria-label="压缩质量"
						@input="emit('update:outputCompression', Number($event.target.value))"
					/>
					<span class="compression-value">{{ outputCompression }}</span>
				</div>
			</div>
		</div>
	</div>
</template>

<style scoped>
.advanced-wrapper {
	display: flex;
	flex-direction: column;
	border-top: 1px solid var(--line-1, rgba(0, 0, 0, 0.06));
	padding-top: 10px;
}

.advanced-toggle {
	display: flex;
	align-items: center;
	justify-content: space-between;
	background: transparent;
	border: none;
	padding: 4px 0;
	cursor: pointer;
	color: var(--ink-2, #454c59);
}

.toggle-text {
	font-size: 13px;
	font-weight: 600;
}

.toggle-arrow {
	color: var(--ink-4, #8a929e);
	transition: transform 0.2s ease;
}

.toggle-arrow.rotated {
	transform: rotate(180deg);
}

.advanced-body {
	display: flex;
	flex-direction: column;
	gap: 12px;
	padding: 10px 0 4px;
	animation: slideDown 0.15s ease-out;
}

@keyframes slideDown {
	from { opacity: 0; transform: translateY(-4px); }
	to { opacity: 1; transform: translateY(0); }
}

.opt-row {
	display: flex;
	align-items: center;
	justify-content: space-between;
	gap: 12px;
}

.opt-label {
	font-size: 12px;
	color: var(--ink-3, #6b7280);
	font-weight: 500;
	flex-shrink: 0;
}

.segmented-control {
	display: inline-flex;
	flex-wrap: wrap;
	justify-content: flex-end;
	gap: 2px;
	background: var(--surface-muted, #f1f3f6);
	padding: 2px;
	border-radius: 8px;
}

.seg-btn {
	border: none;
	background: transparent;
	font-size: 11.5px;
	padding: 4px 10px;
	border-radius: 6px;
	color: var(--ink-3, #6b7280);
	cursor: pointer;
	transition: all 0.15s ease;
}

.seg-btn.active {
	background: var(--surface-base, #ffffff);
	color: var(--accent-base, #3b5bfd);
	font-weight: 600;
	box-shadow: 0 1px 3px rgba(0, 0, 0, 0.06);
}

.compression-control {
	display: inline-flex;
	align-items: center;
	gap: 8px;
	flex: 1;
	max-width: 180px;
}

.compression-range {
	flex: 1;
	accent-color: var(--accent-base, #3b5bfd);
}

.compression-value {
	min-width: 28px;
	text-align: right;
	font-size: 12px;
	font-weight: 600;
	color: var(--ink-2, #454c59);
	font-variant-numeric: tabular-nums;
}
</style>
