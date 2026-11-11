import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import TranscriptSkeleton from '../src/components/chat/transcript/TranscriptSkeleton.vue';
import { setPendingConversation, useChatTranscript } from '../src/composables/useChatTranscript.js';

describe('TranscriptSkeleton', () => {
	it('renders loading status and skeleton message structure', () => {
		const wrapper = mount(TranscriptSkeleton);

		expect(wrapper.find('.skeleton-status-pill').text()).toContain('正在载入历史对话');
		expect(wrapper.findAll('.skeleton-row.assistant').length).toBeGreaterThanOrEqual(1);
		expect(wrapper.findAll('.skeleton-row.user').length).toBeGreaterThanOrEqual(1);
	});
});

describe('useChatTranscript history switching transition', () => {
	it('distinguishes between new chat empty state and history loading state without layout shift', () => {
		const dummyScroll = {
			captureBeforeUpdate: () => null,
			settleAfterUpdate: () => {},
		};

		const { isEmptyConversation, isLoadingHistory } = useChatTranscript(dummyScroll);

		// 默认初始状态为新对话（selectedConversationId === null, items: []）
		expect(isEmptyConversation.value).toBe(true);
		expect(isLoadingHistory.value).toBe(false);

		// 用户在侧栏点击历史会话：立即进入 loadingHistory，并且退出 isEmptyConversation（输入框直接到底部）
		setPendingConversation('hist-conv-001');
		expect(isLoadingHistory.value).toBe(true);
		expect(isEmptyConversation.value).toBe(false);

		// 点击「新建对话」：立即回到新对话空态
		setPendingConversation(null);
		expect(isLoadingHistory.value).toBe(false);
		expect(isEmptyConversation.value).toBe(true);
	});
});
