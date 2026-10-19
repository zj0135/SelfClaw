<script setup>
import { onMounted, ref } from 'vue';
import { X, Save, LoaderCircle, SlidersHorizontal, ReceiptText, Sparkles } from 'lucide-vue-next';
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
			<div class="dialog-frame">
				<form @submit.prevent="save" @invalid.capture="tab = $event.target.closest('[data-tab]')?.dataset.tab || tab">
					<header class="dialog-header">
						<div class="heading">
							<h2 id="configuration-title">{{ draft.name }}</h2>
							<div class="model-id-tag">
								<code>{{ draft.model }}</code>
							</div>
						</div>
						<button class="icon-button close-btn" type="button" title="关闭" aria-label="关闭模型参数" :disabled="busy" @click="close">
							<X :size="18" :stroke-width="2" />
						</button>
					</header>

					<div class="tabs-bar" role="tablist" aria-label="模型配置">
						<button
							id="parameters-tab"
							type="button"
							class="tab-btn"
							role="tab"
							:aria-selected="tab === 'parameters'"
							:class="{ active: tab === 'parameters' }"
							aria-controls="parameters-panel"
							@click="tab = 'parameters'"
						>
							<SlidersHorizontal :size="14" :stroke-width="2.2" />
							<span>参数设置</span>
						</button>
						<button
							id="metadata-tab"
							type="button"
							class="tab-btn"
							role="tab"
							:aria-selected="tab === 'metadata'"
							:class="{ active: tab === 'metadata' }"
							aria-controls="metadata-panel"
							@click="tab = 'metadata'"
						>
							<ReceiptText :size="14" :stroke-width="2.2" />
							<span>计费与元数据</span>
						</button>
					</div>

					<div class="dialog-body scroll">
						<fieldset :disabled="busy" class="fieldset-reset">
							<div v-show="tab === 'parameters'" id="parameters-panel" role="tabpanel" aria-labelledby="parameters-tab" data-tab="parameters">
								<ModelParameterFields :draft="draft" />
							</div>
							<div v-show="tab === 'metadata'" id="metadata-panel" role="tabpanel" aria-labelledby="metadata-tab" data-tab="metadata">
								<ModelMetadataFields :draft="draft" />
							</div>
						</fieldset>
						<div v-if="error" class="error-banner" role="alert">
							<span>{{ error }}</span>
						</div>
					</div>

					<footer class="dialog-footer">
						<button type="button" class="btn btn-secondary" :disabled="busy" @click="close">
							取消
						</button>
						<button type="submit" class="btn btn-primary" :disabled="busy">
							<LoaderCircle v-if="busy" :size="15" class="spin" />
							<Save v-else :size="15" :stroke-width="2.2" />
							<span>{{ busy ? '保存中...' : '保存配置' }}</span>
						</button>
					</footer>
				</form>
			</div>
		</dialog>
	</Teleport>
</template>

<style scoped>
@import '../../styles/settings-console.css';

.model-dialog {
	width: min(660px, calc(100vw - 32px));
	max-width: none;
	max-height: min(820px, calc(100dvh - 36px));
	padding: 0;
	border: 0;
	background: transparent;
	color: var(--sc-text);
	font-family: var(--sc-sans);
	letter-spacing: 0;
	overflow: visible;
}

.model-dialog::backdrop {
	background: var(--overlay-heavy);
	backdrop-filter: blur(12px);
	animation: sc-fade 0.2s var(--sc-ease-out);
}

.dialog-frame {
	position: relative;
	display: flex;
	flex-direction: column;
	max-height: calc(100dvh - 48px);
	border: 1px solid var(--sc-line-2);
	border-radius: 16px;
	background: var(--sc-panel);
	box-shadow: 0 32px 100px rgba(var(--shadow-ink), 0.36), 0 4px 20px rgba(var(--shadow-ink), 0.12);
	overflow: hidden;
	animation: sc-pop 0.24s var(--sc-ease-spring);
}

form {
	display: flex;
	flex-direction: column;
	min-height: 0;
	max-height: inherit;
}

.dialog-header {
	position: relative;
	display: flex;
	align-items: flex-start;
	justify-content: space-between;
	gap: 16px;
	padding: 24px 26px 18px;
	border-bottom: 1px solid var(--sc-line);
	background: linear-gradient(to bottom, var(--sc-raise), transparent);
}

.heading {
	min-width: 0;
	overflow-wrap: anywhere;
}

.eyebrow-row {
	display: flex;
	align-items: center;
	gap: 10px;
	margin-bottom: 4px;
}

.eyebrow {
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-95);
	font-weight: 650;
	letter-spacing: 0.22em;
}

