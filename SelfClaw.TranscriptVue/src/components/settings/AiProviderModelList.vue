<script setup>
import { computed, ref } from 'vue';
import { Search, Plus, RefreshCw, Eye, SlidersHorizontal, Trash2, Image } from 'lucide-vue-next';

const props = defineProps({
	provider: { type: Object, required: true },
	logo: { type: String, required: true },
	busy: Boolean,
	fetching: Boolean,
	pendingModelIds: { type: Set, required: true },
});
const emit = defineEmits(['set-all-enabled', 'fetch-models', 'add-model', 'configure', 'delete-model', 'set-enabled']);
const search = ref('');
const enabledCount = computed(() => props.provider.models.filter((model) => model.on).length);
const filteredModels = computed(() => {
	const term = search.value.trim().toLowerCase();
	return props.provider.models.filter((model) => `${model.name} ${model.id}`.toLowerCase().includes(term));
});
</script>

<template>
	<section class="model-registry">
		<header>
			<div>
				<h3>模型列表</h3>
				<p>共 {{ provider.models.length }} 个模型，已启用 {{ enabledCount }}</p>
			</div>
			<span class="count">{{ enabledCount }} / {{ provider.models.length }}</span>
		</header>
		<div class="toolbar">
			<label class="search"><Search :size="14" /><input v-model="search" type="search" placeholder="搜索模型..." aria-label="搜索模型" /></label>
			<button type="button" :disabled="busy || !provider.connectionId || !provider.models.length" @click="emit('set-all-enabled', true)">
				全部启用
			</button>
			<button type="button" :disabled="busy || !provider.connectionId || !provider.models.length" @click="emit('set-all-enabled', false)">
				全部禁用
			</button>
			<button
				type="button"
				class="fetch"
				:disabled="fetching || !provider.connectionId || !provider.supportsModelListing"
				@click="emit('fetch-models')"
			>
				<RefreshCw :size="14" :class="{ spinning: fetching }" />获取模型列表
			</button>
			<button type="button" class="icon" aria-label="添加模型" title="添加模型" :disabled="!provider.connectionId" @click="emit('add-model')">
				<Plus :size="16" />
			</button>
		</div>
		<div v-if="!filteredModels.length" class="empty">{{ provider.models.length ? '没有匹配的模型' : '暂无模型' }}</div>
		<div v-for="model in filteredModels" :key="model.profileId" class="model">
			<img class="logo" :src="logo" alt="" />
			<div class="model-main">
				<div class="title">
					<strong>{{ model.name }}</strong
					><Image v-if="model.configuration?.isMultimodal" :size="14" aria-label="支持图像输入" />
				</div>
				<code>{{ model.id }}</code>
				<div class="metadata">
					<span>{{ model.ctx }} 上下文</span><span>{{ model.out }} 输出</span
					><span v-if="model.configuration?.reasoningEffort">{{ model.configuration.reasoningEffort }}</span>
				</div>
				<div v-if="model.inp !== '—' || model.outp !== '—'" class="prices">IN {{ model.inp }} / OUT {{ model.outp }}</div>
				<div v-if="model.cacheR || model.cacheW" class="cache">CACHE R {{ model.cacheR || '—' }} / W {{ model.cacheW || '—' }}</div>
			</div>
			<div class="actions">
				<button class="icon" type="button" title="查看详情" aria-label="查看详情" @click="emit('configure', model, 'metadata')">
					<Eye :size="15" />
				</button>
				<button class="icon" type="button" title="模型参数" aria-label="模型参数" @click="emit('configure', model, 'parameters')">
					<SlidersHorizontal :size="15" />
				</button>
				<button
					class="icon danger"
					type="button"
					title="删除模型"
					aria-label="删除模型"
					:disabled="pendingModelIds.has(model.profileId)"
					@click="emit('delete-model', model)"
				>
					<Trash2 :size="15" />
				</button>
				<label class="toggle" title="启用模型"
					><input
						type="checkbox"
						:checked="model.on"
						aria-label="启用模型"
						:disabled="pendingModelIds.has(model.profileId)"
						@change="emit('set-enabled', model, $event.target.checked)" /><span></span
				></label>
			</div>
		</div>
	</section>
</template>

<style scoped>
@import '../../styles/ai-provider-model-list.css';
</style>
