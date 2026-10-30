<script setup>
import { computed, defineAsyncComponent, ref, watch } from 'vue';
import ComposerPanel from '../components/chat/composer/ComposerPanel.vue';
import TodoPlanDock from '../components/chat/todo/TodoPlanDock.vue';
import AskUserDock from '../components/chat/question/AskUserDock.vue';
import ConversationQueue from '../components/chat/ConversationQueue.vue';
import ActivityStage from '../components/activities/ActivityStage.vue';
import TranscriptPanel from '../components/chat/TranscriptPanel.vue';
import { useHostBridge } from '../composables/hostBridge.js';
import { useTranscriptCollapse } from '../composables/useTranscriptCollapse.js';
import { useTranscriptScroll } from '../composables/useTranscriptScroll.js';
import { useChatTranscript } from '../composables/useChatTranscript.js';
import { useChatTurnStatus } from '../composables/useChatTurnStatus.js';
import { useChatTodo } from '../composables/useChatTodo.js';
import { useWorkspaceSelection } from '../composables/useWorkspaceSelection.js';
import { useChatComposer } from '../composables/useChatComposer.js';
import { useComposerAttachments } from '../composables/useComposerAttachments.js';
import { getSelectedModelSupportsImages } from '../composables/useComposerModelSelection.js';
import { useChatApprovals } from '../composables/useChatApprovals.js';
import { useChatQuestions } from '../composables/useChatQuestions.js';
import { useChatTerminal } from '../composables/useChatTerminal.js';
import { useConversationInputs } from '../composables/useConversationInputs.js';

const TerminalPanel = defineAsyncComponent(() => import('../components/chat/TerminalPanel.vue'));
const emit = defineEmits(['preview-image']);
const bridge = useHostBridge();
const transcriptPanelRef = ref(null);
const terminalPanelRef = ref(null);
const composerShellRef = ref(null);
const collapse = useTranscriptCollapse();
const transcriptScroll = useTranscriptScroll(() => transcriptPanelRef.value?.getScrollEl?.() ?? null);
const { state, isEmptyConversation } = useChatTranscript(transcriptScroll);
const workspace = useWorkspaceSelection(computed(() => state.selectedConversationId), bridge);
// 附件与提交共享同一个新对话 uuid：两者都在这里接上 transcript 的单会话选择。
const composerAttachments = useComposerAttachments(bridge, () => state.selectedConversationId);
const composer = useChatComposer(state, workspace, transcriptScroll, bridge, composerAttachments);
// 图片入口 = Direct 模式 + 所选模型声明多模态；极少数配置下宿主仍会拒绝，那走提交错误提示。
const imagesEnabled = computed(() => state.agentMode === 'direct' && getSelectedModelSupportsImages());
const conversationQueue = useConversationInputs(bridge);
watch(() => state.selectedConversationId, (id) => conversationQueue.subscribe(id), { immediate: true });
const approvals = useChatApprovals(bridge);
// `ask_user` 待答批次：有它时输入框暂停发送，问题 dock 顶在计划之上。
const questions = useChatQuestions(bridge);
const terminal = useChatTerminal(terminalPanelRef, bridge);
const turnStatus = useChatTurnStatus(state);
// 计划 dock 的折叠偏好按会话记忆，默认跟随忙碌态（忙碌展开、空闲收成一行）。
const chatTodo = useChatTodo(computed(() => state.selectedConversationId));

function openImagePreview(preview) { emit('preview-image', preview); }

function onWorkspaceInteraction(event) {
	if (event.target instanceof Element && event.target.closest('.terminal-panel')) return;
	terminal.setFocused(false);
}

defineExpose({
	browseWorkspaceFolder: workspace.browseFolder,
	insertPrompt: (text) => composerShellRef.value?.insertText?.(text),
});
</script>

