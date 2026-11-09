<script setup>
import { computed, ref } from 'vue';
import { ChevronDown } from 'lucide-vue-next';
import { QUALITY_LABELS, STYLE_LABELS } from '../../renderers/imageStudio.js';

const props = defineProps({
	qualities: { type: Array, default: () => [] },
	styles: { type: Array, default: () => [] },
	quality: { type: String, default: '' },
	styleVal: { type: String, default: '' },
});

const emit = defineEmits([
	'update:quality',
	'update:styleVal',
]);

const isExpanded = ref(false);

// 两个控件都由模型能力决定：gpt-image-1 没有 style，dall-e-3 没有 low/medium/high。
// 两边都空就不渲染整块「高级设置」，免得给用户一组点不动的开关。
const hasQualities = computed(() => props.qualities.length > 1);
const hasStyles = computed(() => props.styles.length > 1);
const isVisible = computed(() => hasQualities.value || hasStyles.value);
</script>

<template>
	<div v-if="isVisible" class="advanced-wrapper">
		<button type="button" class="advanced-toggle" :aria-expanded="isExpanded" @click="isExpanded = !isExpanded">
			<span class="toggle-text">高级设置</span>
			<ChevronDown :size="15" class="toggle-arrow" :class="{ rotated: isExpanded }" />
		</button>

		<div v-if="isExpanded" class="advanced-body">
			<div v-if="hasQualities" class="opt-row">
				<span class="opt-label">画质档位</span>
				<div class="segmented-control">
					<button
						v-for="value in qualities"
						:key="value"
						type="button"
						class="seg-btn"
						:class="{ active: quality === value }"
						:aria-pressed="quality === value"
						@click="emit('update:quality', value)"
					>
						{{ QUALITY_LABELS[value] || value }}
					</button>
				</div>
			</div>

			<div v-if="hasStyles" class="opt-row">
				<span class="opt-label">生成风格</span>
				<div class="segmented-control">
					<button
						v-for="value in styles"
						:key="value"
						type="button"
						class="seg-btn"
						:class="{ active: styleVal === value }"
						:aria-pressed="styleVal === value"
						@click="emit('update:styleVal', value)"
					>
						{{ STYLE_LABELS[value] || value }}
					</button>
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
	from {
		opacity: 0;
		transform: translateY(-4px);
	}
	to {
		opacity: 1;
		transform: translateY(0);
	}
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
</style>
