<script setup>
import { X, Server, Layers, Plus } from 'lucide-vue-next';

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

// Keys mirror the AiProviderApiFormat enum; 3 is retired and intentionally absent.
const formatNames = {
	0: 'OpenAI Chat Completions',
	1: 'OpenAI Responses',
	2: 'Anthropic Messages',
	4: 'Ollama Native',
};
</script>

<template>
	<Teleport to="body">
		<!-- Add Provider Dialog -->
		<div v-if="providerOpen" class="dialog-backdrop sc-root" @click.self="emit('close-provider')">
			<div class="dialog-frame">
				<form class="dialog-form" role="dialog" aria-modal="true" aria-labelledby="provider-dialog-title"
					@submit.prevent="emit('submit-provider')">
					<header class="dialog-header">
						<div class="dh-icon-box">
							<Server :size="20" :stroke-width="2" />
						</div>
						<div class="dh-meta">
							<h3 id="provider-dialog-title">添加自定义服务商</h3>
						</div>
						<button type="button" class="close-icon-btn" aria-label="关闭" @click="emit('close-provider')">
							<X :size="16" :stroke-width="2" />
						</button>
					</header>

					<div class="dialog-content">
						<div class="form-group">
							<label class="field-label" for="new-prov-name">
								<span>服务商显示名称</span>
							</label>
							<div class="input-wrap">
								<input id="new-prov-name" v-model.trim="providerDraft.name" required autocomplete="off"
									placeholder="例如 我的私有网关 / OpenRouter" class="styled-input" />
							</div>
						</div>

						<div class="form-group">
							<label class="field-label" for="new-prov-proto">
								<span>协议类型</span>
							</label>
							<div class="select-wrap">
								<select id="new-prov-proto" v-model="providerDraft.protocolId" class="styled-select">
									<option v-for="protocol in protocols" :key="protocol.id" :value="protocol.id">
										{{ protocol.label }}
									</option>
								</select>
							</div>
						</div>

						<div class="form-group">
							<label class="field-label" for="new-prov-base">
								<span>API 基础地址 (Base URL)</span>
							</label>
							<div class="input-wrap">
								<input id="new-prov-base" v-model.trim="providerDraft.base" required autocomplete="off"
									class="styled-input mono" placeholder="https://api.example.com/v1" />
							</div>
						</div>
					</div>

					<footer class="dialog-footer">
						<button type="button" class="btn btn-secondary" @click="emit('close-provider')">取消</button>
						<button type="submit" class="btn btn-primary"
							:disabled="busy || !providerDraft.name || !providerDraft.base || !providerDraft.protocolId">
							<Plus :size="15" :stroke-width="2.2" />
							<span>{{ busy ? '添加中…' : '添加服务商' }}</span>
						</button>
					</footer>
				</form>
			</div>
		</div>

		<!-- Add Model Dialog -->
		<div v-if="modelOpen" class="dialog-backdrop sc-root" @click.self="emit('close-model')">
			<div class="dialog-frame">
				<form class="dialog-form" role="dialog" aria-modal="true"
					aria-labelledby="model-dialog-title" @submit.prevent="emit('submit-model')">
					<header class="dialog-header">
						<div class="dh-icon-box">
							<Layers :size="20" :stroke-width="2" />
						</div>
						<div class="dh-meta">
							<h3 id="model-dialog-title">手动添加模型</h3>
						</div>
						<button type="button" class="close-icon-btn" aria-label="关闭" @click="emit('close-model')">
							<X :size="16" :stroke-width="2" />
						</button>
					</header>

					<div class="dialog-content">
						<div class="form-group">
							<label class="field-label" for="new-model-name">
								<span>显示名称</span>
							</label>
							<div class="input-wrap">
								<input id="new-model-name" v-model.trim="modelDraft.name" required autocomplete="off"
									placeholder="例如 Claude 3.7 Sonnet / DeepSeek V3" class="styled-input" />
							</div>
						</div>

						<div class="form-group">
							<label class="field-label" for="new-model-id">
								<span>模型 ID / 部署名 (Deployment ID)</span>
							</label>
							<div class="input-wrap">
								<input id="new-model-id" v-model.trim="modelDraft.model" required autocomplete="off"
									class="styled-input mono" placeholder="例如 claude-3-7-sonnet-20250219" />
							</div>
						</div>

						<div class="form-group">
							<label class="field-label" for="new-model-format">
								<span>API 格式标准</span>
							</label>
							<div class="select-wrap">
								<select id="new-model-format" v-model.number="modelDraft.apiFormat" class="styled-select">
									<option v-for="format in provider?.supportedFormats || []" :key="format" :value="format">
										{{ formatNames[format] || `协议标准 ${format}` }}
									</option>
								</select>
							</div>
						</div>
					</div>

					<footer class="dialog-footer">
						<button type="button" class="btn btn-secondary" @click="emit('close-model')">取消</button>
						<button type="submit" class="btn btn-primary" :disabled="busy || !modelDraft.name || !modelDraft.model">
							<Plus :size="15" :stroke-width="2.2" />
							<span>{{ busy ? '保存中…' : '添加模型' }}</span>
						</button>
					</footer>
				</form>
			</div>
		</div>
	</Teleport>
