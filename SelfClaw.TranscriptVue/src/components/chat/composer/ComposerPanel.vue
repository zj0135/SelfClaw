<script setup>
import { computed, ref, watch } from 'vue';
import { ArrowRight, LoaderCircle, Square } from 'lucide-vue-next';
import ComposerStatusBar from './ComposerStatusBar.vue';
import ModelSelector from './ModelSelector.vue';
import ComposerAddMenu from './ComposerAddMenu.vue';
import ComposerApprovalBar from './ComposerApprovalBar.vue';
import ComposerAttachments from './ComposerAttachments.vue';
import ComposerInput from './ComposerInput.vue';
import ComposerUsageMeter from './ComposerUsageMeter.vue';
import { isSupportedImageFile } from '../../../composables/useComposerAttachments.js';
import { createSkillChip } from '../../../renderers/composerText.js';

const props = defineProps({
	busy: {
		type: Boolean,
		default: false,
	},
	// A background continuation cannot be stopped: cancelling the attempt would only schedule a retry,
	// so the composer shows progress instead of a stop button.
	stoppable: {
		type: Boolean,
		default: true,
	},
	// Direct 忙碌时允许提交（进入队列）；CLI 忙碌时仍禁止提交。
	queueable: {
		type: Boolean,
		default: false,
	},
	workspaceSelection: {
		type: Object,
		default: () => ({}),
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
	// 有 `ask_user` 待答批次时禁止发送：回合被这一问卡住，输入只会排在后面等着。
	questionPending: {
		type: Boolean,
		default: false,
	},
	toolPermissionMode: {
		type: String,
		default: 'require-approval',
	},
	gitLoading: { type: Boolean, default: false },
	gitError: { type: String, default: '' },
	usage: { type: Object, default: null },
	submitError: { type: String, default: '' },
	// 待发送图片引用（宿主已落盘），由 ChatView 的 useComposerAttachments 持有。
	attachments: { type: Array, default: () => [] },
	attachmentsError: { type: String, default: '' },
	// 图片入口的唯一闸门：Direct 且所选模型多模态。前端不复制宿主的可用性逻辑。
	imagesEnabled: { type: Boolean, default: false },
	loadingHistory: { type: Boolean, default: false },
});

const emit = defineEmits([
	'submit',
	'stop',
	'request-workspace',
	'select-workspace-root',
	'delete-workspace-root',
	'browse-workspace-folder',
	'git-action',
	'approve-tool',
	'reject-tool',
	'select-permission-mode',
	'request-attachment-picker',
	'add-attachments',
	'remove-attachment',
	'preview-image',
]);

// composerText 是编辑器序列化后的发送文本（chip 已变成 `[/id]` / `@path` token）。
const composerText = ref('');
const workspaceMode = ref('local');
const shellRef = ref(null);
const inputRef = ref(null);

const hasText = computed(() => composerText.value.trim().length > 0);
const canSend = computed(() => hasText.value && (!props.busy || props.queueable) && !props.questionPending && !props.loadingHistory);
// 单一按钮：空闲显示发送；Direct 忙碌且输入框有内容时，同一位置变成发送（入队）；否则显示停止/进度。
const showSend = computed(() => !props.busy || (props.queueable && hasText.value));

// 待答期间输入框仍可打字（用户可能想边想边改草稿），只有发送被挡住。
const inputPlaceholder = computed(() => {
	if (props.loadingHistory) {
		return '正在载入历史对话…';
	}
	if (props.questionPending) {
		return '请先回答上方的问题';
	}
	return '让助手帮你处理项目…（@ 文件 · / 指令 · $ 技能）';
});

function submit() {
	// 键盘 Enter 也走这里，所以早退必须在取值之前。
	if (props.questionPending || props.loadingHistory) {
		return;
	}

	const prompt = composerText.value.trim();
	if (!prompt || (props.busy && !props.queueable)) {
		return;
	}

	emit('submit', {
		prompt,
		workspaceMode: workspaceMode.value,
		attachments: props.attachments,
		accept() {
			// 等待受理期间用户可能继续打字：只有内容没变才清空。
			if (composerText.value.trim() === prompt) {
				composerText.value = '';
				inputRef.value?.clear();
			}
		},
	});
}

function insertSkill(skillId) {
	inputRef.value?.insertChip(createSkillChip(skillId));
}

// 图片入口被禁时分别给出「模式」与「模型」两种原因，避免用户误会成文件问题。
const imagesDisabledReason = computed(() => props.agentMode === 'direct'
	? '当前模型不支持图片输入'
	: '当前模式不支持图片输入');

const dragging = ref(false);

function acceptsDrop(event) {
	const transfer = event.dataTransfer;
	if (!transfer) {
		return false;
	}

	if (transfer.files?.length) {
		return true;
	}

	return transfer.types ? Array.from(transfer.types).includes('Files') : false;
}

function imageFiles(files) {
	return Array.from(files || []).filter((file) => isSupportedImageFile(file));
}

function onComposerPaste(event) {
	if (!props.imagesEnabled) {
		return;
	}

	const files = imageFiles(event.clipboardData?.files);
	if (!files.length) {
		// 纯文本粘贴仍走默认行为（编辑器是 plaintext-only，富文本会被降级）。
		return;
	}

	event.preventDefault();
	emit('add-attachments', files);
}

function onComposerDragEnter(event) {
	if (!props.imagesEnabled || !acceptsDrop(event)) {
		return;
	}

	dragging.value = true;
}

function onComposerDragOver(event) {
	if (!props.imagesEnabled || !acceptsDrop(event)) {
		return;
	}

	event.preventDefault();
	if (event.dataTransfer) {
		event.dataTransfer.dropEffect = 'copy';
	}
}

function onComposerDragLeave(event) {
	// 在子元素间移动也会触发 dragleave：只有真正离开整块输入区时才熄灭高亮。
	const related = event?.relatedTarget;
	if (related instanceof Node && shellRef.value?.contains(related)) {
		return;
	}

	dragging.value = false;
}

function onComposerDrop(event) {
	dragging.value = false;
	if (!props.imagesEnabled) {
		return;
	}

	const files = imageFiles(event.dataTransfer?.files);
	if (!files.length) {
		return;
	}

	event.preventDefault();
	emit('add-attachments', files);
}

watch(
	() => [props.workspaceSelection?.current?.id, Boolean(props.workspaceSelection?.current?.isManagedWorktree)],
	([, isManaged], previous = []) => {
		if (isManaged) {
			workspaceMode.value = 'worktree';
		} else if (previous[0] !== props.workspaceSelection?.current?.id) {
			workspaceMode.value = 'local';
		}
	},
	{ immediate: true },
);

function stop() {
	emit('stop');
}

function requestWorkspace(refresh) {
	emit('request-workspace', Boolean(refresh));
}

function selectWorkspaceRoot(rootId) {
	emit('select-workspace-root', rootId);
}

function browseWorkspaceFolder() {
	emit('browse-workspace-folder');
}

function deleteWorkspaceRoot(workspaceRootId) {
	emit('delete-workspace-root', workspaceRootId);
}

function setText(text) {
	inputRef.value?.setText(text);
}

function focus() {
	inputRef.value?.focus();
}

defineExpose({
	setText,
	focus,
	insertSkill,
});
</script>

<template>
	<div ref="shellRef" class="composer-stack"
		@dragenter="onComposerDragEnter" @dragover="onComposerDragOver" @dragleave="onComposerDragLeave"
		@drop="onComposerDrop">
		<ComposerApprovalBar :approval="props.pendingApproval" @approve="emit('approve-tool', $event)"
			@reject="emit('reject-tool', $event)" />
		<slot name="question" />
		<slot name="plan" />
		<slot name="queue" />
		<section class="composer-shell" :class="{ dragging }" aria-label="消息输入">
			<ComposerAttachments :items="props.attachments" :error="props.attachmentsError"
				@remove="emit('remove-attachment', $event)" @preview-image="emit('preview-image', $event)" />
			<ComposerInput ref="inputRef" v-model="composerText" :placeholder="inputPlaceholder" :agent-mode="agentMode"
				:workspace-selection="workspaceSelection" :selected-agent-id="selectedAgentId"
				:selected-agent-name="selectedAgentName" :capability-revision="capabilityRevision"
				@submit="submit" @paste="onComposerPaste" />
			<p v-if="submitError" class="composer-error" role="alert">{{ submitError }}</p>
			<div class="composer-toolbar">
				<!-- 左侧：+ -->
				<div class="composer-tools-left">
					<ComposerAddMenu :skills-enabled="agentMode === 'direct'" :agent-id="selectedAgentId"
						:agent-name="selectedAgentName" :capability-revision="capabilityRevision"
						:images-enabled="props.imagesEnabled" :images-disabled-reason="imagesDisabledReason"
						@add-image="emit('request-attachment-picker')" @select-skill="insertSkill" />
				</div>
				<!-- 右侧：用量圆环百分比 / 模型 / 发送 -->
				<div class="composer-tools-right">
					<ComposerUsageMeter v-if="usage" :usage="usage" />
					<ModelSelector :execution-mode="agentMode" align="end" />
					<!-- 只保留一个按钮：空闲即发送；Direct 忙碌且输入框有内容时同一按钮变成发送（入队）；其余忙碌态显示停止或进度。 -->
					<button v-if="showSend" class="send-btn" type="button" :disabled="!canSend" title="发送"
						aria-label="发送" @click="submit">
						<ArrowRight :size="17" :stroke-width="2.2" aria-hidden="true" />
					</button>
					<button v-else-if="props.busy && props.stoppable" class="send-btn stop" type="button" title="停止生成"
						aria-label="停止生成" @click="stop">
						<Square :size="13" fill="currentColor" :stroke-width="0" aria-hidden="true" />
					</button>
					<span v-else class="send-btn background-busy" role="status" title="正在处理子代理结果" aria-label="正在处理子代理结果">
						<LoaderCircle :size="16" class="spin" aria-hidden="true" />
					</span>
				</div>
			</div>
		</section>
		<ComposerStatusBar :workspace-selection="workspaceSelection" :workspace-mode="workspaceMode"
			:git-loading="gitLoading" :git-error="gitError" :tool-permission-mode="toolPermissionMode"
			@update:workspace-mode="workspaceMode = $event"
			@select-permission-mode="emit('select-permission-mode', $event)"
			@refresh="requestWorkspace" @select-root="selectWorkspaceRoot" @delete-root="deleteWorkspaceRoot"
			@browse="browseWorkspaceFolder" @git-action="emit('git-action', $event)" />
	</div>
</template>

<style scoped>
.composer-stack {
	/* relative：状态栏项目下拉的定位锚点 */
	position: relative;
	/* 与 transcript 内容列同源（--chat-content-width + --chat-inline-inset）：
	   两条列在任何窗口宽度下同宽同轴，上下边缘严格对齐。 */
	width: min(calc(100% - var(--chat-inline-inset)), var(--chat-content-width));
	margin: 0 auto 16px;
	will-change: transform;
}

:global(.empty-workspace) .composer-stack {
	/* 空对话的欢迎页收窄一点（约 93%），聚焦输入；仍随断点同步伸缩。 */
	width: min(calc(100% - var(--chat-inline-inset)), calc(var(--chat-content-width) * 0.93));
	margin-bottom: 0;
}

.composer-shell {
	/* relative：快捷弹层（@ / $ / /）挂在它上方，水平跟随光标 */
	position: relative;
	width: 100%;
	min-height: 138px;
	display: flex;
	flex-direction: column;
	padding: 16px 18px 12px;
	border: 1px solid color-mix(in srgb, var(--text) 12%, transparent);
	border-radius: 16px 16px 0 0;
	background: var(--panel);
	box-shadow:
		0 1px 2px rgba(var(--shadow-ink), 0.04),
		0 8px 26px -4px rgba(var(--shadow-ink), 0.06);
	transition: border-color 0.18s, box-shadow 0.18s, background 0.18s;
}

/* 拖拽入框：整块输入区高亮 + 虚线内框，松手前就能看出落点。 */
.composer-shell.dragging {
	border-color: color-mix(in srgb, var(--accent) 62%, transparent);
	background: color-mix(in srgb, var(--accent) 5%, var(--panel));
	box-shadow:
		0 0 0 3px color-mix(in srgb, var(--accent) 12%, transparent),
		0 14px 34px rgba(var(--shadow-ink), 0.09);
}

.composer-shell.dragging::after {
	content: '';
	position: absolute;
	inset: 8px;
	pointer-events: none;
	border: 1.5px dashed color-mix(in srgb, var(--accent) 45%, transparent);
	border-radius: 11px;
}

.composer-shell:focus-within {
	border-color: color-mix(in srgb, var(--accent) 52%, transparent);
	box-shadow:
		0 1px 3px rgba(var(--shadow-ink), 0.05),
		0 16px 36px -6px rgba(var(--shadow-ink), 0.1),
		0 0 0 3px color-mix(in srgb, var(--accent) 12%, transparent);
}

:global(.empty-workspace) .composer-shell {
	min-height: 132px;
}

.composer-error {
	align-self: end;
	flex: none;
	margin: 2px 2px 7px;
	color: var(--err-text);
	font-size: var(--fs-115);
	line-height: 1.35;
}

.composer-toolbar {
	display: flex;
	align-items: center;
	justify-content: space-between;
	gap: 12px;
	min-height: 40px;
	padding-top: 4px;
	/* 幽灵按钮自带内边距，向左收回让「+」与输入文字左缘对齐 */
	margin-left: -6px;
}

.composer-tools-left,
.composer-tools-right {
	display: inline-flex;
	align-items: center;
	gap: 6px;
	min-width: 0;
}

.composer-tools-left {
	gap: 2px;
}

.send-btn {
	display: inline-flex;
	align-items: center;
	justify-content: center;
	width: 36px;
	height: 36px;
	border: 0;
	border-radius: 50%;
	background: linear-gradient(135deg, var(--accent) 0%, var(--accent-2) 100%);
	color: var(--accent-ink);
	cursor: pointer;
	box-shadow: 0 3px 12px color-mix(in srgb, var(--accent) 35%, transparent);
	transition:
		filter 0.16s,
		box-shadow 0.16s,
		opacity 0.15s,
		transform 0.12s cubic-bezier(0.34, 1.56, 0.64, 1);
}

.send-btn:hover {
	filter: brightness(1.08);
	box-shadow: 0 6px 18px color-mix(in srgb, var(--accent) 45%, transparent);
	transform: translateY(-1px);
}

.send-btn:active {
	transform: scale(0.94);
}

.send-btn:disabled {
	background: color-mix(in srgb, var(--text) 8%, transparent);
	color: var(--faint);
	box-shadow: none;
	opacity: 0.55;
	cursor: default;
	transform: none;
	filter: none;
}

.send-btn.stop {
	background: var(--text);
	box-shadow: none;
}

.send-btn.background-busy {
	background: transparent;
	color: var(--accent);
	box-shadow: none;
	cursor: default;
}

.send-btn.background-busy:hover {
	background: transparent;
	box-shadow: none;
	transform: none;
}

.spin {
	animation: composer-spin 1s linear infinite;
}

@keyframes composer-spin {
	to {
		transform: rotate(360deg);
	}
}

@media (prefers-reduced-motion: reduce) {
	.spin {
		animation: none;
	}
}
</style>
