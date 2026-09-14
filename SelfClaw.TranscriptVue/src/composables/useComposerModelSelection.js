import { computed, ref, watch } from 'vue';
import { useHostBridge, isSuperseded } from './hostBridge.js';
import { defaultCliModel, useProgrammingAssistantSelection } from './useProgrammingAssistantSelection.js';

const directModels = ref([]);
const activeDirectModelProfileId = ref('');
export function getSelectedModelProfileId() { return activeDirectModelProfileId.value || null; }

export function useComposerModelSelection(executionMode, emit) {
    const bridge = useHostBridge();
    const cli = useProgrammingAssistantSelection();
    const selectedMode = ref(executionMode() === 'direct' ? 'direct' : 'cli');
    const isDirect = computed(() => selectedMode.value === 'direct');
    const directLoaded = ref(false);
    const directError = ref('');
    const saving = ref(false);
    let directGeneration = 0;
    const selectedDirectModel = computed(() => directModels.value.find((model) => model.modelProfileId === activeDirectModelProfileId.value));
    const loaded = computed(() => isDirect.value ? directLoaded.value : cli.loaded.value);
    const loadError = computed(() => directError.value || (!isDirect.value ? cli.error.value : ''));
    const models = computed(() => cli.selectedTool.value?.models || [defaultCliModel]);
    const reasoningLevels = computed(() => cli.selectedTool.value?.reasoningLevels || []);

    async function requestDirectModels() {
        const generation = ++directGeneration;
        try {
            const payload = await bridge.requestLatest('composer-direct-models', 'ai-providers/list-enabled-models');
            if (generation !== directGeneration) return;
            directModels.value = (payload.models || []).filter((model) => model.modelProfileId);
            activeDirectModelProfileId.value = directModels.value.some((model) => model.modelProfileId === payload.defaultModelProfileId)
                ? payload.defaultModelProfileId : '';
            directError.value = '';
            directLoaded.value = true;
        } catch (failure) {
            if (generation === directGeneration && !isSuperseded(failure)) directError.value = failure.message;
        }
    }

    function requestActiveSource() { return isDirect.value ? requestDirectModels() : cli.load(); }

    async function pickMode(mode) {
        if (saving.value || mode === selectedMode.value) return;
        saving.value = true;
        directError.value = '';
        try {
            await bridge.request('select-composer-mode', { mode });
            selectedMode.value = mode;
            await requestActiveSource();
        } catch (failure) { directError.value = failure.message; }
        finally { saving.value = false; }
    }

    async function pickDirectModel(model) {
        if (saving.value || !model?.modelProfileId) return;
        saving.value = true;
        directGeneration++;
        directError.value = '';
        try {
            await bridge.request('ai-providers/set-default-model', { scope: 'desktop-default', modelProfileId: model.modelProfileId });
            activeDirectModelProfileId.value = model.modelProfileId;
            emit('update:model', model.modelProfileId);
        } catch (failure) { directError.value = failure.message; }
        finally { saving.value = false; }
    }

    watch(executionMode, (mode) => { selectedMode.value = mode === 'direct' ? 'direct' : 'cli'; });
    return { selectedMode, isDirect, loaded, loadError, models, reasoningLevels,
        hasReasoning: computed(() => reasoningLevels.value.length > 1), pending: computed(() => saving.value || cli.pending.value),
        detectedAgents: cli.tools, selectedCliId: cli.selectedCliId, selectedAgent: cli.selectedTool,
        activeModel: cli.selectedModel, activeReasoning: cli.selectedReasoningLevel,
        directModels, activeDirectModelProfileId, selectedDirectModel, requestActiveSource, pickMode, pickDirectModel,
        async pickAgent(agent) { if (await cli.selectCli(agent.id)) emit('update:agent', agent.id); },
        async pickModel(model) { if (await cli.selectModel(model)) emit('update:model', model); },
        async pickReasoning(level) { if (await cli.selectReasoning(level)) emit('update:reasoning', level); },
    };
}
