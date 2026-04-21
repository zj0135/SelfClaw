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
		<button
			class="pane-collapse-toggle pane-collapse-toggle-sidebar"
			type="button"
			:aria-label="collapsed ? '展开' : '折叠'"
			:title="collapsed ? '展开' : '折叠'"
			@click="emit('toggle-collapse')"
		>
			<svg
				class="pane-collapse-toggle-icon pane-collapse-toggle-icon-left"
				xmlns="http://www.w3.org/2000/svg"
				viewBox="0 0 1024 1024"
				aria-hidden="true"
			>
				<path
					fill="currentColor"
					d="M529.408 149.376a29.12 29.12 0 0 1 41.728 0 30.59 30.59 0 0 1 0 42.688L259.264 511.936l311.872 319.936a30.59 30.59 0 0 1-.512 43.264 29.12 29.12 0 0 1-41.216-.512L197.76 534.272a32 32 0 0 1 0-44.672zm256 0a29.12 29.12 0 0 1 41.728 0 30.59 30.59 0 0 1 0 42.688L515.264 511.936l311.872 319.936a30.59 30.59 0 0 1-.512 43.264 29.12 29.12 0 0 1-41.216-.512L453.76 534.272a32 32 0 0 1 0-44.672z"
				></path>
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
			<button
				class="sidebar-primary"
				type="button"
				:disabled="isChannelMode"
				:title="isChannelMode ? '频道会话由外部消息自动创建' : '新建对话'"
				@click="emit('new-conversation')"
			>
				+ 新建对话
			</button>
			<input id="conversation-search" class="search-box" type="text" :value="conversationSearch" placeholder="搜索会话..." @input="onSearchInput" />
			<div class="section-title">{{ conversationSectionTitle }}</div>
			<div id="conversation-list" ref="conversationListEl" class="conversation-list" v-html="conversationListHtml"></div>
			<button class="sidebar-footer" type="button" @click="emit('open-settings')">
				<div class="avatar">SC</div>
				<div class="sidebar-footer-copy">
					<div class="sidebar-footer-title">系统设置</div>
					<div class="sidebar-footer-subtitle">模型、工作区、我的频道、主题</div>
				</div>
				<div>&rsaquo;</div>
			</button>
		</div>
	</aside>
</template>
