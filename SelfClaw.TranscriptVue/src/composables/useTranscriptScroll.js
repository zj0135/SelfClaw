import { onUnmounted } from 'vue';

const BOTTOM_THRESHOLD_PX = 40;

export function useTranscriptScroll(getScrollElement) {
	let followsLatest = true;
	let selectedConversationId = null;
	let pendingInitialConversationId = null;
	let scrollFrame = null;

	function isNearBottom(element) {
		return element.scrollHeight - element.scrollTop - element.clientHeight < BOTTOM_THRESHOLD_PX;
	}

	function captureBeforeUpdate(nextConversationId) {
		const element = getScrollElement();
		const snapshot = element
			? { top: element.scrollTop, nearBottom: isNearBottom(element) }
			: null;

		if (nextConversationId !== selectedConversationId) {
			selectedConversationId = nextConversationId;
			pendingInitialConversationId = nextConversationId;
			followsLatest = true;
		}

		return snapshot;
	}

	function scrollToBottom() {
		const element = getScrollElement();
		if (element) {
			element.scrollTop = element.scrollHeight;
		}
	}

	function cancelScheduledScroll() {
		if (scrollFrame === null) {
			return;
		}

		window.cancelAnimationFrame(scrollFrame);
		scrollFrame = null;
	}

	function scheduleScrollToBottom() {
		if (scrollFrame !== null) {
			return;
		}

		scrollFrame = window.requestAnimationFrame(() => {
			scrollFrame = null;
			scrollToBottom();
		});
	}

	function shouldFollow() {
		return followsLatest;
	}

	function settleAfterUpdate(autoScroll, snapshot) {
		const element = getScrollElement();
		if (!element) {
			return;
		}

		if (selectedConversationId && pendingInitialConversationId === selectedConversationId) {
			pendingInitialConversationId = null;
			followsLatest = true;
			scrollToBottom();
			scheduleScrollToBottom();
			return;
		}

		if ((autoScroll && shouldFollow()) || !snapshot || snapshot.nearBottom) {
			scrollToBottom();
			scheduleScrollToBottom();
			return;
		}

		element.scrollTop = snapshot.top;
	}

	function resumeFollow() {
		followsLatest = true;
		scrollToBottom();
		scheduleScrollToBottom();
	}

	function onScroll(event) {
		const target = event.target instanceof HTMLElement ? event.target : null;
		if (!target) {
			return;
		}

		followsLatest = isNearBottom(target);
		if (!followsLatest) {
			cancelScheduledScroll();
		}
	}

	function onContentResize() {
		if (shouldFollow()) {
			scheduleScrollToBottom();
		}
	}

	onUnmounted(cancelScheduledScroll);

	return {
		captureBeforeUpdate,
		onContentResize,
		onScroll,
		resumeFollow,
		settleAfterUpdate,
	};
}
