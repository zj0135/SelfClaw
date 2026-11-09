<script setup>
import { ref } from 'vue';
import { ChevronDown, ChevronUp } from 'lucide-vue-next';
import { COMMON_TAGS, EXTENDED_TAGS } from '../../renderers/imageStudio.js';

defineProps({
	prompt: { type: String, default: '' },
});

const emit = defineEmits([
	'update:prompt',
	'append-tag',
]);

const showMoreTags = ref(false);
</script>

<template>
	<div class="prompt-section">
		<div class="field-block">
			<label class="field-label" for="studio-prompt-input">提示词</label>
			<div class="textarea-shell">
				<textarea
					id="studio-prompt-input"
					:value="prompt"
					rows="3"
					maxlength="1000"
					placeholder="请输入你想生成的图片描述..."
					class="custom-textarea"
					@input="emit('update:prompt', $event.target.value)"
				></textarea>
				<span class="char-count">{{ prompt.length }} / 1000</span>
			</div>
		</div>

		<!-- 常用标签 -->
		<div class="tags-block">
			<span class="tags-label">常用标签</span>
			<div class="tags-flow">
				<button
					v-for="tag in COMMON_TAGS"
					:key="tag"
					type="button"
					class="tag-pill"
					@click="emit('append-tag', tag)"
				>
					{{ tag }}
				</button>

				<button type="button" class="tag-pill more-btn" @click="showMoreTags = !showMoreTags">
					<span>{{ showMoreTags ? '收起' : '更多' }}</span>
					<component :is="showMoreTags ? ChevronUp : ChevronDown" :size="12" />
				</button>
			</div>

			<div v-if="showMoreTags" class="tags-flow extended">
				<button
					v-for="tag in EXTENDED_TAGS"
					:key="tag"
					type="button"
					class="tag-pill ext-pill"
					@click="emit('append-tag', tag)"
				>
					{{ tag }}
				</button>
			</div>
		</div>
	</div>
</template>

<style scoped>
.prompt-section {
	display: flex;
	flex-direction: column;
	gap: 14px;
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
	width: 100%;
	border: none;
	outline: none;
	background: transparent;
	font-family: inherit;
	font-size: 13px;
	line-height: 1.5;
	color: var(--ink-1, #171a1f);
	resize: none;
}

.custom-textarea::placeholder {
	color: var(--ink-4, #8a929e);
}

.char-count {
	position: absolute;
	right: 12px;
	bottom: 6px;
	font-size: 11px;
	color: var(--ink-4, #8a929e);
	font-variant-numeric: tabular-nums;
	user-select: none;
}

.tags-block {
	display: flex;
	flex-direction: column;
	gap: 8px;
}

.tags-label {
	font-size: 12px;
	font-weight: 500;
	color: var(--ink-3, #6b7280);
}

.tags-flow {
	display: flex;
	flex-wrap: wrap;
	gap: 6px;
}

.tags-flow.extended {
	padding-top: 4px;
	border-top: 1px dashed var(--line-1, rgba(0, 0, 0, 0.06));
}

.tag-pill {
	display: inline-flex;
	align-items: center;
	gap: 3px;
	padding: 4px 10px;
	background: var(--surface-soft, #f7f8fa);
	border: 1px solid var(--line-1, rgba(0, 0, 0, 0.06));
	border-radius: 6px;
	font-size: 12px;
	color: var(--ink-2, #454c59);
	cursor: pointer;
	transition: all 0.15s ease;
	user-select: none;
}

.tag-pill:hover {
	background: color-mix(in srgb, var(--accent-base, #3b5bfd) 8%, var(--surface-soft, #f7f8fa));
	border-color: var(--accent-base, #3b5bfd);
	color: var(--accent-base, #3b5bfd);
}

.tag-pill.more-btn {
	background: transparent;
	color: var(--ink-3, #6b7280);
	border-style: dashed;
}

.tag-pill.more-btn:hover {
	color: var(--accent-base, #3b5bfd);
	border-color: var(--accent-base, #3b5bfd);
}

.tag-pill.ext-pill {
	background: var(--surface-muted, #f1f3f6);
}
</style>
