<script setup>
import { ref } from 'vue';
import { ChevronDown } from 'lucide-vue-next';

defineProps({
	quality: { type: String, default: 'standard' },
	styleVal: { type: String, default: 'vivid' },
});

const emit = defineEmits([
	'update:quality',
	'update:styleVal',
]);

const isExpanded = ref(false);

function toggle() {
	isExpanded.value = !isExpanded.value;
}
</script>

<template>
	<div class="advanced-wrapper">
		<button type="button" class="advanced-toggle" @click="toggle">
			<span class="toggle-text">高级设置</span>
			<ChevronDown :size="15" class="toggle-arrow" :class="{ rotated: isExpanded }" />
		</button>

		<div v-if="isExpanded" class="advanced-body">
			<!-- 画质选项 -->
			<div class="opt-row">
				<span class="opt-label">画质档位</span>
				<div class="segmented-control">
					<button
						type="button"
						class="seg-btn"
						:class="{ active: quality === 'standard' }"
						@click="emit('update:quality', 'standard')"
					>
						标准 (Standard)
					</button>
					<button
						type="button"
						class="seg-btn"
						:class="{ active: quality === 'hd' }"
						@click="emit('update:quality', 'hd')"
					>
						高清 (HD)
					</button>
				</div>
			</div>

			<!-- 风格选项 -->
			<div class="opt-row">
				<span class="opt-label">生成风格</span>
				<div class="segmented-control">
					<button
						type="button"
						class="seg-btn"
						:class="{ active: styleVal === 'vivid' }"
						@click="emit('update:styleVal', 'vivid')"
					>
						生动 (Vivid)
					</button>
					<button
						type="button"
						class="seg-btn"
						:class="{ active: styleVal === 'natural' }"
						@click="emit('update:styleVal', 'natural')"
					>
						自然 (Natural)
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
}

.opt-label {
	font-size: 12px;
	color: var(--ink-3, #6b7280);
	font-weight: 500;
}

.segmented-control {
	display: inline-flex;
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