<template>
	<div class="workspace" :class="{
		'empty-workspace': isEmptyConversation,
		'terminal-open': terminal.state.isOpen,
	}" @pointerdown="onWorkspaceInteraction" @focusin="onWorkspaceInteraction">
		<ActivityStage :parent-conversation-id="state.selectedConversationId" @preview-image="openImagePreview">
			<TranscriptPanel v-if="!isEmptyConversation" ref="transcriptPanelRef" :items="state.items"
				:collapse="collapse" :activity-text="state.activityText" :turn-status="turnStatus"
				@content-resize="transcriptScroll.onContentResize" @scroll="transcriptScroll.onScroll"
				@preview-image="openImagePreview" />
			<section v-else class="empty-composer-stage" aria-label="新对话">
				<div class="empty-composer-copy">
					<h1>想构建什么？</h1>
					<p>随意提问，或使用命令/工具。</p>
				</div>
			</section>
		</ActivityStage>
		<ComposerPanel ref="composerShellRef" :busy="state.isBusy || composer.submitting.value"
			:queueable="state.agentMode === 'direct'"
			:stoppable="!state.isContinuation"
			:workspace-selection="workspace.state" :git-loading="workspace.state.gitLoading"
			:git-error="workspace.state.gitError" :submit-error="composer.error.value" :agent-mode="state.agentMode"
			:selected-agent-id="state.selectedAgentId" :selected-agent-name="state.selectedAgentName"
			:capability-revision="state.capabilityRevision" :pending-approval="approvals.pending.value"
			:question-pending="Boolean(questions.pending.value)"
			:tool-permission-mode="state.toolPermissionMode" :usage="state.usage" @submit="composer.submit" @stop="composer.stop"
			:attachments="composerAttachments.items.value" :attachments-error="composerAttachments.error.value"
			:images-enabled="imagesEnabled"
			@request-attachment-picker="composerAttachments.addFromPicker()"
			@add-attachments="composerAttachments.addFromFiles($event)"
			@remove-attachment="composerAttachments.remove($event)"
			@preview-image="openImagePreview"
			@request-workspace="workspace.refresh" @select-workspace-root="workspace.selectRoot"
			@delete-workspace-root="workspace.deleteRoot" @browse-workspace-folder="workspace.browseFolder"
			@git-action="workspace.runGitAction" @approve-tool="(id) => approvals.resolve(id, true)"
			@reject-tool="(id) => approvals.resolve(id, false)"
			@select-permission-mode="composer.selectPermissionMode">
			<template #question>
				<AskUserDock :question="questions.pending.value" :resolving="questions.resolving.value"
					@answer="questions.answer($event.requestId, $event.answers)" @skip="questions.skip" />
			</template>
			<template #plan>
				<TodoPlanDock :todo="state.todo" :busy="state.isBusy" :open="chatTodo.isOpen(state.isBusy)"
					@toggle="chatTodo.toggle(state.isBusy)" />
			</template>
			<template #queue>
				<ConversationQueue v-if="state.agentMode === 'direct'"
					:state="conversationQueue.state"
					:editing="conversationQueue.editing.value"
					:notice="conversationQueue.notice.value"
					@cancel="(item) => conversationQueue.cancel(item.inputId, item.revision)"
					@retry="(item) => conversationQueue.retry(item.inputId, item.revision)"
					@promote="(item) => conversationQueue.promote(item.inputId, item.revision)"
					@edit="(item) => conversationQueue.edit(item.inputId)"
					@save-edit="conversationQueue.saveEdit()" @cancel-edit="conversationQueue.cancelEdit()"
					@update:prompt="conversationQueue.setEditPrompt($event)"
					@pause="conversationQueue.pause()" @resume="conversationQueue.resume()" />
			</template>
		</ComposerPanel>
		<TerminalPanel ref="terminalPanelRef" :is-open="terminal.state.isOpen" :is-running="terminal.state.isRunning"
			:cwd="terminal.state.cwd" @ready="terminal.ready" @input="terminal.input" @resize="terminal.resize"
			@close="terminal.close" @restart="terminal.restart" @focus-change="terminal.setFocused" />
	</div>
</template>

<style scoped>
.workspace {
	position: relative;
	width: 100%;
	height: 100%;
	display: grid;
	grid-template-rows: minmax(0, 1fr) auto 0;
	background: var(--panel);
	transition: grid-template-rows 850ms cubic-bezier(0.18, 0.86, 0.24, 1);
}

.workspace.empty-workspace {
	grid-template-rows: minmax(128px, 0.78fr) auto 0 minmax(160px, 1fr);
	align-items: stretch;
}

.workspace.terminal-open {
	grid-template-rows: minmax(0, 1fr) auto 286px;
}

.workspace.empty-workspace.terminal-open {
	grid-template-rows: minmax(96px, 1fr) auto 286px 0;
}

.empty-composer-stage {
	min-height: 0;
	display: flex;
	align-items: flex-end;
	justify-content: center;
	padding: 0 28px 28px;
	background: transparent;
}

.empty-composer-copy {
	text-align: center;
	animation: empty-rise 0.6s cubic-bezier(0.22, 1, 0.36, 1) both;
}

@keyframes empty-rise {
	from {
		opacity: 0;
		transform: translateY(14px);
	}

	to {
		opacity: 1;
		transform: none;
	}
}

.empty-composer-copy h1 {
	margin: 0;
	color: var(--text);
	font-family: var(--font-display);
	font-size: 42px;
	font-weight: 700;
	line-height: 1.1;
	letter-spacing: 0;
}

.empty-composer-copy p {
	margin: 12px 0 0;
	color: var(--faint);
	font-size: var(--fs-14);
	line-height: 1.6;
}

@media (max-width: 960px) {
	.workspace.empty-workspace {
		grid-template-rows: minmax(112px, 0.7fr) auto 0 minmax(140px, 1fr);
	}

	.workspace.empty-workspace.terminal-open {
		grid-template-rows: minmax(84px, 1fr) auto 286px 0;
	}

	.empty-composer-stage {
		padding-inline: 18px;
		padding-bottom: 22px;
	}

	.empty-composer-copy h1 {
		font-size: 30px;
	}
}
</style>
