import { computed, onMounted, reactive, ref } from 'vue';
import { useProgrammingAssistantSelection } from './useProgrammingAssistantSelection.js';
import { useHostBridge } from './hostBridge.js';

export function useProgrammingAssistantPage() {
    const selection = useProgrammingAssistantSelection();
    const bridge = useHostBridge();
    const expanded = ref(null);
    const tests = reactive({});
    const cliTools = computed(() => selection.tools.value.map((tool, index) => ({ ...tool,
        isOpen: expanded.value === null ? tool.id === (selection.selectedCliId.value || selection.tools.value[0]?.id) : expanded.value === tool.id,
        ...(tests[tool.id] || {}),
    })));
    const isLoading = computed(() => !selection.loaded.value);
    const scanStatusText = computed(() => selection.error.value ||
        (!isLoading.value && !selection.scanning.value && !cliTools.value.length ? '还没有检测到本地 CLI。' : ''));

    async function testCli(cli) {
        if (tests[cli.id]?.testing) return;
        tests[cli.id] = { testing: true, showToast: false };
        try {
            const result = await bridge.request('test-programming-cli', { cliId: cli.id });
            tests[cli.id] = { testing: false, showToast: true, testError: result.success ? '' : result.error,
                testMessage: result.success ? `连接正常 · ${result.version || '已检测到 CLI'}` : `连接失败 · ${result.error || 'CLI 不可用'}` };
        } catch (failure) {
            tests[cli.id] = { testing: false, showToast: true, testError: failure.message, testMessage: failure.message };
        }
    }

    onMounted(selection.load);
    return { cliTools, isLoading, isRescanning: selection.scanning, scanError: selection.error,
        selectedCliId: selection.selectedCliId, pending: selection.pending, scanStatusText, testCli,
        toggleOpen(cli) { expanded.value = cli.isOpen ? '' : cli.id; },
        selectCli: (cli) => selection.selectCli(cli.id), rescanCliTools: selection.rescan,
        selectModel: selection.selectModel, selectReasoning: selection.selectReasoning };
}
