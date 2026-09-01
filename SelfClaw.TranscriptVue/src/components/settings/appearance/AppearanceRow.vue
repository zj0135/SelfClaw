<script setup>
// 设置行的骨架：左侧标签 + 说明，右侧控件。三个外观分组都用它排版，
// 避免每处各写一遍 grid。
defineProps({
	label: { type: String, required: true },
	hint: { type: String, default: '' },
	index: { type: Number, default: 0 },
});
</script>

<template>
	<div class="row sc-rise" :style="{ '--i': index }">
		<div class="row-meta">
			<span class="row-label">{{ label }}</span>
			<span v-if="hint" class="row-hint">{{ hint }}</span>
		</div>
		<div class="row-control">
			<slot />
		</div>
	</div>
</template>

<style scoped>
@import '../../../styles/settings-console.css';

.row {
	display: grid;
	grid-template-columns: minmax(0, 1fr) minmax(0, auto);
	align-items: center;
	gap: 18px;
	padding: 14px 0;
	border-bottom: 1px solid var(--sc-line);
}

.row:last-child {
	border-bottom: 0;
}

.row-meta {
	display: flex;
	flex-direction: column;
	gap: 3px;
	min-width: 0;
}

.row-label {
	color: var(--sc-text);
	font-size: var(--fs-13);
	font-weight: 600;
}

.row-hint {
	color: var(--sc-mute);
	font-size: var(--fs-115);
	line-height: 1.5;
}

.row-control {
	display: flex;
	align-items: center;
	justify-content: flex-end;
	gap: 8px;
	min-width: 0;
}

@media (max-width: 720px) {
	.row {
		grid-template-columns: minmax(0, 1fr);
		gap: 10px;
	}

	.row-control {
		justify-content: flex-start;
	}
}
</style>
