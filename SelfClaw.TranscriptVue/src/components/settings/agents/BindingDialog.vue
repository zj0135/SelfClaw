<script setup>
import { computed, ref, watch } from 'vue';
import { LoaderCircle, Search, X } from 'lucide-vue-next';
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
			<div class="dialog" role="dialog" aria-modal="true" :aria-label="title">
				<header>
					<div class="head-meta">
						<div class="dlg-kicker">{{ kicker }}</div>
						<h3>{{ title }}</h3>
						<p v-if="hint">{{ hint }}</p>
					</div>
					<span class="count-pill" :class="{ off: boundCount === 0 }">{{ boundCount }} / {{ items.length }}</span>
					<button type="button" class="close" aria-label="关闭" @click="emit('close')">
						<X :size="16" :stroke-width="2" />
					</button>
				</header>

				<div v-if="items.length" class="search">
					<Search :size="14" :stroke-width="2" class="search-ico" aria-hidden="true" />
					<input v-model="term" type="text" placeholder="搜索名称或 id..." :aria-label="`搜索${title}`" />
				</div>

				<div class="list">
					<div v-if="!items.length" class="empty">{{ emptyText }}</div>
					<div v-else-if="!filtered.length" class="empty">没有匹配项</div>
					<label
						v-for="item in filtered"
						:key="item.id"
						class="row"
						:class="{ bound: item.bound, locked: Boolean(item.managedBy) }"
					>
						<input
							type="checkbox"
							:checked="item.bound"
							:disabled="Boolean(item.managedBy) || pending(item.id)"
							:aria-label="`绑定 ${item.name}`"
							@change="emit('toggle', item, $event.target.checked)"
						/>
						<span class="r-main">
							<span class="r-title">
								<span class="r-name">{{ item.name }}</span>
								<span class="r-id">{{ item.id }}</span>
								<ExtensionStatusBadge v-if="item.status" :status="item.status" />
							</span>
							<span v-if="item.description" class="r-desc">{{ item.description }}</span>
							<span v-if="item.managedBy" class="r-managed">随插件 {{ item.managedBy }} 的绑定自动生效</span>
						</span>
						<LoaderCircle v-if="pending(item.id)" :size="15" :stroke-width="2.2" class="spin" aria-hidden="true" />
					</label>
				</div>

				<footer>
					<button type="button" class="primary" @click="emit('close')">完成</button>
				</footer>
			</div>
		</div>
	</Teleport>
</template>

<style scoped>
@import '../settings-console.css';

.dialog-backdrop {
	position: fixed;
	inset: 0;
	z-index: 1200;
	display: grid;
	place-items: center;
	padding: 24px;
	background: rgba(23, 26, 31, 0.28);
	backdrop-filter: blur(4px);
	animation: sc-fade 160ms ease-out;
	font-family: var(--sc-sans);
}

.dialog {
	display: flex;
	flex-direction: column;
	width: min(560px, 100%);
	max-height: min(640px, calc(100vh - 48px));
	overflow: hidden;
	border: 1px solid var(--sc-line-2);
	border-radius: 16px;
	background: var(--sc-panel);
	box-shadow: 0 32px 90px rgba(23, 26, 31, 0.2);
	color: var(--sc-text);
	animation: sc-pop 240ms var(--sc-ease-out);
}

header {
	display: flex;
	align-items: flex-start;
	gap: 14px;
	padding: 22px 22px 16px;
}

.head-meta {
	min-width: 0;
	flex: 1;
}

.dlg-kicker {
	margin-bottom: 6px;
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: 9.5px;
	font-weight: 600;
	letter-spacing: 0.24em;
}

h3 {
	margin: 0;
	font-family: var(--sc-display);
	font-size: 19px;
	font-weight: 640;
	line-height: 1.3;
}

.head-meta p {
	margin: 5px 0 0;
	color: var(--sc-mute);
	font-size: 12.5px;
	line-height: 1.5;
}

.count-pill {
	flex: 0 0 auto;
	margin-top: 2px;
	padding: 5px 12px;
	border: 1px solid color-mix(in srgb, var(--sc-acid) 35%, transparent);
	border-radius: 99px;
	background: var(--sc-acid-soft);
	color: var(--sc-acid);
	font-family: var(--sc-mono);
	font-size: 11.5px;
	font-weight: 600;
	letter-spacing: 0.04em;
}

.count-pill.off {
	border-color: var(--sc-line);
	background: var(--sc-raise);
	color: var(--sc-mute);
}

