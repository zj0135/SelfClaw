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
});

const emit = defineEmits(['new-conversation', 'open-settings', 'search-change', 'search-input']);

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
	<aside class="panel sidebar">
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
		<input
			id="conversation-search"
			class="search-box"
			type="text"
			:value="conversationSearch"
			placeholder="搜索会话..."
			@input="onSearchInput"
		/>
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
	</aside>
</template>
