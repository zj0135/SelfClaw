<script setup>
import { computed, onMounted, onUnmounted, useId } from 'vue';
import { Check, LoaderCircle, MessageCircleQuestion, X } from 'lucide-vue-next';
import { useAskUserAnswers } from '../../../composables/useAskUserAnswers.js';

const props = defineProps({
	// user-question/state 里的 question：{ requestId, prompts: [...] }；null 表示没有待答批次。
	question: { type: Object, default: null },
	// 提交中：全部控件禁用。宿主会拒绝二次作答，但界面不该给出这个机会。
	resolving: { type: Boolean, default: false },
});

const emit = defineEmits(['answer', 'skip']);

const questionRef = computed(() => props.question);
const {
	prompts, multiple, activeIndex, activePrompt, activePromptId, answeredCount, canSubmit,
	isAnswered, isSelected, isOtherActive, otherText, setOtherText,
	selectOption, chooseOther, setActive, moveTab, selectByIndex, buildAnswers,
} = useAskUserAnswers(questionRef);

const dockId = useId();
const panelId = `${dockId}-panel`;

// 会话切换时 dock 常驻（问题把那一回合彻底卡住），所以必须自报来源。
const sourceLabel = computed(() => (props.question?.conversationTitle
	? `来自「${props.question.conversationTitle}」`
	: ''));

function tabLabel(prompt, index) {
	return prompt.header || `问题 ${index + 1}/${prompts.value.length}`;
}

function submit() {
	const answers = buildAnswers();
	if (!answers.length || props.resolving || !props.question) {
		return;
	}

	emit('answer', { requestId: props.question.requestId, answers });
}

function skip() {
	if (!props.question || props.resolving) {
		return;
	}

	emit('skip', props.question.requestId);
}

// 快捷键挂在 window 上：dock 出现时焦点通常还在输入框里，绑在容器上就得先点一下。
function onKeydown(event) {
	if (!props.question || event.defaultPrevented || event.isComposing) {
		return;
	}

	if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
		event.preventDefault();
		submit();
		return;
	}

	// 在输入框里打字时不抢数字与方向键（"其它"的 textarea 也在其中）。
	if (isTextEntry(event.target) || event.ctrlKey || event.metaKey || event.altKey) {
		return;
	}

	const digit = Number(event.key);
	if (Number.isInteger(digit) && digit >= 1 && digit <= 9) {
		event.preventDefault();
		selectByIndex(digit - 1);
		return;
	}

	if (event.key === 'ArrowRight' || event.key === 'ArrowLeft') {
		event.preventDefault();
		moveTab(event.key === 'ArrowRight' ? 1 : -1);
	}
}

function isTextEntry(target) {
	if (!(target instanceof HTMLElement)) {
		return false;
	}

	return target.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName);
}

onMounted(() => window.addEventListener('keydown', onKeydown));
onUnmounted(() => window.removeEventListener('keydown', onKeydown));
</script>

<template>
	<transition name="ask-user-dock">
		<section v-if="props.question" class="ask-user-dock" role="alertdialog" aria-label="需要你的回答">
			<header class="ask-user-head">
				<span class="ask-user-mark" aria-hidden="true">
					<MessageCircleQuestion :size="15" :stroke-width="1.9" />
				</span>
				<span class="ask-user-heading">
					<span class="ask-user-title">需要你的回答</span>
					<span v-if="sourceLabel" class="ask-user-source">{{ sourceLabel }}</span>
				</span>
				<span class="ask-user-count" role="status" aria-live="polite">{{ answeredCount }}／{{ prompts.length }}</span>
			</header>

			<div v-if="multiple" class="ask-user-tabs" role="tablist" aria-label="问题列表">
				<button v-for="(prompt, index) in prompts" :id="`${dockId}-tab-${index}`" :key="prompt.promptId"
					type="button" role="tab" class="ask-user-tab"
					:class="{ active: index === activeIndex, answered: isAnswered(prompt.promptId) }"
					:aria-selected="index === activeIndex" :aria-controls="panelId"
					:tabindex="index === activeIndex ? 0 : -1" @click="setActive(index)">
					<Check v-if="isAnswered(prompt.promptId)" class="ask-user-tab-check" :size="12" :stroke-width="2.6"
						aria-hidden="true" />
					<span v-else class="ask-user-tab-dot" aria-hidden="true"></span>
					<span class="ask-user-tab-label">{{ tabLabel(prompt, index) }}</span>
				</button>
			</div>

			<div :id="panelId" class="ask-user-panel" :role="multiple ? 'tabpanel' : undefined">
				<template v-if="activePrompt">
					<h3 class="ask-user-question">{{ activePrompt.question }}</h3>
					<ul class="ask-user-options" :class="{ multi: activePrompt.multiSelect }">
						<li v-for="(option, index) in activePrompt.options" :key="option.label" class="ask-user-row">
							<button type="button" class="ask-user-option" :data-label="option.label"
								:class="{ selected: isSelected(activePromptId, option.label) }"
								:aria-pressed="isSelected(activePromptId, option.label)" :disabled="props.resolving"
								@click="selectOption(activePromptId, option.label, activePrompt.multiSelect)">
								<span class="ask-user-key" aria-hidden="true">{{ index + 1 }}</span>
								<span class="ask-user-option-copy">
									<span class="ask-user-option-label">{{ option.label }}</span>
									<span v-if="option.description" class="ask-user-option-desc">{{ option.description }}</span>
								</span>
								<Check class="ask-user-option-check" :size="14" :stroke-width="2.4" aria-hidden="true" />
							</button>
						</li>
						<li class="ask-user-row other">
							<button type="button" class="ask-user-option" data-label="其它"
								:class="{ selected: isOtherActive(activePromptId) }"
								:aria-pressed="isOtherActive(activePromptId)" :disabled="props.resolving"
								@click="chooseOther(activePromptId, activePrompt.multiSelect)">
								<span class="ask-user-key" aria-hidden="true">＋</span>
								<span class="ask-user-option-copy">
									<span class="ask-user-option-label">其它</span>
									<span class="ask-user-option-desc">用自己的话描述</span>
								</span>
								<Check class="ask-user-option-check" :size="14" :stroke-width="2.4" aria-hidden="true" />
							</button>
							<transition name="ask-user-other">
								<textarea v-if="isOtherActive(activePromptId)" class="ask-user-other" rows="2"
									:value="otherText(activePromptId)" placeholder="输入你的回答…" aria-label="其它回答"
									:disabled="props.resolving"
									@input="setOtherText(activePromptId, $event.target.value)"></textarea>
							</transition>
						</li>
					</ul>
				</template>
			</div>

			<footer class="ask-user-actions">
				<span class="ask-user-hint" aria-hidden="true">1–4 选择 · ←→ 切题 · Ctrl+Enter 提交</span>
				<button class="ask-user-btn skip" type="button" :disabled="props.resolving" @click="skip">
					<X :size="13" :stroke-width="2.4" aria-hidden="true" />
					跳过
				</button>
				<button class="ask-user-btn submit" type="button" :disabled="!canSubmit || props.resolving"
					@click="submit">
					<LoaderCircle v-if="props.resolving" class="ask-user-spin" :size="13" :stroke-width="2.2"
						aria-hidden="true" />
					<Check v-else :size="13" :stroke-width="2.6" aria-hidden="true" />
					提交
				</button>
			</footer>
		</section>
	</transition>
</template>

<style scoped>
@import '../../../styles/ask-user-dock.css';
</style>
