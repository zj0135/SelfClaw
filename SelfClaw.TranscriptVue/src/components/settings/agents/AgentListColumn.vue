<script setup>
import { computed, ref } from 'vue';
import { Bot, Search, Workflow, Plus } from 'lucide-vue-next';

const props = defineProps({
	agents: { type: Array, default: () => [] },
	subagents: { type: Array, default: () => [] },
	activeKind: { type: String, default: 'agent' },
	selectedId: { type: String, default: '' },
	loading: { type: Boolean, default: false },
});

const emit = defineEmits(['update:activeKind', 'select', 'create']);

const term = ref('');

const bindingCount = (agent) =>
	(agent.pluginIds?.length || 0) +
	(agent.skillIds?.length || 0) +
	(agent.mcpServerIds?.length || 0) +
	(agent.subagentIds?.length || 0);

const groups = computed(() => {
	const keyword = term.value.trim().toLowerCase();
	const match = (item) =>
		!keyword ||
		item.name?.toLowerCase().includes(keyword) ||
		item.id?.toLowerCase().includes(keyword) ||
		item.description?.toLowerCase().includes(keyword);

	if (props.activeKind === 'agent') {
		const matched = props.agents.filter(match);
		return [
			{ label: '内置', en: 'BUILT-IN', live: true, items: matched.filter((agent) => agent.isBuiltIn) },
			{ label: '自定义', en: 'CUSTOM', live: false, items: matched.filter((agent) => !agent.isBuiltIn) },
		].filter((group) => group.items.length > 0);
	}

	const matched = props.subagents.filter(match);
	return [
		{ label: '可用', en: 'READY', live: true, items: matched.filter((subagent) => subagent.isValid) },
		{ label: '需修复', en: 'INVALID', live: false, items: matched.filter((subagent) => !subagent.isValid) },
	].filter((group) => group.items.length > 0);
});

const totalCount = computed(() =>
	props.activeKind === 'agent' ? props.agents.length : props.subagents.length);

function itemIndex(item) {
	const list = props.activeKind === 'agent' ? props.agents : props.subagents;
	const index = list.indexOf(item);
	return index >= 0 ? String(index + 1).padStart(2, '0') : '00';
}

function subText(item) {
	if (props.activeKind === 'agent') {
		return `${item.mode === 'cli' ? 'CLI' : 'DIRECT'} · 绑定 ${bindingCount(item)} 项`;
	}
	return `${item.toolPolicy} · ${item.maxRunSeconds}s`;
}

function itemHealthy(item) {
	return props.activeKind === 'agent' ? !(item.warnings?.length > 0) : item.isValid;
}
</script>

<template>
	<aside class="list-col">
		<div class="list-top sc-rise" style="--i: 0">
			<div class="list-kicker">
				<span>{{ activeKind === 'agent' ? 'AGENT INDEX' : 'SUBAGENT INDEX' }}</span>
				<span class="list-kicker-count">{{ totalCount }} 项</span>
			</div>
			<div class="seg-wrap">
				<div class="seg" role="tablist" aria-label="定义类型">
					<button type="button" role="tab" :aria-selected="activeKind === 'agent'"
						:class="{ active: activeKind === 'agent' }" @click="emit('update:activeKind', 'agent')">
						<Bot :size="13" :stroke-width="2.1" aria-hidden="true" />
						代理
					</button>
					<button type="button" role="tab" :aria-selected="activeKind === 'subagent'"
						:class="{ active: activeKind === 'subagent' }" @click="emit('update:activeKind', 'subagent')">
						<Workflow :size="13" :stroke-width="2.1" aria-hidden="true" />
						子代理
					</button>
				</div>
				<button
					type="button"
					class="add-btn"
					:title="`新增${activeKind === 'agent' ? '代理' : '子代理'}`"
					:aria-label="`新增${activeKind === 'agent' ? '代理' : '子代理'}`"
					@click="emit('create')"
				>
					<Plus :size="15" :stroke-width="2.2" aria-hidden="true" />
				</button>
			</div>
			<div class="search">
				<Search :size="14" :stroke-width="2" class="search-ico" aria-hidden="true" />
				<input v-model="term" type="text" :placeholder="activeKind === 'agent' ? '搜索代理...' : '搜索子代理...'"
					:aria-label="activeKind === 'agent' ? '搜索代理' : '搜索子代理'" />
			</div>
		</div>

		<div class="list-scroll">
			<div v-if="loading" class="list-empty">正在加载定义…</div>
			<template v-else-if="groups.length">
				<div v-for="group in groups" :key="group.en" class="list-group">
					<div class="grp-label">
						<span class="grp-dot" :class="{ live: group.live }" aria-hidden="true"></span>
						<span>{{ group.label }}</span>
						<span class="grp-en">{{ group.en }}</span>
					</div>
					<button v-for="(item, index) in group.items" :key="item.id" type="button" class="entry sc-rise"
						:style="{ '--i': index + 1 }" :class="{ active: item.id === selectedId }"
						@click="emit('select', item.id)">
						<span class="e-index">{{ itemIndex(item) }}</span>
						<span class="e-icon" aria-hidden="true">
							<Bot v-if="activeKind === 'agent'" :size="17" :stroke-width="1.9" />
							<Workflow v-else :size="17" :stroke-width="1.9" />
						</span>
						<span class="e-meta">
							<span class="e-name">{{ item.name }}</span>
							<span class="e-sub">{{ subText(item) }}</span>
						</span>
						<span class="dot" :class="{ bad: !itemHealthy(item) }" aria-hidden="true"></span>
					</button>
				</div>
			</template>
			<div v-else class="list-empty">
				{{ term ? '没有匹配的定义' : activeKind === 'agent' ? '暂无代理定义' : '暂无子代理定义' }}
			</div>
		</div>
	</aside>
