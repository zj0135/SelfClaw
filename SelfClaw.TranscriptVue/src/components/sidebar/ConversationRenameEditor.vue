<script setup>
import { nextTick, onMounted, ref } from 'vue';
import { Check, X } from 'lucide-vue-next';

const props = defineProps({
	initial: {
		type: String,
		default: '',
	},
});

const emit = defineEmits(['confirm', 'cancel']);

const draft = ref(props.initial);
const inputEl = ref(null);

onMounted(async () => {
	await nextTick();
	inputEl.value?.focus();
	inputEl.value?.select();
});

function confirm() {
	const title = draft.value.trim();
	if (!title) {
		return;
	}

	emit('confirm', title);
}

function onKeydown(event) {
	// 中文/日文输入法用 Enter 选定候选词，此时不能当作提交。
	if (event.isComposing) {
		return;
	}

	if (event.key === 'Enter') {
		event.preventDefault();
		confirm();
	} else if (event.key === 'Escape') {
		event.preventDefault();
		emit('cancel');
	}
}
</script>

<template>
	<div class="node kind-chat renaming" @click.stop @contextmenu.prevent>
		<input ref="inputEl" v-model="draft" class="rename-input" type="text" maxlength="120"
			autocomplete="off" spellcheck="false" aria-label="重命名会话" @keydown="onKeydown" />
		<span class="rename-actions">
			<button class="rename-action confirm" type="button" title="确认重命名" aria-label="确认重命名"
				:disabled="!draft.trim()" @click.stop="confirm">
				<Check :size="13" :stroke-width="2.4" aria-hidden="true" />
			</button>
			<button class="rename-action cancel" type="button" title="取消重命名" aria-label="取消重命名"
				@click.stop="emit('cancel')">
				<X :size="13" :stroke-width="2.4" aria-hidden="true" />
			</button>
		</span>
	</div>
</template>

<style scoped>
/* 根节点复用 AppSidebar 的 .node 盒模型（父作用域 id 会落到组件根元素上），
   这里只补编辑态外观与输入框、操作按钮。 */
.node.renaming {
	border-color: var(--sb-accent);
	background: var(--panel);
	cursor: text;
}

.rename-input {
	flex: 1;
	min-width: 0;
	height: 23px;
	padding: 0 6px;
	border: none;
	border-radius: 5px;
	background: var(--sb-hover);
	color: var(--sb-text);
	font: inherit;
	outline: none;
}

.rename-input::selection {
	background: color-mix(in srgb, var(--accent) 32%, transparent);
}

.rename-actions {
	display: flex;
	flex: none;
	align-items: center;
	gap: 1px;
}

.rename-action {
	display: grid;
	width: 21px;
	height: 21px;
	place-items: center;
	border: none;
	border-radius: 5px;
	background: transparent;
	color: var(--sb-mute);
	transition:
		background 0.13s,
		color 0.13s;
}

.rename-action:hover {
	background: var(--sb-hover);
}

.rename-action.confirm:hover {
	color: var(--sb-accent);
}

.rename-action.cancel:hover {
	color: var(--danger);
}

.rename-action:disabled {
	opacity: 0.4;
	cursor: default;
}
</style>
