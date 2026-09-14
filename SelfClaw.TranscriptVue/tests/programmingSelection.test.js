import { expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';

const { bridge } = vi.hoisted(() => ({ bridge: { request: vi.fn(), requestLatest: vi.fn(), on: vi.fn(() => () => {}), post: vi.fn(), hasHost: () => true } }));
vi.mock('../src/composables/hostBridge.js', () => ({ hostBridge: bridge, useHostBridge: () => bridge, isSuperseded: () => false }));
import ProgrammingAssistant from '../src/components/settings/ProgrammingAssistant.vue';
import ModelSelector from '../src/components/Chat/ModelSelector.vue';
import { createProgrammingAssistantSelection } from '../src/composables/useProgrammingAssistantSelection.js';

it('does not let an old read failure replace the result of a newer confirmed selection', async () => {
    let failRead;
    const transport = {
        on: () => () => {},
        request: (type) => type === 'get-programming-assistant-settings'
            ? new Promise((_, reject) => { failRead = reject; })
            : Promise.resolve({ revision: 5, selectedCliId: 'codex', tools: [{ id: 'codex', models: [] }] }),
    };
    const selection = createProgrammingAssistantSelection(transport);
    const loading = selection.load();
    await selection.selectCli('codex');
    failRead(new Error('stale read failure'));
    await loading;
    expect(selection.selectedCliId.value).toBe('codex');
    expect(selection.error.value).toBe('');
    selection.dispose();
});

it('restores and saves the same CLI choices in settings and composer, preserving confirmed values on failure', async () => {
    let settings = { revision: 1, selectedCliId: 'codex', selectedModel: 'model-a', selectedReasoningLevel: 'high',
        tools: [{ id: 'codex', name: 'Codex CLI', models: ['model-a', 'model-b'], reasoningLevels: ['Default (CLI config)', 'low', 'high'] }] };
    let fail = false;
    bridge.request.mockImplementation(async (type, payload) => {
        if (type === 'select-programming-model') {
            if (fail) throw new Error('Settings file is locked');
            settings = { ...settings, revision: settings.revision + 1, selectedModel: payload.model };
        }
        if (type === 'select-programming-reasoning') settings = { ...settings, revision: settings.revision + 1, selectedReasoningLevel: payload.reasoningLevel };
        return structuredClone(settings);
    });
    const page = mount(ProgrammingAssistant);
    const composer = mount(ModelSelector, { props: { executionMode: 'cli' } });
    await flushPromises();
    expect(page.get('select[aria-label="Codex CLI 模型选择"]').element.value).toBe('model-a');
    expect(page.get('select[aria-label="Codex CLI 推理等级选择"]').element.value).toBe('high');
    await page.get('select[aria-label="Codex CLI 模型选择"]').setValue('model-b');
    await flushPromises();
    expect(composer.get('.model-name').text()).toBe('model-b');
    await page.get('select[aria-label="Codex CLI 推理等级选择"]').setValue('low');
    await flushPromises();
    expect(settings.selectedReasoningLevel).toBe('low');
    fail = true;
    await page.get('select[aria-label="Codex CLI 模型选择"]').setValue('model-a');
    await flushPromises();
    expect(page.text()).toContain('Settings file is locked');
    expect(page.get('select[aria-label="Codex CLI 模型选择"]').element.value).toBe('model-b');
    expect(composer.get('.model-name').text()).toBe('model-b');
    page.unmount();
    const reopened = mount(ProgrammingAssistant);
    await flushPromises();
    expect(reopened.get('select[aria-label="Codex CLI 模型选择"]').element.value).toBe('model-b');
    expect(reopened.get('select[aria-label="Codex CLI 推理等级选择"]').element.value).toBe('low');
    reopened.unmount(); composer.unmount();
});