</template>

<style scoped>
@import '../../../styles/settings-console.css';

.list-col {
	display: flex;
	min-height: 0;
	flex-direction: column;
	border-right: 1px solid var(--sc-line);
	background: color-mix(in srgb, var(--sc-panel) 72%, transparent);
}

.list-top {
	padding: 20px 16px 14px;
	border-bottom: 1px solid var(--sc-line);
}

.list-kicker {
	display: flex;
	align-items: baseline;
	justify-content: space-between;
	margin-bottom: 14px;
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-10);
	font-weight: 600;
	letter-spacing: 0.22em;
}

.list-kicker-count {
	color: var(--sc-acid);
	letter-spacing: 0.12em;
}

.seg-wrap {
	display: flex;
	align-items: center;
	gap: 6px;
	margin-bottom: 10px;
}

.seg {
	display: grid;
	grid-template-columns: 1fr 1fr;
	gap: 4px;
	flex: 1;
	padding: 4px;
	border: 1px solid var(--sc-line);
	border-radius: 10px;
	background: var(--sc-raise);
}

.seg button {
	display: inline-flex;
	align-items: center;
	justify-content: center;
	gap: 6px;
	height: 30px;
	border: 0;
	border-radius: 7px;
	background: transparent;
	color: var(--sc-mute);
	cursor: pointer;
	font: inherit;
	font-size: var(--fs-125);
	font-weight: 560;
	transition:
		background 0.16s var(--sc-ease-out),
		color 0.16s var(--sc-ease-out),
		box-shadow 0.16s var(--sc-ease-out);
}

.seg button:hover {
	color: var(--sc-text);
}

.seg button.active {
	background: var(--sc-panel);
	color: var(--sc-acid);
	box-shadow: 0 2px 8px rgba(var(--shadow-ink), 0.08);
}

.add-btn {
	display: grid;
	width: 38px;
	height: 38px;
	flex: 0 0 auto;
	place-items: center;
	border: 1px solid var(--sc-line);
	border-radius: 10px;
	background: var(--sc-raise);
	color: var(--sc-soft);
	cursor: pointer;
	transition:
		background 0.16s var(--sc-ease-out),
		color 0.16s var(--sc-ease-out),
		border-color 0.16s var(--sc-ease-out),
		box-shadow 0.16s var(--sc-ease-out);
}

.add-btn:hover {
	border-color: color-mix(in srgb, var(--sc-acid) 35%, transparent);
	background: var(--sc-panel);
	color: var(--sc-acid);
	box-shadow: 0 2px 8px rgba(var(--shadow-ink), 0.08);
}

.add-btn:active {
	transform: scale(0.96);
}

.search {
	position: relative;
	display: flex;
	flex: 1;
	align-items: center;
	min-width: 0;
}

.search-ico {
	position: absolute;
	left: 11px;
	color: var(--sc-faint);
	pointer-events: none;
}

.search input {
	width: 100%;
	padding: 9px 10px 9px 33px;
	border: 1px solid var(--sc-line);
	border-radius: 8px;
	background: var(--sc-panel);
	color: var(--sc-text);
	font: inherit;
	font-size: var(--fs-13);
	transition: border-color 0.16s, box-shadow 0.16s, background 0.16s;
}

