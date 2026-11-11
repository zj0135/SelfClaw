<script setup>
import { computed } from 'vue';
import { DEFAULT_MAX_PROMPT_LENGTH } from '../../renderers/imageStudio.js';

const props = defineProps({
	prompt: { type: String, default: '' },
	maxPromptLength: { type: Number, default: DEFAULT_MAX_PROMPT_LENGTH },
});

const emit = defineEmits(['update:prompt']);

const limit = computed(() => Math.max(1, props.maxPromptLength || DEFAULT_MAX_PROMPT_LENGTH));
</script>

<template>
	<div class="prompt-section">
		<div class="field-block">
			<label class="field-label" for="studio-prompt-input">提示词</label>
			<div class="textarea-shell">
				<textarea
					id="studio-prompt-input"
					:value="prompt"
					rows="9"
					:maxlength="limit"
					placeholder="请输入你想生成的图片描述..."
					class="custom-textarea"
					@input="emit('update:prompt', $event.target.value)"
				></textarea>
				<span class="char-count">{{ prompt.length }} / {{ limit }}</span>
			</div>
		</div>
	</div>
</template>

<style scoped>
.prompt-section {
	display: flex;
	flex-direction: column;
	width: 100%;
}

.field-block {
	display: flex;
	flex-direction: column;
	gap: 6px;
}

.field-label {
	font-size: 13px;
	font-weight: 600;
	color: var(--ink-1, #171a1f);
}

.textarea-shell {
	position: relative;
	background: var(--surface-base, #ffffff);
	border: 1px solid var(--line-card, #e3e6ec);
	border-radius: 12px;
	padding: 10px 12px 24px;
	transition: all 0.15s ease;
}

.textarea-shell:focus-within {
	border-color: var(--accent-base, #3b5bfd);
	box-shadow: 0 0 0 3px color-mix(in srgb, var(--accent-base, #3b5bfd) 15%, transparent);
}

.custom-textarea {
	display: block;
	width: 100%;
	min-height: 162px;
	border: none;
	outline: none;
	background: transparent;
	font-family: inherit;
	font-size: 13px;
	line-height: 1.5;
	color: var(--ink-1, #171a1f);
	resize: vertical;
}

.custom-textarea::placeholder {
	color: var(--ink-4, #8a929e);
}

.char-count {
	position: absolute;
	right: 20px;
	bottom: 6px;
	font-size: 11px;
	color: var(--ink-4, #8a929e);
	font-variant-numeric: tabular-nums;
	user-select: none;
	pointer-events: none;
}
</style>
