<script setup>
import { X } from 'lucide-vue-next';

defineProps({
	providerOpen: Boolean,
	modelOpen: Boolean,
	protocols: { type: Array, default: () => [] },
	providerDraft: { type: Object, required: true },
	provider: { type: Object, default: null },
	modelDraft: { type: Object, required: true },
	busy: Boolean,
});

const emit = defineEmits(['close-provider', 'submit-provider', 'close-model', 'submit-model']);

const formatNames = {
	0: 'OpenAI Chat Completions',
	1: 'OpenAI Responses',
	2: 'Anthropic Messages',
	3: 'Gemini generateContent',
	4: 'Ollama Native',
};
</script>

<template>
	<Teleport to="body">
		<div v-if="providerOpen" class="dialog-backdrop sc-root" @click.self="emit('close-provider')">
			<form class="dialog form-dialog" role="dialog" aria-modal="true" aria-labelledby="provider-dialog-title" @submit.prevent="emit('submit-provider')">
				<header>
					<div>
						<div class="dlg-kicker">NEW CONNECTION</div>
						<h3 id="provider-dialog-title">添加自定义服务商</h3>
						<p>添加一个 OpenAI 兼容或 Anthropic 协议的自定义 AI 服务商</p>
					</div>
					<button type="button" class="close" aria-label="关闭" @click="emit('close-provider')">
						<X :size="16" :stroke-width="2" />
					</button>
				</header>

				<label>
					<span>服务商名称</span>
					<input v-model.trim="providerDraft.name" required autocomplete="off" placeholder="我的服务商" />
				</label>
				<label>
					<span>协议类型</span>
					<select v-model="providerDraft.protocolId">
						<option v-for="protocol in protocols" :key="protocol.id" :value="protocol.id">
							{{ protocol.label }}
						</option>
					</select>
				</label>
				<label>
					<span>Base URL</span>
					<input v-model.trim="providerDraft.base" required autocomplete="off" class="mono" placeholder="https://api.example.com" />
					<small class="hint">API 接口的基础地址</small>
				</label>

				<footer>
					<button type="button" class="secondary" @click="emit('close-provider')">取消</button>
					<button type="submit" class="primary" :disabled="busy || !providerDraft.name || !providerDraft.base || !providerDraft.protocolId">
						{{ busy ? '添加中…' : '添加' }}
					</button>
				</footer>
			</form>
		</div>

		<div v-if="modelOpen" class="dialog-backdrop sc-root" @click.self="emit('close-model')">
			<form class="dialog form-dialog model-dialog" role="dialog" aria-modal="true" aria-labelledby="model-dialog-title" @submit.prevent="emit('submit-model')">
				<header>
					<div>
						<div class="dlg-kicker">NEW MODEL</div>
						<h3 id="model-dialog-title">手动添加模型</h3>
						<p>{{ provider?.name }} · 模型 id 将原样发送给 API。</p>
					</div>
					<button type="button" class="close" aria-label="关闭" @click="emit('close-model')">
						<X :size="16" :stroke-width="2" />
					</button>
				</header>

				<label>
					<span>显示名称</span>
					<input v-model.trim="modelDraft.name" required autocomplete="off" placeholder="例如 GPT-4.1" />
				</label>
				<label>
					<span>模型 id / deployment</span>
					<input v-model.trim="modelDraft.model" required autocomplete="off" class="mono" placeholder="例如 gpt-4.1" />
				</label>
				<label>
					<span>API 协议</span>
					<select v-model.number="modelDraft.apiFormat">
						<option v-for="format in provider?.supportedFormats || []" :key="format" :value="format">
							{{ formatNames[format] || `协议 ${format}` }}
						</option>
					</select>
				</label>

				<footer>
					<button type="button" class="secondary" @click="emit('close-model')">取消</button>
					<button type="submit" class="primary" :disabled="busy || !modelDraft.name || !modelDraft.model">
						{{ busy ? '保存中…' : '添加模型' }}
					</button>
				</footer>
			</form>
		</div>
	</Teleport>
</template>

<style scoped>
@import './settings-console.css';

.dialog-backdrop {
	position: fixed;
	inset: 0;
	z-index: 1200;
	display: grid;
	place-items: center;
	padding: 24px;
	background: rgba(23, 26, 31, 0.28);
	backdrop-filter: blur(4px);
	animation: sc-fade 160ms ease-out;
	font-family: var(--sc-sans);
}

