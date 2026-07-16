<script setup>
defineProps({
	providerOpen: Boolean,
	modelOpen: Boolean,
	providers: { type: Array, default: () => [] },
	provider: { type: Object, default: null },
	modelDraft: { type: Object, required: true },
	busy: Boolean,
});

const emit = defineEmits(['close-provider', 'select-provider', 'close-model', 'submit-model']);

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
		<div v-if="providerOpen" class="dialog-backdrop" @click.self="emit('close-provider')">
			<section class="dialog" role="dialog" aria-modal="true" aria-labelledby="provider-dialog-title">
				<header>
					<div>
						<h3 id="provider-dialog-title">添加 AI 服务商</h3>
						<p>选择一个尚未创建连接的内置目录。</p>
					</div>
					<button type="button" class="close" aria-label="关闭" @click="emit('close-provider')">×</button>
				</header>

				<div class="provider-options">
					<button
						v-for="entry in providers"
						:key="entry.id"
						type="button"
						:disabled="busy"
						@click="emit('select-provider', entry)"
					>
						<span class="accent" :style="{ background: entry.color }"></span>
						<span><strong>{{ entry.name }}</strong><small>{{ entry.sub }}</small></span>
					</button>
				</div>
			</section>
		</div>

		<div v-if="modelOpen" class="dialog-backdrop" @click.self="emit('close-model')">
			<form class="dialog model-dialog" role="dialog" aria-modal="true" aria-labelledby="model-dialog-title" @submit.prevent="emit('submit-model')">
				<header>
					<div>
						<h3 id="model-dialog-title">手动添加模型</h3>
						<p>{{ provider?.name }} · 模型 id 将原样发送给 API。</p>
					</div>
					<button type="button" class="close" aria-label="关闭" @click="emit('close-model')">×</button>
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
.dialog-backdrop {
	position: fixed;
	inset: 0;
	z-index: 1200;
	display: grid;
	place-items: center;
	padding: 24px;
	background: rgba(10, 13, 18, 0.42);
	backdrop-filter: blur(4px);
	animation: fade-in 140ms ease-out;
}

.dialog {
	width: min(480px, 100%);
	max-height: min(640px, calc(100vh - 48px));
	overflow: auto;
	padding: 22px;
	border: 1px solid #e3e6eb;
	border-radius: 14px;
	background: #fff;
	box-shadow: 0 24px 70px rgba(12, 18, 28, 0.24);
	color: #20242b;
	font-family: var(--font-ui, system-ui, sans-serif);
	animation: rise-in 180ms cubic-bezier(.2, .75, .25, 1);
}

header {
	display: flex;
	align-items: flex-start;
	justify-content: space-between;
	gap: 20px;
	margin-bottom: 18px;
}

h3 { margin: 0; font-size: 18px; line-height: 1.35; }
p { margin: 4px 0 0; color: #727985; font-size: 12.5px; line-height: 1.5; }

.close {
	width: 30px;
	height: 30px;
	border: 0;
	border-radius: 7px;
	background: transparent;
	color: #747b86;
	font-size: 23px;
	line-height: 1;
	cursor: pointer;
}
.close:hover { background: #f1f3f6; color: #20242b; }

.provider-options { display: grid; gap: 4px; }
.provider-options button {
	display: flex;
	align-items: center;
	gap: 12px;
	width: 100%;
	padding: 11px 10px;
	border: 0;
	border-radius: 8px;
	background: transparent;
	text-align: left;
	cursor: pointer;
}
.provider-options button:hover { background: #f4f6f8; }
.provider-options button:disabled { opacity: .55; cursor: wait; }
.accent { width: 9px; height: 32px; border-radius: 99px; flex: none; }
.provider-options strong, .provider-options small { display: block; }
.provider-options strong { font-size: 13.5px; font-weight: 650; }
.provider-options small { margin-top: 2px; color: #7a818c; font-size: 11.5px; }

.model-dialog { display: grid; gap: 14px; }
.model-dialog header { margin-bottom: 0; }
.model-dialog label { display: grid; gap: 6px; }
.model-dialog label > span { font-size: 12px; font-weight: 650; color: #555d68; }
.model-dialog input, .model-dialog select {
	width: 100%;
	box-sizing: border-box;
	height: 38px;
	padding: 0 11px;
	border: 1px solid #d9dde3;
	border-radius: 8px;
	background: #fff;
	color: #20242b;
	font: inherit;
}
.model-dialog input:focus, .model-dialog select:focus { outline: 2px solid #85aaf5; outline-offset: 1px; }
.mono { font-family: ui-monospace, SFMono-Regular, Consolas, monospace !important; }

footer { display: flex; justify-content: flex-end; gap: 8px; margin-top: 4px; }
footer button { height: 36px; padding: 0 15px; border-radius: 8px; font: inherit; font-weight: 600; cursor: pointer; }
.secondary { border: 1px solid #d9dde3; background: #fff; color: #343a43; }
.primary { border: 1px solid #20242b; background: #20242b; color: #fff; }
.primary:disabled { opacity: .5; cursor: not-allowed; }

@keyframes fade-in { from { opacity: 0; } }
@keyframes rise-in { from { opacity: 0; transform: translateY(8px) scale(.985); } }
@media (prefers-reduced-motion: reduce) {
	.dialog-backdrop, .dialog { animation-duration: .001ms; }
}
</style>
