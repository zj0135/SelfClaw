<script setup>
import { computed, nextTick, ref } from 'vue';
import { SlidersHorizontal, ArrowRight, Square, ShieldAlert, Check, X } from 'lucide-vue-next';
import ModelSelector from './ModelSelector.vue';
import WorkspaceSelector from './WorkspaceSelector.vue';
import SkillPicker from './SkillPicker.vue';

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
	selectedAgentId: { type: String, default: '' },
	selectedAgentName: { type: String, default: '' },
	capabilityRevision: { type: Number, default: 0 },
	pendingApproval: {
		type: Object,
		default: null,
	},
});

const emit = defineEmits([
	'submit',
	'stop',
	'request-workspace',
	'select-workspace-root',
	'select-workspace-path',
	'browse-workspace-folder',
	'approve-tool',
	'reject-tool',
]);

const approvalTitle = computed(() => {
	const approval = props.pendingApproval;
	if (!approval) {
		return '';
	}

	return approval.displayName || approval.toolName || '工具调用';
});

// 参数原文可能很长，状态栏里只给一行预览，展开细节仍留在对话流的工具卡片里。
const approvalDetail = computed(() => {
	const approval = props.pendingApproval;
	if (!approval) {
		return '';
	}

	const raw = (approval.argumentsJson || approval.description || '').replace(/\s+/g, ' ').trim();
	if (raw.length <= 120) {
		return raw;
	}

	return `${raw.slice(0, 120)}…`;
});

const approvalSource = computed(() => {
	const approval = props.pendingApproval;
	if (!approval?.sourceId) return '';
	const numericLabels = { 1: 'MCP', 2: 'Skill', 3: 'Plugin' };
	const kind = typeof approval.sourceKind === 'string'
		? approval.sourceKind
		: numericLabels[approval.sourceKind] || '扩展';
	return `${kind.toUpperCase()} · ${approval.sourceId}${approval.transportSummary ? ` · ${approval.transportSummary}` : ''}`;
});

function approveTool() {
	if (!props.pendingApproval) {
		return;
	}

	emit('approve-tool', props.pendingApproval.toolExecutionId);
}

function rejectTool() {
	if (!props.pendingApproval) {
		return;
	}

	emit('reject-tool', props.pendingApproval.toolExecutionId);
}

const composerText = ref('');
const shellRef = ref(null);
const textareaRef = ref(null);

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

async function insertSkillToken(skillId) {
	const textarea = textareaRef.value;
	const start = textarea?.selectionStart ?? composerText.value.length;
	const end = textarea?.selectionEnd ?? start;
	const token = `[/${skillId}] `;
	composerText.value = composerText.value.slice(0, start) + token + composerText.value.slice(end);
	await nextTick();
	textareaRef.value?.focus();
	textareaRef.value?.setSelectionRange(start + token.length, start + token.length);
}

defineExpose({
	getShellEl: () => shellRef.value,
});
</script>

<template>
	<div class="composer-stack">
		<transition name="approval-bar">
			<div v-if="props.pendingApproval" class="tool-approval-bar" role="alertdialog" aria-label="工具调用确认">
				<span class="tool-approval-icon" aria-hidden="true">
					<ShieldAlert :size="16" :stroke-width="1.9" />
				</span>
				<div class="tool-approval-copy">
					<span class="tool-approval-title">
						请求执行 <strong>{{ approvalTitle }}</strong>
					</span>
					<span v-if="approvalSource" class="tool-approval-source">{{ approvalSource }}</span>
					<span v-if="approvalDetail" class="tool-approval-detail" :title="approvalDetail">{{ approvalDetail }}</span>
				</div>
				<div class="tool-approval-actions">
					<button class="tool-approval-btn reject" type="button" title="拒绝" @click="rejectTool">
						<X :size="14" :stroke-width="2.2" aria-hidden="true" />
						拒绝
					</button>
					<button class="tool-approval-btn approve" type="button" title="允许" @click="approveTool">
						<Check :size="14" :stroke-width="2.4" aria-hidden="true" />
						允许
					</button>
				</div>
			</div>
		</transition>
		<section ref="shellRef" class="composer-shell" aria-label="消息输入">
			<div class="composer-grip" aria-hidden="true"></div>
			<textarea
				ref="textareaRef"
				v-model="composerText"
				class="composer-input"
				rows="3"
				placeholder="让助手帮你处理项目..."
				@keydown="onKeydown"
			></textarea>
			<div class="composer-toolbar">
			<div class="composer-tools-left">
			<ModelSelector :execution-mode="agentMode" />
			<SkillPicker
				v-if="agentMode === 'direct'"
				:agent-id="selectedAgentId"
				:agent-name="selectedAgentName"
				:capability-revision="capabilityRevision"
				@select="insertSkillToken"
			/>
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
	</div>
