<script setup>
import { computed, ref, watch } from 'vue';
import { LoaderCircle, Search, X, Check } from 'lucide-vue-next';
import ExtensionStatusBadge from '../extensions/ExtensionStatusBadge.vue';

// 绑定维护弹窗：从能力绑定卡片点击数量标签打开，勾选即刻生效。
// item: { id, name, description, status?, bound, managedBy? }
const props = defineProps({
	open: { type: Boolean, default: false },
	kicker: { type: String, required: true },
	title: { type: String, required: true },
	hint: { type: String, default: '' },
	items: { type: Array, default: () => [] },
	emptyText: { type: String, default: '暂无可绑定项' },
	pending: { type: Function, default: () => false },
});

const emit = defineEmits(['close', 'toggle']);

const term = ref('');

watch(
	() => props.open,
	(open) => {
		if (open) term.value = '';
	},
);

const filtered = computed(() => {
	const keyword = term.value.trim().toLowerCase();
	if (!keyword) return props.items;
	return props.items.filter(
		(item) =>
			item.name?.toLowerCase().includes(keyword) ||
			item.id?.toLowerCase().includes(keyword) ||
			item.description?.toLowerCase().includes(keyword),
	);
});

const boundCount = computed(() => props.items.filter((item) => item.bound).length);
</script>

<template>
	<Teleport to="body">
		<div v-if="open" class="dialog-backdrop sc-root" @click.self="emit('close')">
			<div class="dialog-frame" role="dialog" aria-modal="true" :aria-label="title">
				<header class="dialog-head">
					<div class="head-meta">
						<h3>{{ title }}</h3>
					</div>
					<div class="head-right">
						<span class="count-pill" :class="{ off: boundCount === 0 }">
							<span class="count-dot" v-if="boundCount > 0"></span>
							{{ boundCount }} / {{ items.length }}
						</span>
						<button type="button" class="close-btn" aria-label="关闭" @click="emit('close')">
							<X :size="16" :stroke-width="2" />
						</button>
					</div>
				</header>

				<div v-if="items.length" class="search-bar">
					<Search :size="14" :stroke-width="2" class="search-ico" aria-hidden="true" />
					<input v-model="term" type="text" placeholder="搜索名称或标识..." :aria-label="`搜索${title}`" class="search-input" />
				</div>

				<div class="list scroll">
					<div v-if="!items.length" class="empty-state">{{ emptyText }}</div>
					<div v-else-if="!filtered.length" class="empty-state">没有匹配的条目</div>
					<label
						v-for="item in filtered"
						:key="item.id"
						class="row-item"
						:class="{ bound: item.bound, locked: Boolean(item.managedBy) }"
					>
						<div class="toggle-cell">
							<input
								type="checkbox"
								:checked="item.bound"
								:disabled="Boolean(item.managedBy) || pending(item.id)"
								:aria-label="`绑定 ${item.name}`"
								@change="emit('toggle', item, $event.target.checked)"
								class="custom-check"
							/>
						</div>
						<div class="r-main">
							<div class="r-titleline">
								<span class="r-name">{{ item.name }}</span>
								<code class="r-id">{{ item.id }}</code>
								<span v-if="item.managedBy" class="r-managed-tag">VIA {{ item.managedBy.toUpperCase() }}</span>
								<ExtensionStatusBadge v-if="item.status" :status="item.status" />
							</div>
							<div v-if="item.description" class="r-desc">{{ item.description }}</div>
						</div>
						<div v-if="pending(item.id)" class="pending-cell">
							<LoaderCircle :size="15" :stroke-width="2.2" class="spin" aria-hidden="true" />
						</div>
					</label>
				</div>

				<footer class="dialog-foot">
					<button type="button" class="btn btn-primary" @click="emit('close')">
						<Check :size="14" :stroke-width="2.4" />
						<span>完成配置</span>
					</button>
				</footer>
			</div>
		</div>
	</Teleport>
</template>

<style scoped>
@import '../../../styles/settings-console.css';

.dialog-backdrop {
	position: fixed;
	inset: 0;
	z-index: 1200;
	display: grid;
	place-items: center;
	padding: 24px;
	background: var(--overlay-heavy);
	backdrop-filter: blur(12px);
	animation: sc-fade 0.18s var(--sc-ease-out);
	font-family: var(--sc-sans);
}

.dialog-frame {
	position: relative;
	display: flex;
	flex-direction: column;
	width: min(580px, calc(100vw - 32px));
	max-height: min(680px, calc(100vh - 48px));
	border: 1px solid var(--sc-line-2);
	border-radius: 16px;
	background: var(--sc-panel);
	color: var(--sc-text);
	box-shadow: 0 32px 100px rgba(var(--shadow-ink), 0.35), 0 4px 20px rgba(var(--shadow-ink), 0.1);
	overflow: hidden;
	animation: sc-pop 0.22s var(--sc-ease-spring);
}

.dialog-head {
	display: flex;
	align-items: center;
	justify-content: space-between;
	padding: 22px 24px 16px;
	border-bottom: 1px solid var(--sc-line);
	background: linear-gradient(to bottom, var(--sc-raise), transparent);
}

.head-meta {
	min-width: 0;
	flex: 1;
}

.dlg-kicker {
	margin-bottom: 3px;
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
	font-weight: 650;
	letter-spacing: 0.18em;
}

h3 {
	margin: 0;
	font-family: var(--sc-display);
	font-size: var(--fs-18);
	font-weight: 650;
	color: var(--sc-text);
}

.head-right {
	display: flex;
	align-items: center;
	gap: 12px;
}