.dialog {
	width: min(480px, 100%);
	max-height: min(640px, calc(100vh - 48px));
	overflow: auto;
	padding: 24px;
	border: 1px solid var(--sc-line-2);
	border-radius: 16px;
	background: var(--sc-panel);
	box-shadow: 0 32px 90px rgba(23, 26, 31, 0.2);
	color: var(--sc-text);
	animation: sc-pop 240ms var(--sc-ease-out);
}

header {
	display: flex;
	align-items: flex-start;
	justify-content: space-between;
	gap: 20px;
	margin-bottom: 20px;
}

.dlg-kicker {
	margin-bottom: 6px;
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: 9.5px;
	font-weight: 600;
	letter-spacing: 0.24em;
}

h3 {
	margin: 0;
	font-family: var(--sc-display);
	font-size: 19px;
	font-weight: 640;
	line-height: 1.3;
}

p {
	margin: 5px 0 0;
	color: var(--sc-mute);
	font-size: 12.5px;
	line-height: 1.5;
}

.close {
	display: grid;
	width: 30px;
	height: 30px;
	flex: 0 0 auto;
	place-items: center;
	border: 1px solid var(--sc-line);
	border-radius: 8px;
	background: transparent;
	color: var(--sc-mute);
	cursor: pointer;
	transition: background 0.15s, color 0.15s, border-color 0.15s;
}

.close:hover {
	border-color: var(--sc-line-2);
	background: var(--sc-hover);
	color: var(--sc-text);
}

.form-dialog {
	display: grid;
	gap: 15px;
}

.form-dialog header {
	margin-bottom: 4px;
}

.form-dialog label {
	display: grid;
	gap: 7px;
}

.form-dialog label > span {
	color: var(--sc-soft);
	font-family: var(--sc-mono);
	font-size: 10.5px;
	font-weight: 600;
	letter-spacing: 0.16em;
	text-transform: uppercase;
}

.form-dialog input,
.form-dialog select {
	width: 100%;
	box-sizing: border-box;
	height: 40px;
	padding: 0 12px;
	border: 1px solid var(--sc-line);
	border-radius: 9px;
	background: var(--sc-panel);
	color: var(--sc-text);
	font: inherit;
	font-size: 13.5px;
	transition: border-color 0.16s, box-shadow 0.16s;
}

.form-dialog input::placeholder {
	color: var(--sc-faint);
}

.form-dialog input:focus,
.form-dialog select:focus {
	border-color: color-mix(in srgb, var(--sc-acid) 55%, transparent);
	outline: none;
	box-shadow: 0 0 0 3px var(--sc-acid-soft);
}

.form-dialog select {
	appearance: none;
}

.form-dialog option {
	background: var(--sc-panel);
	color: var(--sc-text);
}

.hint {
	color: var(--sc-mute);
	font-size: 11.5px;
}

.mono {
	font-family: var(--sc-mono) !important;
	font-size: 12.5px !important;
	letter-spacing: 0.02em;
}

footer {
	display: flex;
	justify-content: flex-end;
	gap: 9px;
	margin-top: 6px;
}

footer button {
	height: 38px;
	padding: 0 18px;
	border-radius: 9px;
	font: inherit;
	font-size: 13px;
	font-weight: 600;
	cursor: pointer;
	transition: background 0.15s, border-color 0.15s, color 0.15s, opacity 0.15s, transform 0.12s;
}

.secondary {
	border: 1px solid var(--sc-line-2);
	background: var(--sc-panel);
	color: var(--sc-soft);
}

.secondary:hover {
	background: var(--sc-hover);
	color: var(--sc-text);
}

.primary {
	border: 1px solid var(--sc-acid);
	background: var(--sc-acid);
	color: var(--sc-acid-ink);
}

.primary:hover:not(:disabled) {
	transform: translateY(-1px);
	box-shadow: 0 8px 22px rgba(59, 91, 253, 0.2);
}

.primary:disabled {
	opacity: 0.45;
	cursor: not-allowed;
}

@media (prefers-reduced-motion: reduce) {
	.dialog-backdrop,
	.dialog {
		animation-duration: 0.001ms;
	}
}
</style>
