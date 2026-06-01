<script setup>
import { computed, nextTick, onMounted, onUnmounted, reactive, ref, watch } from 'vue';
import TranscriptPanel from './components/TranscriptPanel.vue';
import { renderMessages } from './renderers';

const state = reactive({
	items: [],
	conversations: [],
	selectedConversationId: null,
	theme: 'light',
	autoScroll: false,
	isBusy: false,
});

const transcriptPanelRef = ref(null);
const imagePreview = ref(null);
const openThoughts = ref(new Set());
const openToolSegments = ref(new Set());
const openToolGroups = ref(new Set());
const scrollFollowState = {
	transcript: true,
	transcriptPausedUntil: 0,
};

function post(message) {
	window.chrome?.webview?.postMessage(message);
}

const messagesHtml = computed(() =>
	renderMessages(state.items || [], openThoughts.value, openToolSegments.value, openToolGroups.value)
);

function applyTheme(theme) {
	const normalizedTheme = theme === 'dark' ? 'dark' : 'light';
	document.documentElement.dataset.theme = normalizedTheme;
}

function getTranscriptScrollEl() {
	return transcriptPanelRef.value?.getScrollEl?.() ?? null;
}

function snapshotScrollPosition(element) {
	return element
		? { top: element.scrollTop, nearBottom: element.scrollHeight - element.scrollTop - element.clientHeight < 40 }
		: null;
}

function restoreScrollPosition(element, snapshot) {
	if (!element || !snapshot || snapshot.nearBottom || shouldFollowTranscript()) {
		scrollTranscriptToBottom();
		return;
	}

	element.scrollTop = snapshot.top;
}

function scrollTranscriptToBottom() {
	const element = getTranscriptScrollEl();
	if (element) {
		element.scrollTop = element.scrollHeight;
	}
}

function pauseTranscriptFollow(durationMs = 1200) {
	scrollFollowState.transcript = false;
	scrollFollowState.transcriptPausedUntil = Date.now() + durationMs;
}

function shouldFollowTranscript() {
	if (Date.now() < scrollFollowState.transcriptPausedUntil) {
		return false;
	}

	return scrollFollowState.transcript;
}

function replaceState(payload) {
	const transcriptEl = getTranscriptScrollEl();
	const scrollSnapshot = snapshotScrollPosition(transcriptEl);

	state.items = Array.isArray(payload.items) ? payload.items : [];
	state.conversations = Array.isArray(payload.conversations) ? payload.conversations : [];
	state.selectedConversationId = payload.selectedConversationId || null;
	state.theme = payload.theme === 'dark' ? 'dark' : 'light';
	state.autoScroll = Boolean(payload.autoScroll);
	state.isBusy = Boolean(payload.isBusy);

	applyTheme(state.theme);

	nextTick(() => {
		const currentTranscriptEl = getTranscriptScrollEl();
		if (state.autoScroll || shouldFollowTranscript()) {
			scrollTranscriptToBottom();
			return;
		}

		restoreScrollPosition(currentTranscriptEl, scrollSnapshot);
	});
}

function handleIncomingMessage(event) {
	const payload = event?.data;
	if (!payload || typeof payload !== 'object') {
		return;
	}

	if (payload.type === 'replaceState') {
		replaceState(payload);
	}
}

function handleDocumentClick(event) {
	const link = event.target instanceof Element ? event.target.closest('a[href]') : null;
	if (!link) {
		return;
	}

	const href = link.getAttribute('href');
	if (!href) {
		return;
	}

	event.preventDefault();
	post({ type: 'open-link', href });
}

function onTranscriptScroll(event) {
	const target = event.target instanceof HTMLElement ? event.target : null;
	if (!target) {
		return;
	}

	const nearBottom = target.scrollHeight - target.scrollTop - target.clientHeight < 40;
	scrollFollowState.transcript = nearBottom;
	if (!nearBottom) {
		pauseTranscriptFollow();
	}
}