.close {
	display: grid;
	width: 30px;
	height: 30px;
	flex: 0 0 auto;
	place-items: center;
	border: 1px solid var(--sc-line);
	border-radius: 8px;
	background: transparent;
	color: var(--sc-mute);
	cursor: pointer;
	transition: background 0.15s, color 0.15s, border-color 0.15s;
}

.close:hover {
	border-color: var(--sc-line-2);
	background: var(--sc-hover);
	color: var(--sc-text);
}

.search {
	position: relative;
	display: flex;
	align-items: center;
	margin: 0 22px 12px;
}

.search-ico {
	position: absolute;
	left: 11px;
	color: var(--sc-faint);
	pointer-events: none;
}

.search input {
	width: 100%;
	box-sizing: border-box;
	padding: 9px 10px 9px 33px;
	border: 1px solid var(--sc-line);
	border-radius: 8px;
	background: var(--sc-panel);
	color: var(--sc-text);
	font: inherit;
	font-size: 13px;
	transition: border-color 0.16s, box-shadow 0.16s;
}

.search input::placeholder {
	color: var(--sc-faint);
}

.search input:focus {
	border-color: color-mix(in srgb, var(--sc-acid) 55%, transparent);
	outline: none;
	box-shadow: 0 0 0 3px var(--sc-acid-soft);
}

.list {
	min-height: 120px;
	flex: 1;
	overflow-y: auto;
	border-top: 1px solid var(--sc-line);
	scrollbar-width: thin;
	scrollbar-color: var(--sc-faint) transparent;
}

.list::-webkit-scrollbar {
	width: 9px;
}

.list::-webkit-scrollbar-thumb {
	background: var(--sc-raise);
	background-clip: padding-box;
	border: 2px solid transparent;
	border-radius: 99px;
}

.empty {
	display: grid;
	place-items: center;
	min-height: 120px;
	color: var(--sc-mute);
	font-size: 12.5px;
}

.row {
	position: relative;
	display: flex;
	align-items: flex-start;
	gap: 12px;
	padding: 12px 22px;
	border-bottom: 1px solid var(--sc-line);
	cursor: pointer;
	transition: background 0.15s;
}

.row:last-child {
	border-bottom: 0;
}

.row:hover {
	background: rgba(19, 27, 45, 0.025);
}

.row.bound {
	background: color-mix(in srgb, var(--sc-acid-soft) 42%, transparent);
}

.row.bound:hover {
	background: color-mix(in srgb, var(--sc-acid-soft) 62%, transparent);
}

.row.locked {
	cursor: default;
}

.row input[type='checkbox'] {
	width: 15px;
	height: 15px;
	flex: 0 0 auto;
	margin: 3px 0 0;
	accent-color: var(--sc-acid);
	cursor: pointer;
}

.row.locked input[type='checkbox'] {
	cursor: default;
}

.r-main {
	display: grid;
	min-width: 0;
	flex: 1;
	gap: 3px;
}

.r-title {
	display: flex;
	align-items: center;
	flex-wrap: wrap;
	gap: 8px;
}

.r-name {
	font-size: 13.5px;
	font-weight: 600;
}

.r-id {
	padding: 2px 7px;
	border: 1px solid var(--sc-line);
	border-radius: 5px;
	background: var(--sc-raise);
	color: var(--sc-soft);
	font-family: var(--sc-mono);
	font-size: 10.5px;
	letter-spacing: 0.02em;
}

.r-desc {
	overflow: hidden;
	color: var(--sc-mute);
	font-size: 12px;
	line-height: 1.5;
	display: -webkit-box;
	-webkit-line-clamp: 2;
	-webkit-box-orient: vertical;
}

.r-managed {
	color: var(--sc-faint);
	font-size: 11px;
}

.spin {
	flex: 0 0 auto;
	margin-top: 3px;
	color: var(--sc-acid);
	animation: sc-spin 0.8s linear infinite;
}

footer {
	display: flex;
	justify-content: flex-end;
	padding: 14px 22px;
	border-top: 1px solid var(--sc-line);
}

footer .primary {
	height: 38px;
	padding: 0 22px;
	border: 1px solid var(--sc-acid);
	border-radius: 9px;
	background: var(--sc-acid);
	color: var(--sc-acid-ink);
	cursor: pointer;
	font: inherit;
	font-size: 13px;
	font-weight: 600;
	transition: transform 0.12s, box-shadow 0.15s;
}

footer .primary:hover {
	transform: translateY(-1px);
	box-shadow: 0 8px 22px rgba(59, 91, 253, 0.2);
}

@media (prefers-reduced-motion: reduce) {
	.dialog-backdrop,
	.dialog {
		animation-duration: 0.001ms;
	}
}
</style>
