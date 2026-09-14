import { computed, readonly, ref } from 'vue';
import claudeIcon from '@lobehub/icons-static-png/light/claude-color.png';
import codexIcon from '@lobehub/icons-static-png/light/openai.png';
import opencodeIcon from '@lobehub/icons-static-png/light/opencode.png';
import { hostBridge } from './hostBridge.js';

export const defaultCliModel = 'Default (CLI config)';
const icons = { claude: claudeIcon, codex: codexIcon, opencode: opencodeIcon };
const list = (values) => [...new Set((Array.isArray(values) ? values : []).filter((value) => typeof value === 'string' && value.trim()))];

export function createProgrammingAssistantSelection(bridge) {
    const confirmed = ref({ tools: [], selectedCliId: '', revision: -1 });
    const loaded = ref(false);
    const pending = ref(0);
    const scanning = ref(false);
    const error = ref('');
    let generation = 0;
    let saveQueue = Promise.resolve();
    let loading = null;
    const selectedCliId = computed(() => confirmed.value.selectedCliId || '');
    const selectedModel = computed(() => confirmed.value.selectedModel || defaultCliModel);
    const selectedReasoningLevel = computed(() => confirmed.value.selectedReasoningLevel || defaultCliModel);
    const tools = computed(() => (confirmed.value.tools || []).filter((tool) => tool.id).map((tool) => ({
        ...tool, name: tool.name || tool.id, iconSrc: icons[tool.id], iconBackground: '#ffffff',
        iconFallback: (tool.name || tool.id).slice(0, 2).toUpperCase(),
        models: list([defaultCliModel, ...(tool.models || [])]), reasoningLevels: list(tool.reasoningLevels),
        selectedModel: tool.id === selectedCliId.value ? selectedModel.value : defaultCliModel,
        selectedReasoningLevel: tool.id === selectedCliId.value ? selectedReasoningLevel.value : defaultCliModel,
    })));
    const selectedTool = computed(() => tools.value.find((tool) => tool.id === selectedCliId.value) || null);

    function apply(payload) {
        if (payload.error) throw new Error(payload.error);
        if ((payload.revision ?? 0) < (confirmed.value.revision ?? 0)) return;
        confirmed.value = { ...payload, revision: payload.revision ?? 0 };
        scanning.value = Boolean(payload.isScanning);
        error.value = payload.scanError || '';
        loaded.value = true;
    }

    function load() {
        if (loading) return loading;
        const version = generation;
        loading = bridge.request('get-programming-assistant-settings').then((payload) => {
            if (version === generation) apply(payload);
        }).catch((failure) => { if (version === generation) error.value = failure.message; }).finally(() => { loading = null; });
        return loading;
    }

    function save(type, payload = {}) {
        generation++;
        pending.value++;
        const operation = saveQueue.then(async () => {
            error.value = '';
            try {
                const result = await bridge.request(type, payload);
                apply(result);
                return true;
            } catch (failure) {
                error.value = failure?.message || String(failure);
                return false;
            } finally { pending.value--; }
        });
        saveQueue = operation;
        return operation;
    }

    const unsubscribe = bridge.on('programming-assistant-settings-changed', apply);
    return { tools, selectedTool, selectedCliId, selectedModel, selectedReasoningLevel, loaded: readonly(loaded),
        pending: computed(() => pending.value > 0), scanning: readonly(scanning), error: readonly(error), load,
        selectCli: (cliId) => save('select-programming-cli', { cliId }),
        selectModel: (model) => save('select-programming-model', { model }),
        selectReasoning: (reasoningLevel) => save('select-programming-reasoning', { reasoningLevel }),
        async rescan() {
            scanning.value = true;
            try { return await save('scan-programming-clis'); } finally { scanning.value = false; }
        },
        dispose: unsubscribe,
    };
}

const selection = createProgrammingAssistantSelection(hostBridge);
export function useProgrammingAssistantSelection() { return selection; }
