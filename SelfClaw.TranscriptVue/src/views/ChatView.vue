<script setup>
import { computed, defineAsyncComponent, ref } from 'vue';
import ComposerPanel from '../components/Chat/ComposerPanel.vue';
import ActivityStage from '../components/Activities/ActivityStage.vue';
import TranscriptPanel from '../components/Chat/TranscriptPanel.vue';
import { useHostBridge } from '../composables/hostBridge.js';
import { useTranscriptCollapse } from '../composables/useTranscriptCollapse.js';
import { useTranscriptScroll } from '../composables/useTranscriptScroll.js';
import { useChatTranscript } from '../composables/useChatTranscript.js';
import { useChatTurnStatus } from '../composables/useChatTurnStatus.js';
import { useWorkspaceSelection } from '../composables/useWorkspaceSelection.js';
import { useChatComposer } from '../composables/useChatComposer.js';
import { useChatApprovals } from '../composables/useChatApprovals.js';
import { useChatTerminal } from '../composables/useChatTerminal.js';

const TerminalPanel = defineAsyncComponent(() => import('../components/Chat/TerminalPanel.vue'));
const emit = defineEmits(['preview-image']);
const bridge = useHostBridge();
const transcriptPanelRef = ref(null);
const terminalPanelRef = ref(null);
const composerShellRef = ref(null);
const collapse = useTranscriptCollapse();
const transcriptScroll = useTranscriptScroll(() => transcriptPanelRef.value?.getScrollEl?.() ?? null);
const { state, isEmptyConversation } = useChatTranscript(transcriptScroll, bridge);
const workspace = useWorkspaceSelection(computed(() => state.selectedConversationId), bridge);
const composer = useChatComposer(state, workspace, transcriptScroll, bridge);
const approvals = useChatApprovals(bridge);
const terminal = useChatTerminal(terminalPanelRef, bridge);
const turnStatus = useChatTurnStatus(state);

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
		<TranscriptPanel v-if="!isEmptyConversation" ref="transcriptPanelRef" :items="state.items" :collapse="collapse"
			:activity-text="state.activityText" :turn-status="turnStatus"
			@content-resize="transcriptScroll.onContentResize" @scroll="transcriptScroll.onScroll"
			@preview-image="openImagePreview" />
		<section v-else class="empty-composer-stage" aria-label="新对话">
			<div class="empty-composer-copy">
				<div class="empty-kicker">SELFCLAW · READY</div>
				<h1>想聊些什么？</h1>
				<p>随意提问，或使用命令/工具。</p>
			</div>
		</section>
		</ActivityStage>
		<ComposerPanel ref="composerShellRef" :busy="state.isBusy || composer.submitting.value"
			:workspace-selection="workspace.state" :git-loading="workspace.state.gitLoading"
			:git-error="workspace.state.gitError" :submit-error="composer.error.value" :agent-mode="state.agentMode"
			:selected-agent-id="state.selectedAgentId" :selected-agent-name="state.selectedAgentName"
			:capability-revision="state.capabilityRevision" :pending-approval="approvals.pending.value"
			:tool-permission-mode="state.toolPermissionMode" @submit="composer.submit" @stop="composer.stop"
			@request-workspace="workspace.refresh" @select-workspace-root="workspace.selectRoot"
			@delete-workspace-root="workspace.deleteRoot" @browse-workspace-folder="workspace.browseFolder"
			@git-action="workspace.runGitAction" @approve-tool="(id) => approvals.resolve(id, true)"
			@reject-tool="(id) => approvals.resolve(id, false)" @select-permission-mode="composer.selectPermissionMode" />
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

.empty-kicker {
	margin-bottom: 14px;
	color: var(--faint);
	font-family: var(--font-mono);
	font-size: var(--fs-10);
	font-weight: 600;
	letter-spacing: 0;
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
