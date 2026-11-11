<script setup>
import { computed } from 'vue';
import { Square, RectangleHorizontal, RectangleVertical, Check, SlidersHorizontal } from 'lucide-vue-next';

const props = defineProps({
	// 宿主按模型能力回传的尺寸清单，形如 { value, ratio, orientation, desc }。
	sizes: { type: Array, required: true },
	selected: { type: String, default: '' },
	customRules: { type: Object, default: null },
	customWidth: { type: Number, default: 1024 },
	customHeight: { type: Number, default: 1024 },
	customError: { type: String, default: '' },
});

const emit = defineEmits([
	'select-size',
	'update:custom-width',
	'update:custom-height',
]);

const iconMap = {
	square: Square,
	landscape: RectangleHorizontal,
	portrait: RectangleVertical,
};

const hasCustom = computed(() => Boolean(props.customRules));
const isCustomActive = computed(() => hasCustom.value && props.selected === 'custom');
const columns = computed(() => Math.min((props.sizes?.length || 1) + (hasCustom.value ? 1 : 0), 4));
</script>

<template>
	<div class="size-selector-block">
		<label class="section-label">图片尺寸</label>

		<div class="sizes-grid" :style="{ '--columns': columns }">
			<div
				v-for="item in sizes"
				:key="item.value"
				class="size-card"
				:class="{ active: item.value === selected && !isCustomActive }"
				role="button"
				:aria-pressed="item.value === selected && !isCustomActive"
				tabindex="0"
				@click="emit('select-size', item.value)"
				@keydown.enter.prevent="emit('select-size', item.value)"
				@keydown.space.prevent="emit('select-size', item.value)"
			>
				<div v-if="item.value === selected && !isCustomActive" class="active-badge">
					<Check :size="10" stroke-width="3" />
				</div>

				<div class="size-icon-shell">
					<component :is="iconMap[item.orientation] || Square" :size="14" class="size-icon" />
				</div>

				<div class="size-label">{{ item.ratio }}</div>
				<div class="size-desc">{{ item.desc }}</div>
			</div>

			<div
				v-if="hasCustom"
				class="size-card"
				:class="{ active: isCustomActive }"
				role="button"
				:aria-pressed="isCustomActive"
				tabindex="0"
				@click="emit('select-size', 'custom')"
				@keydown.enter.prevent="emit('select-size', 'custom')"
				@keydown.space.prevent="emit('select-size', 'custom')"
			>
				<div v-if="isCustomActive" class="active-badge">
					<Check :size="10" stroke-width="3" />
				</div>
				<div class="size-icon-shell">
					<SlidersHorizontal :size="14" class="size-icon" />
				</div>
				<div class="size-label">自定义</div>
				<div class="size-desc">W × H</div>
			</div>
		</div>

		<div v-if="isCustomActive" class="custom-size-row">
			<input
				class="dimension-input"
				type="number"
				min="1"
				:step="customRules.multipleOf"
				:value="customWidth"
				aria-label="宽度"
				@input="emit('update:custom-width', Number($event.target.value))"
			/>
			<span class="dimension-x">×</span>
			<input
				class="dimension-input"
				type="number"
				min="1"
				:step="customRules.multipleOf"
				:value="customHeight"
				aria-label="高度"
				@input="emit('update:custom-height', Number($event.target.value))"
			/>
			<span class="dimension-hint">
				宽高为 {{ customRules.multipleOf }} 的倍数，比例 1:{{ customRules.maxAspectRatio }} ~
				{{ customRules.maxAspectRatio }}:1，上限 {{ customRules.maxWidth }} × {{ customRules.maxHeight }}
			</span>
			<p v-if="customError" class="dimension-error" role="alert">{{ customError }}</p>
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
	gap: 6px;
}

.size-card {
	position: relative;
	display: flex;
	flex-direction: column;
	align-items: center;
	justify-content: center;
	padding: 7px 4px 6px;
	background: var(--surface-base, #ffffff);
	border: 1px solid var(--line-card, #e3e6ec);
	border-radius: 9px;
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
	width: 13px;
	height: 13px;
	background: var(--accent-base, #3b5bfd);
	color: #ffffff;
	border-top-right-radius: 8px;
	border-bottom-left-radius: 5px;
	display: flex;
	align-items: center;
	justify-content: center;
}

.size-icon-shell {
	display: flex;
	align-items: center;
	justify-content: center;
	height: 16px;
	margin-bottom: 3px;
	color: var(--ink-3, #6b7280);
}

.size-card.active .size-icon-shell {
	color: var(--accent-base, #3b5bfd);
}

.size-label {
	font-size: 11.5px;
	font-weight: 600;
	line-height: 1.15;
	color: var(--ink-1, #171a1f);
}

.size-desc {
	font-size: 9px;
	color: var(--ink-4, #8a929e);
	margin-top: 2px;
	white-space: nowrap;
}

.custom-size-row {
	display: flex;
	flex-wrap: wrap;
	align-items: center;
	gap: 8px;
	padding: 10px;
	background: var(--surface-soft, #f7f8fa);
	border-radius: 10px;
}

.dimension-input {
	width: 92px;
	padding: 5px 8px;
	border: 1px solid var(--line-card, #e3e6ec);
	border-radius: 6px;
	font-size: 12px;
	font-family: inherit;
	color: var(--ink-1, #171a1f);
	background: var(--surface-base, #ffffff);
}

.dimension-x {
	font-size: 12px;
	color: var(--ink-3, #6b7280);
}

.dimension-hint {
	flex-basis: 100%;
	font-size: 10.5px;
	color: var(--ink-4, #8a929e);
	line-height: 1.4;
}

.dimension-error {
	flex-basis: 100%;
	margin: 0;
	font-size: 11px;
	color: #dc2626;
}
</style>
