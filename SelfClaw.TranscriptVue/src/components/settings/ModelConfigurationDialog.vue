<script setup>
import { onMounted, ref } from 'vue';
import { X, Save, LoaderCircle, SlidersHorizontal, ReceiptText } from 'lucide-vue-next';
import ModelParameterFields from './ModelParameterFields.vue';
import ModelMetadataFields from './ModelMetadataFields.vue';
import { useModelConfigurationEditor } from '../../composables/useModelConfigurationEditor.js';

const props = defineProps({ model: { type: Object, required: true }, initialTab: { type: String, default: 'parameters' } });
const emit = defineEmits(['close', 'saved']);
const dialog = ref(null);
const tab = ref(props.initialTab);
const { draft, busy, error, save } = useModelConfigurationEditor(props.model, (saved) => {
	dialog.value.close();
	emit('saved', saved);
});
onMounted(() => dialog.value.showModal());

function close() {
	if (!busy.value) {
		dialog.value.close();
		emit('close');
	}
}
</script>

<template>
	<Teleport to="body">
		<dialog
			ref="dialog"
			class="model-dialog sc-root"
			aria-labelledby="configuration-title"
			@cancel.prevent="close"
			@click="$event.target === dialog && close()"
		>
			<form @submit.prevent="save" @invalid.capture="tab = $event.target.closest('[data-tab]')?.dataset.tab || tab">
				<header>
					<div class="heading">
						<span class="eyebrow">模型参数</span>
						<h2 id="configuration-title">{{ draft.name }}</h2>
						<code>{{ draft.model }}</code>
					</div>
					<button class="icon-button" type="button" title="关闭" aria-label="关闭模型参数" :disabled="busy" @click="close"><X :size="18" /></button>
				</header>
				<div class="tabs" role="tablist" aria-label="模型配置">
					<button
						id="parameters-tab"
						type="button"
						role="tab"
						:aria-selected="tab === 'parameters'"
						aria-controls="parameters-panel"
						@click="tab = 'parameters'"
					>
						<SlidersHorizontal :size="15" />参数
					</button>
					<button
						id="metadata-tab"
						type="button"
						role="tab"
						:aria-selected="tab === 'metadata'"
						aria-controls="metadata-panel"
						@click="tab = 'metadata'"
					>
						<ReceiptText :size="15" />计费与元数据
					</button>
				</div>
				<div class="body">
					<fieldset :disabled="busy">
						<div v-show="tab === 'parameters'" id="parameters-panel" role="tabpanel" aria-labelledby="parameters-tab" data-tab="parameters">
							<ModelParameterFields :draft="draft" />
						</div>
						<div v-show="tab === 'metadata'" id="metadata-panel" role="tabpanel" aria-labelledby="metadata-tab" data-tab="metadata">
							<ModelMetadataFields :draft="draft" />
						</div>
					</fieldset>
					<p v-if="error" class="error" role="alert">{{ error }}</p>
				</div>
				<footer>
					<button type="button" :disabled="busy" @click="close">取消</button
					><button type="submit" class="primary" :disabled="busy">
						<LoaderCircle v-if="busy" :size="15" class="spin" /><Save v-else :size="15" />{{ busy ? '保存中...' : '保存配置' }}
					</button>
				</footer>
			</form>
		</dialog>
	</Teleport>
</template>

<style scoped>
@import '../../styles/settings-console.css';
.model-dialog {
	width: min(620px, calc(100vw - 32px));
	max-width: none;
	max-height: calc(100dvh - 32px);
	padding: 0;
	border: 1px solid var(--sc-line-2);
	border-radius: 8px;
	background: var(--sc-panel);
	color: var(--sc-text);
	box-shadow: 0 24px 80px rgba(0, 0, 0, 0.22);
	font-family: var(--sc-sans);
	letter-spacing: 0;
}
.model-dialog::backdrop {
	background: rgba(20, 24, 32, 0.4);
}
form {
	display: flex;
	flex-direction: column;
	max-height: calc(100dvh - 36px);
}
header {
	display: flex;
	align-items: flex-start;
	justify-content: space-between;
	gap: 16px;
	padding: 24px;
}
.heading {
	min-width: 0;
	overflow-wrap: anywhere;
}
.eyebrow {
	color: var(--sc-mute);
	font-size: 12px;
}
h2 {
	margin: 6px 0 5px;
	font-size: 20px;
	line-height: 1.4;
	font-weight: 600;
}
code {
	color: var(--sc-mute);
	font-family: var(--sc-mono);
	font-size: 12px;
}
button {
	display: inline-flex;
	justify-content: center;
	align-items: center;
	gap: 8px;
	height: 38px;
	padding: 0 14px;
	border: 1px solid var(--sc-line-2);
	border-radius: 6px;
	background: var(--sc-panel);
	color: var(--sc-text);
	font: inherit;
	font-size: 13px;
	cursor: pointer;
}
button:hover {
	background: var(--sc-hover);
}
button:focus-visible {
	outline: 2px solid var(--sc-acid);
	outline-offset: 2px;
}
button:disabled {
	opacity: 0.5;
	cursor: wait;
}
.icon-button {
	flex: 0 0 32px;
	width: 32px;
	height: 32px;
	padding: 0;
}
.tabs {
	display: flex;
	gap: 20px;
	padding: 0 24px;
	border-bottom: 1px solid var(--sc-line);
}
.tabs button {
	height: 42px;
	padding: 0 2px;
	border: 0;
	border-bottom: 2px solid transparent;
	border-radius: 0;
	color: var(--sc-mute);
}
.tabs button[aria-selected='true'] {
	border-bottom-color: var(--sc-acid);
	color: var(--sc-text);
}
.body {
	overflow-y: auto;
	min-height: 0;
	padding: 24px;
}
fieldset {
	min-width: 0;
	margin: 0;
	padding: 0;
	border: 0;
}
.error {
	margin: 18px 0 0;
	color: var(--danger, #cc3434);
	font-size: 13px;
	overflow-wrap: anywhere;
}
footer {
	display: flex;
	justify-content: flex-end;
	gap: 10px;
	padding: 16px 24px;
	border-top: 1px solid var(--sc-line);
}
.primary {
	background: var(--sc-acid);
	border-color: var(--sc-acid);
	color: var(--sc-acid-ink);
}
.primary:hover {
	filter: brightness(0.96);
	background: var(--sc-acid);
}
.spin {
	animation: spin 1s linear infinite;
}
@keyframes spin {
	to {
		transform: rotate(360deg);
	}
}
@media (max-width: 440px) {
	header {
		padding: 18px;
	}
	.body {
		padding: 20px 18px;
	}
	.tabs {
		padding: 0 18px;
	}
	footer {
		padding: 14px 18px;
	}
}
@media (prefers-reduced-motion: reduce) {
	.spin {
		animation: none;
	}
}
</style>