.live-pill {
	display: inline-flex;
	align-items: center;
	gap: 5px;
	padding: 2px 7px;
	border: 1px solid color-mix(in srgb, var(--sc-ok) 35%, transparent);
	border-radius: 99px;
	background: var(--sc-ok-soft);
	color: var(--sc-ok);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
	font-weight: 650;
	letter-spacing: 0.1em;
}

.live-pill .dot {
	width: 5px;
	height: 5px;
	border-radius: 50%;
	background: var(--sc-ok);
}

h2 {
	margin: 2px 0 6px;
	font-family: var(--sc-display);
	font-size: var(--fs-22, 22px);
	line-height: 1.25;
	font-weight: 650;
	letter-spacing: 0.01em;
}

.model-id-tag code {
	display: inline-block;
	padding: 3px 8px;
	border: 1px solid var(--sc-line);
	border-radius: 6px;
	background: var(--sc-raise);
	color: var(--sc-soft);
	font-family: var(--sc-mono);
	font-size: var(--fs-115);
}

.icon-button {
	display: grid;
	place-items: center;
	width: 32px;
	height: 32px;
	border: 1px solid var(--sc-line);
	border-radius: 8px;
	background: var(--sc-panel);
	color: var(--sc-mute);
	cursor: pointer;
	transition: all 0.16s var(--sc-ease-out);
}

.icon-button:hover {
	color: var(--sc-text);
	border-color: var(--sc-line-2);
	background: var(--sc-hover);
	transform: scale(1.05);
}

/* Tabs bar */
.tabs-bar {
	display: grid;
	grid-template-columns: 1fr 1fr;
	gap: 8px;
	padding: 10px 24px;
	border-bottom: 1px solid var(--sc-line);
	background: var(--sc-raise);
}

.tab-btn {
	display: flex;
	flex-direction: column;
	align-items: center;
	justify-content: center;
	gap: 3px;
	padding: 9px 12px;
	border: 1px solid transparent;
	border-radius: 9px;
	background: transparent;
	color: var(--sc-mute);
	cursor: pointer;
	transition: all 0.18s var(--sc-ease-out);
}

.tab-btn:hover {
	background: var(--sc-hover);
	color: var(--sc-text);
}

.tab-btn.active {
	border-color: var(--sc-line-2);
	background: var(--sc-panel);
	color: var(--sc-acid);
	box-shadow: 0 4px 12px rgba(var(--shadow-ink), 0.06);
}

.tab-btn span {
	font-size: var(--fs-125);
	font-weight: 600;
}

.tab-sub {
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-85, 9px) !important;
	letter-spacing: 0.12em;
}

.tab-btn.active .tab-sub {
	color: color-mix(in srgb, var(--sc-acid) 80%, var(--sc-faint));
}

.dialog-body {
	flex: 1;
	min-height: 0;
	padding: 20px 26px;
	overflow-y: auto;
}

.fieldset-reset {
	border: 0;
	padding: 0;
	margin: 0;
	min-width: 0;
}

.scroll::-webkit-scrollbar {
	width: 7px;
}

.scroll::-webkit-scrollbar-thumb {
	background: var(--sc-line-2);
	border-radius: 99px;
}

.error-banner {
	margin-top: 14px;
	padding: 10px 14px;
	border: 1px solid color-mix(in srgb, var(--sc-err) 30%, transparent);
	border-radius: 8px;
	background: var(--sc-err-soft);
	color: var(--sc-err);
	font-size: var(--fs-125);
}

/* Footer */
.dialog-footer {
	display: flex;
	align-items: center;
	justify-content: flex-end;
	gap: 10px;
	padding: 16px 26px;
	border-top: 1px solid var(--sc-line);
	background: var(--sc-raise);
}

.btn {
	display: inline-flex;
	align-items: center;
	justify-content: center;
	gap: 8px;
	height: 38px;
	padding: 0 18px;
	border: 1px solid transparent;
	border-radius: 9px;
	font-family: inherit;
	font-size: var(--fs-13);
	font-weight: 600;
	cursor: pointer;
	transition: all 0.18s var(--sc-ease-spring);
}

.btn-secondary {
	border-color: var(--sc-line-2);
	background: var(--sc-panel);
	color: var(--sc-soft);
}

.btn-secondary:hover {
	border-color: var(--sc-faint);
	background: var(--sc-hover);
	color: var(--sc-text);
}

.btn-primary {
	border-color: var(--sc-acid);
	background: var(--sc-acid);
	color: var(--sc-acid-ink);
	box-shadow: 0 1px 3px rgba(var(--shadow-ink), 0.08);
}

.btn-primary:hover {
	transform: translateY(-1px);
	box-shadow: 0 3px 8px rgba(var(--shadow-ink), 0.12);
}

.btn-primary:active {
	transform: translateY(0);
}

.spin {
	animation: sc-spin 0.8s linear infinite;
}
</style>
