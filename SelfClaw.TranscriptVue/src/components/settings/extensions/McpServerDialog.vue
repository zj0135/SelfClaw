<script setup>
import { AlertTriangle, Plus, Save, Trash2, X } from 'lucide-vue-next';
import { computed, watch } from 'vue';
import { useMcpServerForm } from '../../../composables/useMcpServerForm';
import McpSecretFields from './McpSecretFields.vue';

const props = defineProps({
	open: { type: Boolean, default: false },
	server: { type: Object, default: null },
	saving: { type: Boolean, default: false },
});
const emit = defineEmits(['close', 'save']);
const {
	form,
	errors,
	reset,
	addArgument,
	removeArgument,
	addEntry,
	removeEntry,
	toCommand,
} = useMcpServerForm();

const riskyEndpoint = computed(() => {
	if (form.transport !== 'http' || !form.endpoint) return false;
	try {
		const endpoint = new URL(form.endpoint);
		return endpoint.protocol === 'http:' && !['localhost', '127.0.0.1', '[::1]'].includes(endpoint.hostname);
	} catch {
		return false;
	}
});
const managed = computed(() => Boolean(props.server?.sourcePluginId));

watch(
	() => [props.open, props.server],
	([open]) => {
		if (open) {
			reset(props.server);
			form.transport = props.server?.transport === 1 || props.server?.transport === 'http' ? 'http' : 'stdio';
		}
	},
	{ immediate: true },
);

function submit(testAfterSave = false) {
	const command = toCommand();
	if (command) emit('save', command, testAfterSave);
}
</script>

<template>
	<div v-if="open" class="backdrop" @click.self="$emit('close')">
		<form class="dialog sc-root" @submit.prevent="submit(false)">
			<header>
				<div>
					<span>MCP SERVER</span>
					<h2>{{ server ? '编辑服务器' : '新增服务器' }}</h2>
				</div>
				<button type="button" class="icon" title="关闭" @click="$emit('close')">
					<X :size="18" />
				</button>
			</header>

			<div class="scroll">
				<label class="field">
					<span>名称</span>
					<input v-model="form.displayName" autocomplete="off" placeholder="例如 GitHub" :disabled="managed" />
					<small v-if="errors.displayName">{{ errors.displayName }}</small>
				</label>

				<div class="field">
					<span>传输方式</span>
					<div class="segments">
						<button type="button" :class="{ active: form.transport === 'stdio' }" :disabled="managed"
							@click="form.transport = 'stdio'">STDIO</button>
						<button type="button" :class="{ active: form.transport === 'http' }" :disabled="managed"
							@click="form.transport = 'http'">HTTP</button>
					</div>
				</div>

				<template v-if="form.transport === 'stdio'">
					<label class="field">
						<span>启动命令</span>
						<input v-model="form.command" autocomplete="off" placeholder="node、npx、python..."
							:disabled="managed" />
						<small v-if="errors.command">{{ errors.command }}</small>
					</label>
					<div class="field">
						<div class="field-head"><span>参数</span><button v-if="!managed" type="button" title="添加参数"
								@click="addArgument">
								<Plus :size="14" />
							</button></div>
						<div v-for="(_, index) in form.arguments" :key="index" class="argument">
							<input v-model="form.arguments[index]" :placeholder="'参数 ' + (index + 1)"
								:disabled="managed" />
							<button v-if="!managed" type="button" title="删除参数" @click="removeArgument(index)">
								<Trash2 :size="14" />
							</button>
						</div>
					</div>
					<div class="two-columns">
						<label class="field">
							<span>工作目录</span>
							<select v-model="form.workingDirectoryMode" :disabled="managed">
								<option value="workspace">工作区</option>
								<option value="plugin">插件目录</option>
								<option value="appData">应用数据目录</option>
							</select>
						</label>
						<label class="check"><input v-model="form.requiresWorkspace" type="checkbox"
								:disabled="managed" />需要工作区</label>
					</div>
					<McpSecretFields title="环境变量" :rows="form.environment" :error="errors.environment"
						:managed="managed" @add="addEntry('environment')"
						@remove="removeEntry('environment', $event)" />
				</template>

				<template v-else>
					<label class="field">
						<span>Endpoint</span>
						<input v-model="form.endpoint" type="url" placeholder="https://mcp.example.com/api"
							:disabled="managed" />
						<small v-if="errors.endpoint">{{ errors.endpoint }}</small>
					</label>
					<div v-if="riskyEndpoint" class="warning">
						<AlertTriangle :size="15" />远程 HTTP 连接会被后端拒绝，请改用 HTTPS。
					</div>
					<div class="two-columns">
						<label class="field">
							<span>协议模式</span>
							<select v-model="form.transportMode" :disabled="managed">
								<option value="auto">自动检测</option>
								<option value="streamableHttp">Streamable HTTP</option>
								<option value="sse">SSE</option>
							</select>
						</label>
						<label class="field">
							<span>连接超时（秒）</span>
							<input v-model.number="form.connectionTimeoutSeconds" type="number" min="1" max="300"
								:disabled="managed" />
							<small v-if="errors.connectionTimeoutSeconds">{{ errors.connectionTimeoutSeconds }}</small>
						</label>
					</div>
					<McpSecretFields title="请求头" :rows="form.headers" :error="errors.headers" :managed="managed"
						@add="addEntry('headers')" @remove="removeEntry('headers', $event)" />
				</template>
			</div>

			<footer>
				<button type="button" class="secondary" @click="$emit('close')">取消</button>
				<button type="button" class="secondary" :disabled="saving" @click="submit(true)">保存并测试</button>
				<button type="submit" class="primary" :disabled="saving">
					<Save :size="15" />{{ saving ? '保存中' : '保存' }}
				</button>
			</footer>
		</form>
	</div>
