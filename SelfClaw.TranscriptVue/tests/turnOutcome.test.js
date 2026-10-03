import { afterEach, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import TranscriptPanel from '../src/components/Chat/TranscriptPanel.vue';
import TurnOutcome from '../src/components/Chat/transcript/TurnOutcome.vue';
import { formatTurnOutcome } from '../src/renderers/turnOutcome.js';
import { createPluginTranscriptProjector, maximumPluginTranscriptBytes } from '../src/renderers/pluginTranscript.js';

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

it('preserves a bounded explicit outcome when plugin transcript content is oversized', () => {
    const item = { id: 'turn-outcome:t1', kind: 'turn-outcome', role: 'system', status: 'failed', segments: [],
        turnOutcome: { turnId: 't1', status: 'failed', errorMessage: 'failure'.repeat(100000), durationMs: 100,
            usage: { inputTokens: 3, outputTokens: 4, totalTokens: null } } };
    const snapshot = createPluginTranscriptProjector()({ revision: 1, items: [item] });
    expect(snapshot.truncated).toBe(true);
    expect(snapshot.items[0].kind).toBe('turn-outcome');
    expect(snapshot.items[0].segments).toEqual([]);
    expect(snapshot.items[0].turnOutcome.status).toBe('failed');
    expect(snapshot.items[0].turnOutcome.usage.totalTokens).toBeNull();
    expect(new TextEncoder().encode(JSON.stringify(snapshot)).length).toBeLessThan(maximumPluginTranscriptBytes);
});