</template>

<style scoped>
@import '../../../styles/settings-console.css';

.dialog-backdrop {
	position: fixed;
	inset: 0;
	z-index: 1200;
	display: grid;
	place-items: center;
	padding: 24px;
	background: var(--overlay-heavy);
	backdrop-filter: blur(12px);
	animation: sc-fade 0.18s var(--sc-ease-out);
	font-family: var(--sc-sans);
}

.dialog-frame {
	position: relative;
	width: min(520px, calc(100vw - 32px));
	max-height: min(680px, calc(100vh - 48px));
	border: 1px solid var(--sc-line-2);
	border-radius: 16px;
	background: var(--sc-panel);
	box-shadow: 0 32px 100px rgba(var(--shadow-ink), 0.35), 0 4px 20px rgba(var(--shadow-ink), 0.1);
	overflow: hidden;
	animation: sc-pop 0.22s var(--sc-ease-spring);
}

.dialog-form {
	display: flex;
	flex-direction: column;
	max-height: inherit;
}

.dialog-header {
	display: flex;
	align-items: center;
	gap: 14px;
	padding: 22px 24px 18px;
	border-bottom: 1px solid var(--sc-line);
	background: linear-gradient(to bottom, var(--sc-raise), transparent);
}

.dh-icon-box {
	display: grid;
	place-items: center;
	width: 42px;
	height: 42px;
	border: 1px solid var(--sc-line-2);
	border-radius: 10px;
	background: var(--sc-panel);
	color: var(--sc-acid);
	box-shadow: 0 4px 12px color-mix(in srgb, var(--sc-acid) 12%, transparent);
}

.dh-meta {
	flex: 1;
	min-width: 0;
}

.dlg-kicker {
	margin-bottom: 3px;
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
	font-weight: 650;
	letter-spacing: 0.18em;
}

h3 {
	margin: 0;
	font-family: var(--sc-display);
	font-size: var(--fs-18);
	font-weight: 650;
	letter-spacing: 0.01em;
	color: var(--sc-text);
}

.close-icon-btn {
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

.close-icon-btn:hover {
	color: var(--sc-text);
	border-color: var(--sc-line-2);
	background: var(--sc-hover);
	transform: scale(1.05);
}

.dialog-content {
	flex: 1;
	display: flex;
	flex-direction: column;
	gap: 18px;
	padding: 22px 24px;
	overflow-y: auto;
}

.form-group {
	display: flex;
	flex-direction: column;
	gap: 6px;
}

.field-label {
	display: flex;
	align-items: baseline;
	justify-content: space-between;
	color: var(--sc-text);
	font-size: var(--fs-12);
	font-weight: 600;
}

.sub-tag {
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
	letter-spacing: 0.12em;
}

.styled-input,
.styled-select {
	width: 100%;
	height: 38px;
	padding: 0 12px;
	border: 1px solid var(--sc-line-2);
	border-radius: 8px;
	background: var(--sc-panel);
	color: var(--sc-text);
	font-family: inherit;
	font-size: var(--fs-13);
	outline: none;
	transition: border-color 0.16s, box-shadow 0.16s;
}

.styled-input.mono {
	font-family: var(--sc-mono);
	font-size: var(--fs-12);
}

.styled-input:focus,
.styled-select:focus {
	border-color: var(--sc-acid);
	box-shadow: 0 0 0 2px var(--sc-acid-soft);
}

.field-hint {
	margin-top: 4px;
	color: var(--sc-mute);
	font-size: var(--fs-11);
}

.dialog-footer {
	display: flex;
	align-items: center;
	justify-content: flex-end;
	gap: 10px;
	padding: 16px 24px;
	border-top: 1px solid var(--sc-line);
	background: var(--sc-raise);
}

.btn {
	display: inline-flex;
	align-items: center;
	justify-content: center;
	gap: 7px;
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

.btn-primary:hover:not(:disabled) {
	transform: translateY(-1px);
	box-shadow: 0 3px 8px rgba(var(--shadow-ink), 0.12);
}

.btn-primary:disabled {
	opacity: 0.5;
	cursor: not-allowed;
	transform: none;
}
</style>