</template>

<style scoped>
@import '../../../styles/settings-console.css';

.backdrop {
	position: fixed;
	inset: 0;
	z-index: 800;
	display: grid;
	place-items: center;
	padding: 22px;
	background: var(--overlay);
	backdrop-filter: blur(6px);
}

.dialog {
	display: flex;
	flex-direction: column;
	width: min(720px, 96vw);
	max-height: min(820px, 92vh);
	overflow: hidden;
	border: 1px solid var(--sc-line-2);
	border-radius: 8px;
	background: var(--sc-bg);
	box-shadow: 0 24px 70px var(--overlay-shadow);
	animation: sc-pop 180ms var(--sc-ease-out) both;
}

header,
footer {
	display: flex;
	align-items: center;
	justify-content: space-between;
	padding: 16px 18px;
	background: var(--sc-panel);
}

header {
	border-bottom: 1px solid var(--sc-line);
}

footer {
	justify-content: flex-end;
	gap: 8px;
	border-top: 1px solid var(--sc-line);
}

header span {
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
}

h2 {
	margin: 3px 0 0;
	font-size: var(--fs-18);
	letter-spacing: 0;
}

.scroll {
	display: grid;
	gap: 18px;
	padding: 18px;
	overflow-y: auto;
}

.field {
	display: grid;
	gap: 6px;
}

.field>span,
.field-head>span {
	color: var(--sc-mute);
	font-size: var(--fs-11);
	font-weight: 650;
}

.field input,
.field select,
.argument input {
	width: 100%;
	min-width: 0;
	height: 35px;
	padding: 0 9px;
	border: 1px solid var(--sc-line-2);
	border-radius: 5px;
	outline: 0;
	background: var(--sc-panel);
	color: var(--sc-text);
	font: inherit;
	font-size: var(--fs-12);
}

.field input:focus,
.field select:focus,
.argument input:focus {
	border-color: var(--sc-acid);
	box-shadow: 0 0 0 2px var(--sc-acid-soft);
}

small {
	color: var(--sc-err);
	font-size: var(--fs-105);
}

.segments {
	display: inline-flex;
	align-self: start;
	padding: 2px;
	border: 1px solid var(--sc-line-2);
	border-radius: 6px;
	background: var(--sc-panel);
}

.segments button {
	min-width: 80px;
	height: 28px;
	border: 0;
	border-radius: 4px;
	background: transparent;
	color: var(--sc-mute);
	font-size: var(--fs-11);
}

.segments button.active {
	background: var(--sc-raise);
	color: var(--sc-text);
	box-shadow: inset 0 0 0 1px var(--sc-line);
}

.field-head {
	display: flex;
	align-items: center;
	justify-content: space-between;
}

.field-head button,
.argument button,
.icon {
	display: inline-grid;
	place-items: center;
	width: 28px;
	height: 28px;
	padding: 0;
	border: 0;
	background: transparent;
	color: var(--sc-mute);
}

.argument {
	display: grid;
	grid-template-columns: minmax(0, 1fr) 28px;
	align-items: center;
	gap: 6px;
	margin-top: 6px;
}

.two-columns {
	display: grid;
	grid-template-columns: 1fr 1fr;
	align-items: end;
	gap: 14px;
}

.check {
	display: flex;
	align-items: center;
	height: 35px;
	gap: 7px;
	color: var(--sc-soft);
	font-size: var(--fs-115);
}

.check input {
	accent-color: var(--sc-acid);
}

.warning {
	display: flex;
	align-items: center;
	gap: 8px;
	padding: 9px 10px;
	border: 1px solid var(--caution-line);
	border-radius: 5px;
	background: var(--caution-tint);
	color: var(--caution-fill);
	font-size: var(--fs-11);
}

footer button {
	display: inline-flex;
	align-items: center;
	justify-content: center;
	height: 34px;
	gap: 6px;
	padding: 0 12px;
	border: 1px solid var(--sc-line-2);
	border-radius: 6px;
	background: transparent;
	color: var(--sc-soft);
}

footer .primary {
	border-color: var(--sc-acid);
	background: var(--sc-acid);
	color: var(--sc-acid-ink);
}

button:disabled {
	cursor: not-allowed;
	opacity: .45;
}

@media (max-width: 620px) {
	.two-columns {
		grid-template-columns: 1fr;
	}
}
</style>
