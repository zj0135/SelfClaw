<script setup>
import { computed, ref } from 'vue';
import { Search, Plus, RefreshCw, Eye, SlidersHorizontal, Trash2, Image, Sparkles, Cpu } from 'lucide-vue-next';

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
		<header class="registry-head">
			<div class="head-left">
				<h3 class="title">模型列表</h3>
			</div>
			<div class="head-right">
				<span class="count-badge" :class="{ 'has-active': enabledCount > 0 }">
					<span class="pulse-dot" v-if="enabledCount > 0"></span>
					{{ enabledCount }} / {{ provider.models.length }}
				</span>
			</div>
		</header>

		<div class="toolbar">
			<div class="search-wrap">
				<Search :size="14" class="search-icon" aria-hidden="true" />
				<input v-model="search" type="search" placeholder="搜索模型名称或标识..." aria-label="搜索模型" class="search-input" />
			</div>

			<div class="actions-group">
				<div class="seg-batch">
					<button
						type="button"
						class="btn-batch"
						:disabled="busy || !provider.connectionId || !provider.models.length"
						@click="emit('set-all-enabled', true)"
					>
						全部启用
					</button>
					<button
						type="button"
						class="btn-batch"
						:disabled="busy || !provider.connectionId || !provider.models.length"
						@click="emit('set-all-enabled', false)"
					>
						全部禁用
					</button>
				</div>

				<button
					type="button"
					class="btn-action fetch-btn"
					:disabled="fetching || !provider.connectionId || !provider.supportsModelListing"
					@click="emit('fetch-models')"
					title="向服务商 API 查询并同步最新模型列表"
				>
					<RefreshCw :size="13" :class="{ spinning: fetching }" />
					<span>{{ fetching ? '获取中…' : '获取模型列表' }}</span>
				</button>

				<button
					type="button"
					class="btn-action add-btn"
					aria-label="添加模型"
					title="手动添加模型"
					:disabled="!provider.connectionId"
					@click="emit('add-model')"
				>
					<Plus :size="15" :stroke-width="2.2" />
					<span>添加模型</span>
				</button>
			</div>
		</div>

		<div v-if="!filteredModels.length" class="empty-state">
			<Cpu :size="28" :stroke-width="1.6" class="empty-icon" />
			<span class="empty-text">{{ provider.models.length ? '未找到匹配的模型' : '暂无模型配置，请点击上方“获取模型列表”或“添加模型”' }}</span>
		</div>

		<div v-else class="model-cards">
			<article
				v-for="model in filteredModels"
				:key="model.profileId"
				class="model-card"
				:class="{ 'is-enabled': model.on, 'is-pending': pendingModelIds.has(model.profileId) }"
			>
				<div class="card-primary">
					<div class="card-identity">
						<div class="brand-avatar">
							<img class="logo" :src="logo" alt="" draggable="false" />
						</div>
						<div class="model-titleline">
							<h4 class="model-name" :title="model.name">{{ model.name }}</h4>
							<span v-if="model.configuration?.isMultimodal" class="chip chip-vision" title="支持多模态图像理解">
								<Image :size="11" :stroke-width="2.4" />
								<span>VISION</span>
							</span>
							<span v-if="model.configuration?.reasoningEffort" class="chip chip-reasoning" title="扩展思考等级">
								<Sparkles :size="11" :stroke-width="2.4" />
								<span>{{ model.configuration.reasoningEffort.toUpperCase() }}</span>
							</span>
						</div>
					</div>

					<div class="card-actions">
						<div class="btn-cluster">
							<button
								class="icon-btn"
								type="button"
								title="查看计费与详情"
								aria-label="查看详情"
								@click="emit('configure', model, 'metadata')"
							>
								<Eye :size="15" :stroke-width="2" />
							</button>
							<button
								class="icon-btn"
								type="button"
								title="调整模型参数 (温度/思考/Token)"
								aria-label="模型参数"
								@click="emit('configure', model, 'parameters')"
							>
								<SlidersHorizontal :size="15" :stroke-width="2" />
							</button>
							<button
								class="icon-btn danger"
								type="button"
								title="删除此模型"
								aria-label="删除模型"
								:disabled="pendingModelIds.has(model.profileId)"
								@click="emit('delete-model', model)"
							>
								<Trash2 :size="15" :stroke-width="2" />
							</button>
						</div>

						<label class="custom-toggle" title="启用/禁用模型">
							<input
								type="checkbox"
								:checked="model.on"
								aria-label="启用模型"
								:disabled="pendingModelIds.has(model.profileId)"
								@change="emit('set-enabled', model, $event.target.checked)"
							/>
							<span class="toggle-slider"></span>
						</label>
					</div>
				</div>

				<div class="card-secondary">
					<div class="model-id-wrap">
						<code class="model-id-chip" :title="model.id">{{ model.id }}</code>
					</div>

					<div class="card-telemetry">
						<div class="spec-pills">
							<span class="spec-pill" title="上下文窗口大小">
								<span class="spec-kicker">CTX</span>
								<span class="spec-val">{{ model.ctx }}</span>
							</span>
							<span class="spec-pill" title="最大生成输出限制">
								<span class="spec-kicker">OUT</span>
								<span class="spec-val">{{ model.out }}</span>
							</span>
						</div>

						<div v-if="model.inp !== '—' || model.outp !== '—' || model.cacheR || model.cacheW" class="economics-pills">
							<span v-if="model.inp !== '—' || model.outp !== '—'" class="eco-pill">
								${{ model.inp }}/1M IN · ${{ model.outp }}/1M OUT
							</span>
							<span v-if="model.cacheR || model.cacheW" class="eco-pill cache">
								CACHE R ${{ model.cacheR || '—' }} / W ${{ model.cacheW || '—' }}
							</span>
						</div>
					</div>
				</div>
			</article>
		</div>
	</section>
</template>

<style scoped>
@import '../../styles/ai-provider-model-list.css';
</style>