.count-pill {
	display: inline-flex;
	align-items: center;
	gap: 6px;
	padding: 4px 10px;
	border: 1px solid color-mix(in srgb, var(--sc-acid) 35%, transparent);
	border-radius: 99px;
	background: var(--sc-acid-soft);
	color: var(--sc-acid);
	font-family: var(--sc-mono);
	font-size: var(--fs-105);
	font-weight: 650;
	letter-spacing: 0.06em;
}

.count-pill.off {
	border-color: var(--sc-line-2);
	background: var(--sc-raise);
	color: var(--sc-mute);
}

.count-dot {
	width: 5px;
	height: 5px;
	border-radius: 50%;
	background: var(--sc-acid);
}

.close-btn {
	display: grid;
	place-items: center;
	width: 32px;
	height: 32px;
	border: 1px solid var(--sc-line);
	border-radius: 8px;
	background: var(--sc-panel);
	color: var(--sc-mute);
	cursor: pointer;
	transition: all 0.16s var(--sc-ease-out);
}

.close-btn:hover {
	border-color: var(--sc-line-2);
	background: var(--sc-hover);
	color: var(--sc-text);
	transform: scale(1.05);
}

.search-bar {
	position: relative;
	display: flex;
	align-items: center;
	padding: 12px 24px;
	border-bottom: 1px solid var(--sc-line);
	background: var(--sc-panel);
}

.search-ico {
	position: absolute;
	left: 36px;
	color: var(--sc-faint);
	pointer-events: none;
}

.search-input {
	width: 100%;
	height: 36px;
	padding: 0 12px 0 32px;
	border: 1px solid var(--sc-line-2);
	border-radius: 8px;
	background: var(--sc-raise);
	color: var(--sc-text);
	font: inherit;
	font-size: var(--fs-125);
	outline: none;
	transition: border-color 0.16s, box-shadow 0.16s;
}

.search-input:focus {
	border-color: var(--sc-acid);
	box-shadow: 0 0 0 2px var(--sc-acid-soft);
}

.list {
	flex: 1;
	min-height: 0;
	overflow-y: auto;
	padding: 12px 18px;
	display: flex;
	flex-direction: column;
	gap: 6px;
}

.scroll::-webkit-scrollbar {
	width: 7px;
}

.scroll::-webkit-scrollbar-thumb {
	background: var(--sc-line-2);
	border-radius: 99px;
}

.empty-state {
	padding: 48px 16px;
	text-align: center;
	color: var(--sc-mute);
	font-size: var(--fs-13);
}

.row-item {
	display: flex;
	align-items: flex-start;
	gap: 12px;
	padding: 12px 14px;
	border: 1px solid var(--sc-line);
	border-radius: 10px;
	background: var(--sc-panel);
	cursor: pointer;
	user-select: none;
	transition: all 0.15s var(--sc-ease-out);
}

.row-item:hover {
	border-color: var(--sc-line-2);
	background: var(--sc-hover);
}

.row-item.bound {
	border-color: color-mix(in srgb, var(--sc-acid) 35%, transparent);
	background: color-mix(in srgb, var(--sc-acid) 4%, var(--sc-panel));
}

.row-item.locked {
	opacity: 0.65;
	cursor: not-allowed;
}

.toggle-cell {
	margin-top: 2px;
}

.custom-check {
	width: 16px;
	height: 16px;
	margin: 0;
	cursor: pointer;
	accent-color: var(--sc-acid);
}

.r-main {
	flex: 1;
	min-width: 0;
	display: flex;
	flex-direction: column;
	gap: 3px;
}

.r-titleline {
	display: flex;
	align-items: center;
	gap: 8px;
	flex-wrap: wrap;
}

.r-name {
	font-size: var(--fs-13);
	font-weight: 620;
	color: var(--sc-text);
}

.r-id {
	display: inline-block;
	padding: 1px 6px;
	border: 1px solid var(--sc-line);
	border-radius: 4px;
	background: var(--sc-raise);
	color: var(--sc-soft);
	font-family: var(--sc-mono);
	font-size: var(--fs-105);
}

.r-managed-tag {
	padding: 1px 6px;
	border: 1px solid var(--sc-line-2);
	border-radius: 4px;
	background: var(--sc-raise);
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
	font-weight: 650;
	letter-spacing: 0.1em;
}

.r-desc {
	color: var(--sc-mute);
	font-size: var(--fs-12);
	line-height: 1.45;
}

.pending-cell {
	display: grid;
	place-items: center;
	margin-top: 2px;
	color: var(--sc-acid);
}

.spin {
	animation: sc-spin 0.8s linear infinite;
}

.dialog-foot {
	display: flex;
	align-items: center;
	justify-content: flex-end;
	padding: 14px 24px;
	border-top: 1px solid var(--sc-line);
	background: var(--sc-raise);
}

.btn {
	display: inline-flex;
	align-items: center;
	justify-content: center;
	gap: 7px;
	height: 38px;
	padding: 0 18px;
	border: 1px solid transparent;
	border-radius: 9px;
	font-family: inherit;
	font-size: var(--fs-13);
	font-weight: 600;
	cursor: pointer;
	transition: all 0.18s var(--sc-ease-spring);
}

.btn-primary {
	border-color: var(--sc-acid);
	background: var(--sc-acid);
	color: var(--sc-acid-ink);
	box-shadow: 0 1px 3px rgba(var(--shadow-ink), 0.08);
}

.btn-primary:hover {
	transform: translateY(-1px);
	box-shadow: 0 3px 8px rgba(var(--shadow-ink), 0.12);
}
</style>
