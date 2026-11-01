<script setup>
import { ref } from 'vue';
import ComposerEditor from './ComposerEditor.vue';
import MentionPopover from './mention/MentionPopover.vue';
import { useComposerMention } from '../../../composables/useComposerMention.js';

// 输入区编排：可编辑区 + 快捷弹层 + 触发状态机。对 ComposerPanel 只暴露 v-model、submit、paste 与 insertChip/clear/focus。
const props = defineProps({
	modelValue: { type: String, default: '' },
	placeholder: { type: String, default: '' },
	agentMode: { type: String, default: 'cli' },
	workspaceSelection: { type: Object, default: () => ({}) },
	selectedAgentId: { type: String, default: '' },
	selectedAgentName: { type: String, default: '' },
	capabilityRevision: { type: Number, default: 0 },
	commands: { type: Array, default: () => [] },
});
const emit = defineEmits(['update:modelValue', 'submit', 'paste']);

const editorRef = ref(null);
const popoverRef = ref(null);

// `$` 技能只在 Direct 下有意义（CLI 自管技能，宿主不注入）；`@` 没有工作目录时仍打开，由弹层给出提示。
const { open, kind, query, notify, close, dismiss, trigger } = useComposerMention({
	isKindEnabled: (candidate) => candidate !== 'skill' || props.agentMode === 'direct',
});

function onNavigate(event) {
	popoverRef.value?.handleNavigate(event);
}

function onSelect(chip) {
	const range = trigger();
	close();
	editorRef.value?.replaceTriggerWithChip(chip, range);
}

defineExpose({
	insertChip: (chip) => editorRef.value?.insertChip(chip),
	clear: () => editorRef.value?.clear(),
	focus: () => editorRef.value?.focus(),
});
</script>

<template>
	<ComposerEditor ref="editorRef" :model-value="props.modelValue" :placeholder="props.placeholder" :mention-open="open"
		@update:model-value="emit('update:modelValue', $event)" @submit="emit('submit')" @trigger="notify"
		@navigate="onNavigate" @paste="emit('paste', $event)" />
	<MentionPopover ref="popoverRef" :open="open" :kind="kind" :query="query"
		:workspace-selection="props.workspaceSelection" :selected-agent-id="props.selectedAgentId"
		:selected-agent-name="props.selectedAgentName" :capability-revision="props.capabilityRevision"
		:commands="props.commands" @select="onSelect" @dismiss="dismiss" />
</template>
