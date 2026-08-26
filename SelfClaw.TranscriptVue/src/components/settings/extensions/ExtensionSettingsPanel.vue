<script setup>
import { computed, ref } from 'vue';
import { AlertCircle } from 'lucide-vue-next';
import { useExtensionSettings } from '../../../composables/useExtensionSettings';
import { useToast } from '../../../composables/useToast';
import ExtensionCategoryTabs from './ExtensionCategoryTabs.vue';
import ExtensionDetailDrawer from './ExtensionDetailDrawer.vue';
import ExtensionList from './ExtensionList.vue';
import ExtensionToolbar from './ExtensionToolbar.vue';
import McpServerDialog from './McpServerDialog.vue';
import PackageImportDialog from './PackageImportDialog.vue';
import PermissionReviewDialog from './PermissionReviewDialog.vue';

const {
	state,
	counts,
	loading,
	error,
	isPending,
	load,
	setEnabled,
	acknowledgePluginPermissions,
	deleteItem,
	saveMcp,
	testMcp,
	importPackage,
} = useExtensionSettings();

const { showToast } = useToast();

const activeCategory = ref('plugin');
const search = ref('');
const selectedId = ref(null);
const mcpDialogOpen = ref(false);
const editingMcp = ref(null);
const importResult = ref(null);
const permissionPlugin = ref(null);

const collectionNames = {
	plugin: 'plugins',
	skill: 'skills',
	mcpServer: 'mcpServers',
};

const activeItems = computed(() => state.value[collectionNames[activeCategory.value]] || []);
const filteredItems = computed(() => {
	const term = search.value.trim().toLowerCase();
	if (!term) return activeItems.value;
	return activeItems.value.filter((item) =>
		[item.name, item.id, item.description].some((value) => value?.toLowerCase().includes(term)),
	);
});
const selectedItem = computed(() => activeItems.value.find((item) => item.id === selectedId.value) || null);

function changeCategory(category) {
	activeCategory.value = category;
	selectedId.value = null;
	search.value = '';
}

function openMcp(server = null) {
	editingMcp.value = server;
	mcpDialogOpen.value = true;
}

async function handleSaveMcp(command, testAfterSave = false) {
	const saved = await saveMcp(command);
	if (!saved) return;
	if (testAfterSave) await testMcp(saved.id);
	mcpDialogOpen.value = false;
	activeCategory.value = 'mcpServer';
	selectedId.value = saved.id;
}

async function handleDelete() {
	const item = selectedItem.value;
	if (!item) return;
	if (await deleteItem(activeCategory.value, item)) {
		selectedId.value = null;
		showToast(`${item.name} 已删除`);
	}
}

async function handleImport() {
	const result = await importPackage(activeCategory.value);
	if (!result) return;
	importResult.value = result;
	selectedId.value = result.package.id;
	const imported = activeItems.value.find((item) => item.id === result.package.id);
	if (imported?.status === 'needs-permission') permissionPlugin.value = imported;
}

function selectItem(item) {
	selectedId.value = item.id;
	if (activeCategory.value === 'plugin' && item.status === 'needs-permission') {
		permissionPlugin.value = item;
	}
}

async function handleToggle(item, enabled) {
	if (activeCategory.value === 'plugin' && enabled && item.unacknowledgedPermissions?.length) {
		permissionPlugin.value = item;
		return;
	}

	await setEnabled(activeCategory.value, item, enabled);
}

async function confirmPermissions() {
	const plugin = permissionPlugin.value;
	if (!plugin) return;
	if (!await acknowledgePluginPermissions(plugin)) return;
	if (await setEnabled('plugin', plugin, true)) permissionPlugin.value = null;
}
</script>

<template>
	<main class="extensions-page sc-root sc-stage sc-page">
		<header class="sc-page-head page-head sc-rise" style="--i: 0">
			<span class="sc-page-ghost" aria-hidden="true">Registry</span>
			<div>
				<span>EXTENSION REGISTRY</span>
				<h1>扩展</h1>
				<p>管理插件、技能与 Direct 模式使用的 MCP 服务器。</p>
			</div>
			<code>REV {{ state.revision }}</code>
		</header>

		<ExtensionCategoryTabs
			:model-value="activeCategory"
			:counts="counts"
			@update:model-value="changeCategory"
		/>

		<div v-if="error" class="error-bar"><AlertCircle :size="15" />{{ error }}</div>

		<div class="workspace" :class="{ inspecting: selectedItem }">
			<section class="registry">
				<ExtensionToolbar
					v-model="search"
					:category="activeCategory"
					:loading="loading"
					@refresh="load"
					@add-mcp="openMcp()"
					@import-package="handleImport"
				/>
				<ExtensionList
					:items="filteredItems"
					:selected-id="selectedId"
					:kind="activeCategory"
					:is-pending="isPending"
					@select="selectItem"
					@toggle="handleToggle"
				/>
			</section>

			<ExtensionDetailDrawer
				v-if="selectedItem"
				:item="selectedItem"
				:kind="activeCategory"
				:pending="isPending(activeCategory, selectedItem.id)"
				@close="selectedId = null"
				@delete="handleDelete"
				@edit="openMcp(selectedItem)"
			/>
		</div>

		<McpServerDialog
			:open="mcpDialogOpen"
			:server="editingMcp"
			:saving="isPending('mcpServer', editingMcp?.id || 'new')"
			@close="mcpDialogOpen = false"
			@save="handleSaveMcp"
		/>
		<PackageImportDialog
			:open="Boolean(importResult)"
			:result="importResult"
			@close="importResult = null"
		/>
		<PermissionReviewDialog
			:open="Boolean(permissionPlugin)"
			:plugin="permissionPlugin"
			:pending="permissionPlugin ? isPending('plugin', permissionPlugin.id) : false"
			@close="permissionPlugin = null"
			@confirm="confirmPermissions"
		/>
	</main>
</template>

<style scoped>
@import '../../../styles/settings-console.css';
.page-head span { display: inline-flex; align-items: center; gap: 7px; color: var(--sc-faint); font-family: var(--sc-mono); font-size: 9px; font-weight: 650; }
.page-head span::before { width: 14px; height: 1px; background: var(--sc-acid); content: ''; }
.page-head h1 { margin: 5px 0 3px; font-size: 25px; font-weight: 650; letter-spacing: 0; }
.page-head p { margin: 0; color: var(--sc-mute); font-size: 12px; }
.page-head code { color: var(--sc-faint); font-family: var(--sc-mono); font-size: 9px; }
.extensions-page > :deep(.tabs) { padding: 0 14px; }
.error-bar { display: flex; align-items: center; gap: 8px; margin: 10px 28px 0; padding: 9px 11px; border: 1px solid rgba(220,69,69,.2); border-radius: 5px; background: var(--sc-err-soft); color: var(--sc-err); font-size: 11px; }
.workspace { display: grid; grid-template-columns: minmax(0, 1fr); min-height: 0; flex: 1; background: linear-gradient(to bottom, rgba(245,246,248,.9), rgba(245,246,248,0) 44px), var(--sc-panel); }
.workspace.inspecting { grid-template-columns: minmax(360px, 1fr) minmax(260px, 34%); }
.registry { min-width: 0; overflow-y: auto; padding: 0 28px 24px; }
@media (max-width: 780px) {
	.workspace.inspecting { grid-template-columns: 1fr; }
	.workspace.inspecting .registry { display: none; }
	.registry { padding-inline: 18px; }
}
</style>
