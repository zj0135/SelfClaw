import { afterEach, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import TranscriptPanel from '../src/components/chat/TranscriptPanel.vue';
import TurnOutcome from '../src/components/chat/transcript/TurnOutcome.vue';
import { formatTurnOutcome } from '../src/renderers/turnOutcome.js';

let wrapper;
afterEach(() => wrapper?.unmount());

it('renders a no-output outcome without an assistant message, body, or preparing placeholder', () => {
    wrapper = mount(TranscriptPanel, { props: { collapse: {}, items: [{ id: 'turn-outcome:t1', kind: 'turn-outcome', role: 'system',
        status: 'failed', segments: [], turnOutcome: { turnId: 't1', status: 'failed', errorMessage: 'No response.' } }] } });
    expect(wrapper.find('.turn-outcome').text()).toContain('No response.');
    expect(wrapper.find('.message-row.assistant').exists()).toBe(false);
    expect(wrapper.find('.message-blocks').exists()).toBe(false);
    expect(wrapper.find('.preparing-indicator').exists()).toBe(false);
});

it('projects terminal status independently from sealed fragment status', () => {
    wrapper = mount(TranscriptPanel, { props: { collapse: {}, items: [{ id: 'a1', kind: 'message', role: 'assistant',
        status: 'sealed', timestamp: 'now', segments: [{ kind: 'content', markdown: 'Actual output.' }],
        turnOutcome: { turnId: 't1', status: 'cancelled', errorMessage: 'Stopped.' } }] } });
    expect(wrapper.find('.message-row').classes()).toContain('sealed');
    expect(wrapper.find('.turn-outcome').classes()).toContain('cancelled');
    expect(wrapper.text()).toContain('Actual output.');
    expect(wrapper.text()).toContain('Stopped.');
});

it('keeps unknown normalized totals unknown and renders explicit zero', () => {
    const outcome = { turnId: 't1', status: 'succeeded', durationMs: 2000, usage: { inputTokens: 3, outputTokens: 4, totalTokens: null } };
    expect(formatTurnOutcome(outcome).details).toBe('2s');
    expect(formatTurnOutcome({ ...outcome, usage: { totalTokens: 0 } }).details).toBe('2s · 0 tokens');
    wrapper = mount(TurnOutcome, { props: { outcome: { ...outcome, status: 'running' } } });
    expect(wrapper.find('footer').exists()).toBe(false);
});
