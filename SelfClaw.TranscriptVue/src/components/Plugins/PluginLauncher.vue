<script setup>
import { computed } from 'vue';
import { AlertCircle, Layers, Settings2, X } from 'lucide-vue-next';
import { resolvePluginIcon } from '../../renderers/pluginIcons.js';

// 打开视图只有一个入口：左侧导航的「插件」。列表按 slot 分两段——右侧面板与悬浮视图是两种
// 摆放方式，用户在打开之前就该知道它会出现在哪里。
//
// 已打开的条目照样可点：面板被隐藏时，重新选它就是把右栏叫回来的那条路。若在这里 disabled，
// 面板全部打开又全部隐藏时右栏就再也回不来了。悬浮视图没有标签栏，所以这里的关闭按钮是宿主侧
// 保证的关闭入口（插件自己也可以调 selfclaw.close()）。
const props = defineProps({
	open: { type: Boolean, default: false },
	views: { type: Array, default: () => [] },
	openKeys: { type: Array, default: () => [] },
	error: { type: String, default: '' },
});

defineEmits(['close', 'select', 'close-view', 'manage']);

const dockedViews = computed(() => props.views.filter((view) => view.slot === 'right'));
const floatingViews = computed(() => props.views.filter((view) => view.slot === 'floating'));
</script>

<template>
	<div v-if="open" class="launcher-backdrop" @click.self="$emit('close')">
		<div class="launcher" role="dialog" aria-label="打开插件视图">
			<header>
				<Layers :size="14" :stroke-width="1.8" />
				<span>插件视图</span>
			</header>

			<p v-if="error" class="launcher-error">
				<AlertCircle :size="13" />{{ error }}
			</p>

			<div v-if="views.length" class="view-scroll">
				<section v-if="dockedViews.length">
					<h2>右侧面板</h2>
					<ul class="view-list">
						<li v-for="view in dockedViews" :key="view.key">
							<button type="button" class="view-main" @click="$emit('select', view.key)">
								<component :is="resolvePluginIcon(view.icon)" :size="14" :stroke-width="1.8" />
								<span class="view-title">{{ view.title }}</span>
								<code>{{ view.pluginId }}</code>
								<span v-if="openKeys.includes(view.key)" class="view-state">已打开</span>
							</button>
							<button v-if="openKeys.includes(view.key)" type="button" class="view-close"
								:aria-label="`关闭 ${view.title}`" :title="`关闭 ${view.title}`"
								@click="$emit('close-view', view.key)">
								<X :size="13" :stroke-width="2" />
							</button>
						</li>
					</ul>
				</section>

				<section v-if="floatingViews.length">
					<h2>悬浮视图</h2>
					<ul class="view-list">
						<li v-for="view in floatingViews" :key="view.key">
							<button type="button" class="view-main" @click="$emit('select', view.key)">
								<component :is="resolvePluginIcon(view.icon)" :size="14" :stroke-width="1.8" />
								<span class="view-title">{{ view.title }}</span>
								<code>{{ view.pluginId }}</code>
								<span v-if="openKeys.includes(view.key)" class="view-state">已打开</span>
							</button>
							<button v-if="openKeys.includes(view.key)" type="button" class="view-close"
								:aria-label="`关闭 ${view.title}`" :title="`关闭 ${view.title}`"
								@click="$emit('close-view', view.key)">
								<X :size="13" :stroke-width="2" />
							</button>
						</li>
					</ul>
				</section>
			</div>
			<p v-else class="empty">还没有启用任何提供视图的插件。</p>

			<footer>
				<button type="button" @click="$emit('manage')">
					<Settings2 :size="13" :stroke-width="1.8" />管理插件
				</button>
			</footer>
		</div>
	</div>
</template>

<style scoped>
.launcher-backdrop {
	position: fixed;
	inset: 0;
	z-index: 500;
	display: flex;
	align-items: center;
	justify-content: center;
	padding: 24px;
	background: var(--overlay);
	backdrop-filter: blur(3px);
}

.launcher {
	width: min(440px, 100%);
	max-height: min(64vh, 560px);
	display: flex;
	flex-direction: column;
	overflow: hidden;
	border: 1px solid var(--border-strong);
	border-radius: 14px;
	background: var(--panel);
	box-shadow: 0 24px 70px rgba(var(--shadow-ink), 0.22);
	animation: launcher-pop 180ms cubic-bezier(0.22, 1, 0.36, 1);
}

@keyframes launcher-pop {
	from {
		opacity: 0;
		transform: translateY(8px) scale(0.98);
	}
}

header {
	display: flex;
	align-items: center;
	gap: 8px;
	flex: none;
	padding: 14px 16px 12px;
	border-bottom: 1px solid var(--border);
	color: var(--muted);
	font-size: var(--fs-11);
	font-weight: 650;
	letter-spacing: 0.04em;
}

.launcher-error {
	display: flex;
	align-items: center;
	gap: 7px;
	margin: 0;
	flex: none;
	padding: 8px 14px;
	background: color-mix(in srgb, var(--danger) 8%, transparent);
	color: var(--danger);
	font-size: var(--fs-11);
}

.view-scroll {
	min-height: 0;
	flex: 1 1 auto;
	overflow-y: auto;
	padding-bottom: 6px;
}

.view-scroll h2 {
	margin: 10px 12px 4px;
	color: var(--faint);
	font-size: var(--fs-10);
	font-weight: 700;
	letter-spacing: 0.08em;
	text-transform: uppercase;
}

.view-list {
	margin: 0;
	padding: 0 6px;
	list-style: none;
}

.view-list li {
	display: flex;
	align-items: center;
	gap: 2px;
}

.view-main {
	display: flex;
	align-items: center;
	gap: 9px;
	flex: 1 1 auto;
	min-width: 0;
	padding: 9px 10px;
	border: 0;
	border-radius: 8px;
	background: transparent;
	color: var(--text);
	font-size: var(--fs-13);
	text-align: left;
	transition: background 0.12s;
}

.view-main:hover {
	background: var(--panel-muted);
}

.view-title {
	flex: 1 1 auto;
	overflow: hidden;
	font-weight: 560;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.view-main code {
	flex: none;
	color: var(--faint);
	font-family: var(--font-mono);
	font-size: var(--fs-10);
}

.view-state {
	flex: none;
	color: var(--accent);
	font-size: var(--fs-10);
}

.view-close {
	display: grid;
	width: 24px;
	height: 24px;
	flex: none;
	place-items: center;
	padding: 0;
	border: 0;
	border-radius: 7px;
	background: transparent;
	color: var(--faint);
}

.view-close:hover {
	background: var(--panel-muted);
	color: var(--text);
}

.empty {
	margin: 0;
	padding: 26px 16px;
	color: var(--faint);
	font-size: var(--fs-12);
	text-align: center;
}

footer {
	flex: none;
	padding: 8px;
	border-top: 1px solid var(--border);
}

footer button {
	display: flex;
	align-items: center;
	justify-content: center;
	width: 100%;
	gap: 7px;
	height: 32px;
	border: 0;
	border-radius: 8px;
	background: transparent;
	color: var(--muted);
	font-size: var(--fs-12);
	font-weight: 560;
	transition: background 0.12s, color 0.12s;
}

footer button:hover {
	background: var(--panel-muted);
	color: var(--text);
}
</style>
