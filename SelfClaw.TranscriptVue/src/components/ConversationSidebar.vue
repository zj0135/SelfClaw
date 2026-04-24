<script setup>
import { ref } from 'vue';

defineProps({
	fallbackStatusText: {
		type: String,
		default: '',
	},
	isChannelMode: {
		type: Boolean,
		default: false,
	},
	conversationSearch: {
		type: String,
		default: '',
	},
	conversationSectionTitle: {
		type: String,
		default: '',
	},
	conversationListHtml: {
		type: String,
		default: '',
	},
	collapsed: {
		type: Boolean,
		default: false,
	},
});

const emit = defineEmits(['new-conversation', 'open-settings', 'search-change', 'search-input', 'toggle-collapse']);

const conversationListEl = ref(null);

function onSearchInput(event) {
	emit('search-change', event.target.value);
	emit('search-input');
}

defineExpose({
	getConversationListEl: () => conversationListEl.value,
});
</script>

<template>
	<aside class="panel sidebar" :class="{ collapsed }">
		<button class="pane-collapse-toggle pane-collapse-toggle-sidebar" type="button"
			:aria-label="collapsed ? '展开' : '折叠'" :title="collapsed ? '展开' : '折叠'" @click="emit('toggle-collapse')">
			<svg class="pane-collapse-toggle-icon pane-collapse-toggle-icon-left" xmlns="http://www.w3.org/2000/svg"
				viewBox="0 0 1024 1024" aria-hidden="true">
				<path fill="currentColor"
					d="M529.408 149.376a29.12 29.12 0 0 1 41.728 0 30.59 30.59 0 0 1 0 42.688L259.264 511.936l311.872 319.936a30.59 30.59 0 0 1-.512 43.264 29.12 29.12 0 0 1-41.216-.512L197.76 534.272a32 32 0 0 1 0-44.672zm256 0a29.12 29.12 0 0 1 41.728 0 30.59 30.59 0 0 1 0 42.688L515.264 511.936l311.872 319.936a30.59 30.59 0 0 1-.512 43.264 29.12 29.12 0 0 1-41.216-.512L453.76 534.272a32 32 0 0 1 0-44.672z">
				</path>
			</svg>
		</button>
		<div class="sidebar-body">
			<div class="brand">
				<div class="brand-badge">SC</div>
				<div>
					<div class="brand-name">SelfClaw</div>
					<div class="status-row">
						<span class="status-dot"></span>
						<span id="sidebar-status-text">{{ fallbackStatusText }}</span>
					</div>
				</div>
			</div>
			<button class="sidebar-primary" type="button" :disabled="isChannelMode"
				:title="isChannelMode ? '频道会话由外部消息自动创建' : '新建对话'" @click="emit('new-conversation')">
				+ 新建对话
			</button>
			<input id="conversation-search" class="search-box" type="text" :value="conversationSearch"
				placeholder="搜索会话..." @input="onSearchInput" />
			<div class="section-title">{{ conversationSectionTitle }}</div>
			<div id="conversation-list" ref="conversationListEl" class="conversation-list"
				v-html="conversationListHtml"></div>
			<button class="sidebar-footer" type="button" @click="emit('open-settings')">
				<div class="sidebar-footer-icon" aria-hidden="true">
					<svg viewBox="0 0 16 16">
						<path
							d="M8 0a8.2 8.2 0 0 1 .701.031C9.444.095 9.99.645 10.16 1.29l.288 1.107c.018.066.079.158.212.224.231.114.454.243.668.386.123.082.233.09.299.071l1.103-.303c.644-.176 1.392.021 1.82.63.27.385.506.792.704 1.218.315.675.111 1.422-.364 1.891l-.814.806c-.049.048-.098.147-.088.294.016.257.016.515 0 .772-.01.147.038.246.088.294l.814.806c.475.469.679 1.216.364 1.891a7.977 7.977 0 0 1-.704 1.217c-.428.61-1.176.807-1.82.63l-1.102-.302c-.067-.019-.177-.011-.3.071a5.909 5.909 0 0 1-.668.386c-.133.066-.194.158-.211.224l-.29 1.106c-.168.646-.715 1.196-1.458 1.26a8.006 8.006 0 0 1-1.402 0c-.743-.064-1.289-.614-1.458-1.26l-.289-1.106c-.018-.066-.079-.158-.212-.224a5.738 5.738 0 0 1-.668-.386c-.123-.082-.233-.09-.299-.071l-1.103.303c-.644.176-1.392-.021-1.82-.63a8.12 8.12 0 0 1-.704-1.218c-.315-.675-.111-1.422.363-1.891l.815-.806c.05-.048.098-.147.088-.294a6.214 6.214 0 0 1 0-.772c.01-.147-.038-.246-.088-.294l-.815-.806C.635 6.045.431 5.298.746 4.623a7.92 7.92 0 0 1 .704-1.217c.428-.61 1.176-.807 1.82-.63l1.102.302c.067.019.177.011.3-.071.214-.143.437-.272.668-.386.133-.066.194-.158.211-.224l.29-1.106C6.009.645 6.556.095 7.299.03 7.53.01 7.764 0 8 0Zm-.571 1.525c-.036.003-.108.036-.137.146l-.289 1.105c-.147.561-.549.967-.998 1.189-.173.086-.34.183-.5.29-.417.278-.97.423-1.529.27l-1.103-.303c-.109-.03-.175.016-.195.045-.22.312-.412.644-.573.99-.014.031-.021.11.059.19l.815.806c.411.406.562.957.53 1.456a4.709 4.709 0 0 0 0 .582c.032.499-.119 1.05-.53 1.456l-.815.806c-.081.08-.073.159-.059.19.162.346.353.677.573.989.02.03.085.076.195.046l1.102-.303c.56-.153 1.113-.008 1.53.27.161.107.328.204.501.29.447.222.85.629.997 1.189l.289 1.105c.029.109.101.143.137.146a6.6 6.6 0 0 0 1.142 0c.036-.003.108-.036.137-.146l.289-1.105c.147-.561.549-.967.998-1.189.173-.086.34-.183.5-.29.417-.278.97-.423 1.529-.27l1.103.303c.109.029.175-.016.195-.045.22-.313.411-.644.573-.99.014-.031.021-.11-.059-.19l-.815-.806c-.411-.406-.562-.957-.53-1.456a4.709 4.709 0 0 0 0-.582c-.032-.499.119-1.05.53-1.456l.815-.806c.081-.08.073-.159.059-.19a6.464 6.464 0 0 0-.573-.989c-.02-.03-.085-.076-.195-.046l-1.102.303c-.56.153-1.113.008-1.53-.27a4.44 4.44 0 0 0-.501-.29c-.447-.222-.85-.629-.997-1.189l-.289-1.105c-.029-.11-.101-.143-.137-.146a6.6 6.6 0 0 0-1.142 0ZM11 8a3 3 0 1 1-6 0 3 3 0 0 1 6 0ZM9.5 8a1.5 1.5 0 1 0-3.001.001A1.5 1.5 0 0 0 9.5 8Z"
						></path>
					</svg>
				</div>
				<div class="sidebar-footer-copy">
					<div class="sidebar-footer-title">系统设置</div>
					<div class="sidebar-footer-subtitle">模型、工作区、我的频道、主题</div>
				</div>
				<div class="sidebar-footer-chevron" aria-hidden="true">
					<svg viewBox="0 0 16 16" fill="none">
						<path d="M6 3.5 10.5 8 6 12.5"></path>
					</svg>
				</div>
			</button>
		</div>
	</aside>
</template>