</template>

<style scoped>
.composer-stack {
	width: min(calc(100% - 72px), 728px);
	margin: 0 auto 16px;
}

:global(.empty-workspace) .composer-stack {
	width: min(calc(100% - 72px), 680px);
	margin-bottom: 0;
}

.composer-shell {
	position: relative;
	width: 100%;
	min-height: 138px;
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

:global(.empty-workspace) .composer-shell {
	min-height: 132px;
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

/* ===== 工具调用确认栏（输入框上方，需要用户允许/拒绝 Direct 写操作时出现） ===== */
.tool-approval-bar {
	display: flex;
	align-items: center;
	gap: 10px;
	margin-bottom: 10px;
	padding: 9px 10px 9px 12px;
	border: 1px solid rgba(200, 122, 20, 0.32);
	border-radius: 11px;
	background: rgba(251, 191, 84, 0.12);
}

.tool-approval-icon {
	display: inline-grid;
	place-items: center;
	width: 26px;
	height: 26px;
	flex: none;
	border-radius: 8px;
	color: #b26a09;
	background: rgba(240, 165, 60, 0.2);
}

.tool-approval-copy {
	min-width: 0;
	flex: 1 1 auto;
	display: flex;
	flex-direction: column;
	gap: 1px;
}

.tool-approval-title {
	max-width: 100%;
	overflow: hidden;
	color: #4a3410;
	font-size: 12.5px;
	line-height: 1.4;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.tool-approval-title strong {
	font-weight: 650;
	color: #33240a;
}

.tool-approval-detail {
	max-width: 100%;
	overflow: hidden;
	color: #8a7343;
	font-family: var(--font-mono, ui-monospace, monospace);
	font-size: 11px;
	line-height: 1.35;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.tool-approval-source {
	color: #6f5b2e;
	font-size: 10px;
	font-weight: 600;
}

.tool-approval-actions {
	display: inline-flex;
	align-items: center;
	gap: 6px;
	flex: none;
}

.tool-approval-btn {
	display: inline-flex;
	align-items: center;
	gap: 4px;
	height: 30px;
	padding: 0 12px;
	border: 1px solid transparent;
	border-radius: 8px;
	font-size: 12.5px;
	font-weight: 600;
	line-height: 1;
	transition: background 0.15s, border-color 0.15s, color 0.15s;
}

.tool-approval-btn.reject {
	border-color: #d9dde4;
	background: #ffffff;
	color: #5f6a78;
}

.tool-approval-btn.reject:hover {
	border-color: #c7ccd5;
	color: #3d4654;
}

.tool-approval-btn.approve {
	background: #b26a09;
	color: #ffffff;
}

.tool-approval-btn.approve:hover {
	background: #9a5a06;
}

.approval-bar-enter-active,
.approval-bar-leave-active {
	transition: opacity 0.16s ease, transform 0.16s ease;
}

.approval-bar-enter-from,
.approval-bar-leave-to {
	opacity: 0;
	transform: translateY(4px);
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
	.composer-stack {
		width: calc(100% - 28px);
	}

	:global(.empty-workspace) .composer-stack {
		width: calc(100% - 28px);
	}
}
</style>
