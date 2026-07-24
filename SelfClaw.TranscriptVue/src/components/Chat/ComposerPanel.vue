<script setup>
import { computed, ref } from 'vue';
import { SlidersHorizontal, ArrowRight, Square } from 'lucide-vue-next';
import ModelSelector from './ModelSelector.vue';
import WorkspaceSelector from './WorkspaceSelector.vue';

const props = defineProps({
	busy: {
		type: Boolean,
		default: false,
	},
	workspaceSelection: {
		type: Object,
		default: () => ({}),
	},
	workspaceLoading: {
		type: Boolean,
		default: false,
	},
	agentMode: {
		type: String,
		default: 'cli',
	},
});

const emit = defineEmits([
	'submit',
	'stop',
	'request-workspace',
	'select-workspace-root',
	'select-workspace-path',
	'browse-workspace-folder',
]);

const composerText = ref('');
const shellRef = ref(null);

const canSend = computed(() => composerText.value.trim().length > 0 && !props.busy);

function submit() {
	const prompt = composerText.value.trim();
	if (!prompt || props.busy) {
		return;
	}

	emit('submit', prompt);
	composerText.value = '';
}

function stop() {
	emit('stop');
}

function requestWorkspace(refresh) {
	emit('request-workspace', Boolean(refresh));
}

function selectWorkspaceRoot(rootId) {
	emit('select-workspace-root', rootId);
}

function selectWorkspacePath(rootPath) {
	emit('select-workspace-path', rootPath);
}

function browseWorkspaceFolder() {
	emit('browse-workspace-folder');
}

function onKeydown(event) {
	if (event.key !== 'Enter' || event.shiftKey || event.ctrlKey || event.altKey || event.metaKey) {
		return;
	}

	event.preventDefault();
	submit();
}

defineExpose({
	getShellEl: () => shellRef.value,
});
</script>

<template>
	<section ref="shellRef" class="composer-shell" aria-label="消息输入">
		<div class="composer-grip" aria-hidden="true"></div>
		<textarea
			v-model="composerText"
			class="composer-input"
			rows="3"
			placeholder="让助手帮你处理项目..."
			@keydown="onKeydown"
		></textarea>
		<div class="composer-toolbar">
			<div class="composer-tools-left">
			<ModelSelector :execution-mode="agentMode" />
			<button class="icon-btn" type="button" title="功能" aria-label="功能">
				<SlidersHorizontal :size="16" :stroke-width="1.8" aria-hidden="true" />
			</button>
				<WorkspaceSelector
					:workspace-selection="workspaceSelection"
					:loading="workspaceLoading"
					@refresh="requestWorkspace"
					@select-root="selectWorkspaceRoot"
					@select-path="selectWorkspacePath"
					@browse="browseWorkspaceFolder"
				/>
			</div>
			<div class="composer-tools-right">
			<button v-if="props.busy" class="send-btn stop" type="button" title="停止生成" aria-label="停止生成" @click="stop">
				<Square :size="13" fill="currentColor" :stroke-width="0" aria-hidden="true" />
			</button>
			<button v-else class="send-btn" type="button" :disabled="!canSend" title="发送" aria-label="发送" @click="submit">
				<ArrowRight :size="17" :stroke-width="2.2" aria-hidden="true" />
			</button>
			</div>
		</div>
	</section>
</template>

<style>
.composer-shell {
	position: relative;
	width: min(calc(100% - 72px), 728px);
	min-height: 138px;
	margin: 0 auto 16px;
	display: grid;
	grid-template-rows: 1fr auto;
	padding: 22px 18px 12px;
	border: 1px solid rgba(19, 27, 45, 0.1);
	border-radius: 16px;
	background: #ffffff;
	box-shadow:
		0 1px 2px rgba(23, 26, 31, 0.05),
		0 8px 24px rgba(23, 26, 31, 0.04);
	transition: border-color 0.18s, box-shadow 0.18s;
	will-change: transform;
}

.composer-shell:focus-within {
	border-color: rgba(59, 91, 253, 0.45);
	box-shadow:
		0 1px 2px rgba(23, 26, 31, 0.05),
		0 12px 32px rgba(23, 26, 31, 0.07),
		0 0 0 3px rgba(59, 91, 253, 0.1);
}

.empty-workspace .composer-shell {
	width: min(calc(100% - 72px), 680px);
	min-height: 132px;
	margin-bottom: 0;
}

.composer-grip {
	position: absolute;
	top: 6px;
	left: 50%;
	width: 36px;
	height: 3.5px;
	border-radius: 99px;
	background: #dde1e7;
	transform: translateX(-50%);
}

.composer-input {
	width: 100%;
	min-height: 64px;
	resize: none;
	padding: 0 2px;
	border: 0;
	outline: none;
	background: transparent;
	color: #20242a;
	font: 14px/1.65 var(--font-ui);
}

.composer-input::placeholder {
	color: #8f9aab;
}

.composer-input:disabled {
	opacity: 0.68;
}

.composer-toolbar {
	display: flex;
	align-items: center;
	justify-content: space-between;
	gap: 12px;
	min-height: 40px;
	padding-top: 4px;
}

.composer-tools-left,
.composer-tools-right {
	display: inline-flex;
	align-items: center;
	gap: 6px;
	min-width: 0;
}

.icon-btn {
	display: inline-flex;
	align-items: center;
	justify-content: center;
	width: 32px;
	height: 32px;
	padding: 0;
	border: 0;
	border-radius: 8px;
	background: transparent;
	color: #6b7280;
	cursor: pointer;
	transition:
		background 0.15s,
		color 0.15s;
}

.icon-btn:hover {
	background: #f3f4f6;
	color: #171a1f;
}

.send-btn {
	display: inline-flex;
	align-items: center;
	justify-content: center;
	width: 36px;
	height: 36px;
	border: 0;
	border-radius: 50%;
	background: var(--accent, #3b5bfd);
	color: #ffffff;
	cursor: pointer;
	box-shadow: 0 4px 14px rgba(59, 91, 253, 0.3);
	transition:
		background 0.16s,
		box-shadow 0.16s,
		opacity 0.15s,
		transform 0.12s cubic-bezier(0.34, 1.56, 0.64, 1);
}

.send-btn:hover {
	background: var(--accent-2, #2f49d1);
	box-shadow: 0 8px 20px rgba(59, 91, 253, 0.36);
	transform: translateY(-1px);
}

.send-btn:active {
	transform: scale(0.94);
}

.send-btn:disabled {
	background: #c3c9d4;
	box-shadow: none;
	opacity: 0.55;
	cursor: default;
	transform: none;
}

.send-btn.stop {
	background: #171a1f;
	box-shadow: none;
}

@media (max-width: 960px) {
	.composer-shell {
		width: calc(100% - 28px);
	}

	.empty-workspace .composer-shell {
		width: calc(100% - 28px);
	}
}
</style>