function openImagePreview(preview) {
	imagePreview.value = preview;
}

function closeImagePreview() {
	imagePreview.value = null;
}

function toggleSetEntry(source, id) {
	const next = new Set(source.value);
	if (next.has(id)) {
		next.delete(id);
	} else {
		next.add(id);
	}

	source.value = next;
}

function handleTranscriptAction(target) {
	const actionElement = target instanceof Element ? target.closest('[data-action]') : null;
	if (!actionElement) {
		return false;
	}

	switch (actionElement.getAttribute('data-action')) {
		case 'toggle-thinking': {
			const id = actionElement.getAttribute('data-thinking-id');
			if (id) {
				toggleSetEntry(openThoughts, id);
			}
			return true;
		}
		case 'toggle-tool-segment': {
			const id = actionElement.getAttribute('data-tool-segment-id');
			if (id) {
				toggleSetEntry(openToolSegments, id);
			}
			return true;
		}
		case 'toggle-tool-group': {
			const id = actionElement.getAttribute('data-tool-group-id');
			if (id) {
				toggleSetEntry(openToolGroups, id);
			}
			return true;
		}
		default:
			return false;
	}
}

function onTranscriptClick(event) {
	if (handleTranscriptAction(event.target)) {
		event.preventDefault();
	}
}

function onTranscriptKeydown(event) {
	if (event.key !== 'Enter' && event.key !== ' ') {
		return;
	}

	if (handleTranscriptAction(event.target)) {
		event.preventDefault();
	}
}

function onDocumentKeydown(event) {
	if (event.key === 'Escape' && imagePreview.value) {
		closeImagePreview();
	}
}

watch(
	() => state.theme,
	(theme) => applyTheme(theme),
	{ immediate: true },
);

onMounted(() => {
	window.chrome?.webview?.addEventListener('message', handleIncomingMessage);
	document.addEventListener('click', handleDocumentClick);
	document.addEventListener('keydown', onDocumentKeydown);
});

onUnmounted(() => {
	window.chrome?.webview?.removeEventListener('message', handleIncomingMessage);
	document.removeEventListener('click', handleDocumentClick);
	document.removeEventListener('keydown', onDocumentKeydown);
});
</script>

<template>
	<div class="app-simplified" :class="{ busy: state.isBusy }">
		<TranscriptPanel
			ref="transcriptPanelRef"
			:messages-html="messagesHtml"
			:items="state.items"
			:conversations="state.conversations"
			:selected-conversation-id="state.selectedConversationId"
			@scroll="onTranscriptScroll"
			@preview-image="openImagePreview"
			@transcript-click="onTranscriptClick"
			@transcript-keydown="onTranscriptKeydown"
		/>
		<div v-if="imagePreview" class="image-preview-backdrop" @click.self="closeImagePreview">
			<div class="image-preview-dialog">
				<img :src="imagePreview.src" :alt="imagePreview.alt || 'Preview image'" />
			</div>
		</div>
	</div>
</template>

<style>
@import './styles/00-simplified-transcript.css';
@import './styles/10-transcript-messages.css';

* {
	box-sizing: border-box;
}

html,
body,
#app {
	width: 100%;
	height: 100%;
	margin: 0;
	overflow: hidden;
}

body {
	background: var(--app-bg, #0b1220);
}

.app-simplified {
	width: 100%;
	height: 100%;
}

.transcript-panel {
	height: 100%;
	border: 0;
	border-radius: 0;
}

.image-preview-backdrop {
	position: fixed;
	inset: 0;
	display: flex;
	align-items: center;
	justify-content: center;
	padding: 24px;
	background: rgba(5, 10, 20, 0.72);
	backdrop-filter: blur(10px);
	z-index: 1000;
}

.image-preview-dialog img {
	display: block;
	max-width: min(96vw, 1600px);
	max-height: 92vh;
	border-radius: 18px;
	box-shadow: 0 24px 80px rgba(0, 0, 0, 0.35);
}
</style>