.search input::placeholder {
	color: var(--sc-faint);
}

.search input:focus {
	border-color: color-mix(in srgb, var(--sc-acid) 55%, transparent);
	outline: none;
	background: var(--sc-panel);
	box-shadow: 0 0 0 3px var(--sc-acid-soft);
}

.list-scroll {
	min-height: 0;
	flex: 1;
	overflow-y: auto;
	padding: 6px 12px 16px;
	scrollbar-width: thin;
	scrollbar-color: var(--sc-faint) transparent;
}

.list-scroll::-webkit-scrollbar {
	width: 9px;
}

.list-scroll::-webkit-scrollbar-thumb {
	background: var(--sc-raise);
	background-clip: padding-box;
	border: 2px solid transparent;
	border-radius: 99px;
}

.list-scroll::-webkit-scrollbar-thumb:hover {
	background: var(--sc-faint);
}

.grp-label {
	display: flex;
	align-items: center;
	gap: 7px;
	padding: 16px 8px 8px;
	color: var(--sc-mute);
	font-size: var(--fs-11);
	font-weight: 600;
	letter-spacing: 0.05em;
}

.grp-en {
	margin-left: auto;
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
	font-weight: 500;
	letter-spacing: 0.2em;
}

.grp-dot {
	width: 5px;
	height: 5px;
	border-radius: 50%;
	background: var(--sc-faint);
}

.grp-dot.live {
	background: var(--sc-ok);
	box-shadow: 0 0 8px color-mix(in srgb, var(--success) 45%, transparent);
	animation: sc-blink 2.6s ease-in-out infinite;
}

.entry {
	position: relative;
	display: flex;
	align-items: center;
	width: 100%;
	gap: 10px;
	padding: 10px;
	border: 1px solid transparent;
	border-radius: 10px;
	background: transparent;
	color: inherit;
	text-align: left;
	cursor: pointer;
	user-select: none;
	transition:
		background 0.15s,
		border-color 0.15s,
		transform 0.15s var(--sc-ease-out);
}

.entry:hover {
	background: var(--sc-hover);
	transform: translateX(2px);
}

.entry.active {
	border-color: var(--sc-line-2);
	background: var(--sc-panel);
	box-shadow: 0 2px 10px rgba(var(--shadow-ink), 0.05);
}

.entry.active::before {
	position: absolute;
	top: 50%;
	left: -1px;
	width: 2px;
	height: 22px;
	transform: translateY(-50%);
	border-radius: 2px;
	background: var(--sc-acid);
	box-shadow: 0 0 10px color-mix(in srgb, var(--accent) 40%, transparent);
	content: '';
}

.e-index {
	width: 18px;
	flex: 0 0 auto;
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-10);
	letter-spacing: 0.06em;
	transition: color 0.15s;
}

.entry.active .e-index {
	color: var(--sc-acid);
}

.e-icon {
	display: grid;
	width: 34px;
	height: 34px;
	flex: 0 0 auto;
	place-items: center;
	border: 1px solid var(--sc-line);
	border-radius: 9px;
	background: var(--sc-panel);
	color: var(--sc-soft);
	transition: color 0.15s, border-color 0.15s;
}

.entry.active .e-icon {
	border-color: color-mix(in srgb, var(--sc-acid) 35%, transparent);
	color: var(--sc-acid);
}

.e-meta {
	display: grid;
	min-width: 0;
	flex: 1;
}

.e-name {
	overflow: hidden;
	color: var(--sc-text);
	font-size: var(--fs-135);
	font-weight: 560;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.e-sub {
	margin-top: 1px;
	overflow: hidden;
	color: var(--sc-mute);
	font-family: var(--sc-mono);
	font-size: var(--fs-105);
	letter-spacing: 0.02em;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.dot {
	width: 6px;
	height: 6px;
	flex: 0 0 auto;
	border-radius: 50%;
	background: var(--sc-ok);
	box-shadow: 0 0 8px color-mix(in srgb, var(--success) 45%, transparent);
	transition: background 0.18s, box-shadow 0.18s;
}

.dot.bad {
	background: var(--sc-err);
	box-shadow: 0 0 8px color-mix(in srgb, var(--danger) 40%, transparent);
}

.list-empty {
	padding: 30px 10px;
	color: var(--sc-mute);
	font-size: var(--fs-13);
	text-align: center;
}

@media (max-width: 760px) {
	.list-col {
		max-height: 320px;
		border-right: 0;
		border-bottom: 1px solid var(--sc-line);
	}
}
</style>
