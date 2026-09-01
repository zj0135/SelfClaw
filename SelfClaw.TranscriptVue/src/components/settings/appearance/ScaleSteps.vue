<script setup>
import { FONT_SCALE_STEPS } from '../../../composables/useAppearance.js';

const props = defineProps({
	value: { type: Number, default: 1 },
});

const emit = defineEmits(['select']);
</script>

<template>
	<div class="steps" role="group" aria-label="字号档位">
		<button v-for="step in FONT_SCALE_STEPS" :key="step.id" type="button" class="step"
			:data-active="props.value === step.scale ? 'true' : 'false'"
			:aria-pressed="props.value === step.scale ? 'true' : 'false'" @click="emit('select', step.scale)">
			<span class="step-label">{{ step.label }}</span>
			<span class="step-scale">{{ step.scale.toFixed(2).replace(/0$/, '') }}×</span>
		</button>
	</div>
</template>

<style scoped>
@import '../../../styles/settings-console.css';

.steps {
	display: inline-flex;
	padding: 3px;
	border: 1px solid var(--sc-line-2);
	border-radius: 9px;
	background: var(--sc-raise);
}

.step {
	display: flex;
	flex-direction: column;
	align-items: center;
	gap: 1px;
	min-width: 54px;
	padding: 5px 10px 6px;
	border: 0;
	border-radius: 7px;
	background: transparent;
	color: var(--sc-mute);
	font: inherit;
	cursor: pointer;
	transition:
		background 0.16s var(--sc-ease-out),
		color 0.16s var(--sc-ease-out);
}

.step:hover {
	color: var(--sc-text);
}

.step:focus-visible {
	outline: 2px solid var(--sc-acid);
	outline-offset: 1px;
}

.step[data-active='true'] {
	background: var(--sc-panel);
	color: var(--sc-text);
	box-shadow: 0 1px 3px rgba(var(--shadow-ink), 0.08);
}

/* 档位标签刻意不跟 --ui-font-scale 缩放：这块控件本身就是用来改那个值的，
   跟着一起变会让点击目标在脚下移动。 */
.step-label {
	font-size: 12.5px;
	font-weight: 600;
}

.step-scale {
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: 9px;
	letter-spacing: 0.04em;
}

.step[data-active='true'] .step-scale {
	color: var(--sc-acid);
}
</style>
