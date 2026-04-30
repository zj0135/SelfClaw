<script setup>
import { ref } from 'vue';

defineProps({
	isChannelMode: {
		type: Boolean,
		default: false,
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

const emit = defineEmits(['new-conversation', 'open-settings', 'toggle-collapse']);

const conversationListEl = ref(null);

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
			<nav class="sidebar-nav" aria-label="SelfClaw navigation">
				<button
					class="sidebar-primary"
					type="button"
					:disabled="isChannelMode"
					:title="isChannelMode ? '频道会话由外部消息自动创建' : '新建会话'"
					@click="emit('new-conversation')"
				>
					<span class="sidebar-nav-icon" aria-hidden="true">
						<svg viewBox="0 0 20 20" fill="none">
							<path d="M5 15h10M5 15V5h10v5" />
							<path d="M8 8h4M8 11h2" />
						</svg>
					</span>
					<span>新建会话</span>
				</button>

				<div class="sidebar-nav-scroll">
					<div class="sidebar-nav-group">
						<button class="sidebar-nav-item active" type="button" aria-current="page">
							<span class="sidebar-nav-icon" aria-hidden="true">
								<svg viewBox="0 0 20 20" fill="none">
									<path d="M3.5 7.5h13l-1.2-3h-4.6l-.9 1.2H4.4l-.9 1.8Z" />
									<path d="M3.5 7.5v7h13v-7" />
								</svg>
							</span>
							<span>所有会话</span>
						</button>
						<div ref="conversationListEl" class="sidebar-nav-children sidebar-conversation-list-shell">
							<div id="conversation-list" class="conversation-list" v-html="conversationListHtml"></div>
						</div>
						<div class="sidebar-divider"></div>
						<div class="sidebar-nav-subitem standalone">
							<span class="sidebar-nav-icon" aria-hidden="true">
								<svg viewBox="0 0 20 20" fill="none">
									<path d="M4 8.5 9.5 14 16 7.5 11.5 3H5.5L4 4.5v4Z" />
									<path d="M7.25 6.25h.01" />
								</svg>
							</span>
							<span>标签</span>
						</div>

						<div class="sidebar-nav-subitem standalone">
							<span class="sidebar-nav-icon" aria-hidden="true">
								<svg viewBox="0 0 20 20" fill="none">
									<path d="M4 6.5h12M5.5 6.5v9h9v-9" />
									<path d="M7.5 4.5h5l.8 2h-6.6l.8-2Z" />
								</svg>
							</span>
							<span>已归档</span>
						</div>
					</div>
					<div class="sidebar-divider"></div>
					<div class="sidebar-nav-heading">
						<span class="sidebar-nav-icon" aria-hidden="true">
							<svg viewBox="0 0 20 20" fill="none">
								<path d="M7 13 13 7M6 9.5 4.5 11a2.8 2.8 0 0 0 4 4L10 13.5" />
								<path d="M10 6.5 11.5 5a2.8 2.8 0 0 1 4 4L14 10.5" />
							</svg>
						</span>
						<span>MCP</span>
					</div>
					<div class="sidebar-nav-subitem standalone">
						<span class="sidebar-nav-icon" aria-hidden="true">
							<svg viewBox="0 0 20 20" fill="none">
								<path d="M10 3.5 5.5 10H10l-1 6.5 5.5-8H10V3.5Z" />
							</svg>
						</span>
						<span>技能</span>
					</div>
					<div class="sidebar-nav-subitem standalone">
						<span class="sidebar-nav-icon" aria-hidden="true">
							<svg viewBox="0 0 20 20" fill="none">
								<path d="M5 8.5h10v6H5z" />
								<path d="M8 8.5V6a2 2 0 1 1 4 0v2.5" />
								<path d="M7.5 11.5h.01M12.5 11.5h.01" />
							</svg>
						</span>
						<span>智能体</span>
					</div>
					<div class="sidebar-nav-heading">
						<span class="sidebar-nav-icon" aria-hidden="true">
							<svg viewBox="0 0 20 20" fill="none">
								<path d="M4.5 5.5h2v2h-2zM4.5 12.5h2v2h-2zM9 6.5h6.5M9 13.5h6.5" />
							</svg>
						</span>
						<span>自动化</span>
					</div>
					<div class="sidebar-nav-children">
						<div class="sidebar-nav-subitem">
							<span class="sidebar-nav-icon" aria-hidden="true">
								<svg viewBox="0 0 20 20" fill="none">
									<circle cx="10" cy="10" r="6" />
									<path d="M10 6.8v3.5l2.4 1.6" />
								</svg>
							</span>
							<span>定时任务</span>
						</div>
						<div class="sidebar-nav-subitem">
							<span class="sidebar-nav-icon" aria-hidden="true">
								<svg viewBox="0 0 20 20" fill="none">
									<path d="M4 11.5c2.5-4 4.5-4 6 0s3.5 4 6 0" />
									<path d="M4 8.5c2.5 4 4.5 4 6 0s3.5-4 6 0" />
								</svg>
							</span>
							<span>事件触发</span>
						</div>
					</div>
				</div>

				<div class="sidebar-nav-footer">
					<button class="sidebar-nav-subitem standalone action" type="button" @click="emit('open-settings')">
						<span class="sidebar-nav-icon" aria-hidden="true">
							<svg viewBox="0 0 20 20" fill="none">
								<path d="M10 12.4a2.4 2.4 0 1 0 0-4.8 2.4 2.4 0 0 0 0 4.8Z" />
								<path
									d="M4.5 10c0-.4.1-.8.2-1.2L3.5 7.7 5 5.1l1.6.5c.6-.5 1.2-.8 2-1L9 3h3l.4 1.6c.7.2 1.4.6 2 1l1.6-.5 1.5 2.6-1.2 1.1c.1.4.2.8.2 1.2s-.1.8-.2 1.2l1.2 1.1-1.5 2.6-1.6-.5c-.6.5-1.2.8-2 1L12 17H9l-.4-1.6c-.7-.2-1.4-.6-2-1l-1.6.5-1.5-2.6 1.2-1.1c-.1-.4-.2-.8-.2-1.2Z"
								/>
							</svg>
						</span>
						<span>设置</span>
					</button>
				</div>
			</nav>
		</div>
	</aside>
</template>
