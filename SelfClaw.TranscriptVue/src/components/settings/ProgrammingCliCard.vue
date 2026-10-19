<script setup>
import { ChevronDown, Check, Zap, Sparkles } from 'lucide-vue-next';
defineProps({ cli: Object, index: Number, selectedCliId: String, pending: Boolean });
defineEmits(['toggle', 'test', 'select', 'model', 'reasoning']);
</script>

<template>
	<article
		class="cli-card sc-rise"
		:style="{ '--i': index + 2 }"
		:class="{ 'is-open': cli.isOpen, 'is-selected': selectedCliId === cli.id }"
	>
		<div
			class="cli-row"
			role="button"
			tabindex="0"
			:aria-expanded="cli.isOpen ? 'true' : 'false'"
			@click="$emit('toggle', cli)"
			@keydown.enter.prevent="$emit('toggle', cli)"
			@keydown.space.prevent="$emit('toggle', cli)"
		>
			<div class="cli-icon-shell" :style="{ background: cli.iconBackground || 'var(--sc-raise)' }">
				<img v-if="cli.iconSrc" class="cli-svg" :src="cli.iconSrc" alt="" aria-hidden="true" />
				<span v-else class="cli-initials" aria-hidden="true">{{ cli.iconFallback }}</span>
			</div>

			<div class="cli-body">
				<div class="cli-titleline">
					<span class="cli-name">{{ cli.name }}</span>
					<span v-if="cli.vendor" class="cli-vendor">{{ cli.vendor }}</span>
					<span v-if="selectedCliId === cli.id" class="badge badge--selected">
						<span class="active-led"></span>
						<Check :size="11" :stroke-width="3" aria-hidden="true" />
						<span>已设为默认</span>
					</span>
				</div>

				<div class="cli-meta">
					<span v-if="cli.version" class="ver">{{ cli.version }}</span>
					<div class="model-chip" title="当前选中的默认模型">
						<span class="model-name">{{ cli.selectedModel }}</span>
					</div>
					<div v-if="cli.selectedReasoningLevel" class="reasoning-chip" title="推理等级">
						<Sparkles :size="10" />
						<span>{{ cli.selectedReasoningLevel }}</span>
					</div>
				</div>
			</div>

			<button
				class="cli-expand"
				type="button"
				:aria-label="cli.isOpen ? '收起' : '展开'"
				@click.stop="$emit('toggle', cli)"
			>
				<ChevronDown :size="18" :stroke-width="2.2" aria-hidden="true" />
			</button>
		</div>

		<div class="cli-config">
			<div class="cli-config__inner">
				<div class="cli-config__controls">
					<!-- Model Selection -->
					<div class="control-unit model-unit">
						<label class="control-unit__label" :for="`model-${cli.id}`">模型配置</label>
						<div class="select-wrap">
							<select
								:id="`model-${cli.id}`"
								:value="cli.selectedModel"
								:disabled="pending || selectedCliId !== cli.id"
								:aria-label="`${cli.name} 模型选择`"
								@change="$emit('model', $event.target.value)"
							>
								<option v-for="model in cli.models" :key="model" :value="model">
									{{ model }}
								</option>
							</select>
							<ChevronDown :size="15" :stroke-width="2" class="chev" aria-hidden="true" />
						</div>
					</div>

					<!-- Reasoning Level Selection (if available) -->
					<div v-if="cli.reasoningLevels.length" class="control-unit reasoning-unit">
						<label class="control-unit__label" :for="`reasoning-${cli.id}`">思考等级</label>
						<div class="select-wrap select-wrap--reasoning">
							<select
								:id="`reasoning-${cli.id}`"
								:value="cli.selectedReasoningLevel"
								:disabled="pending || selectedCliId !== cli.id"
								:aria-label="`${cli.name} 推理等级选择`"
								@change="$emit('reasoning', $event.target.value)"
							>
								<option v-for="level in cli.reasoningLevels" :key="level" :value="level">
									{{ level }}
								</option>
							</select>
							<ChevronDown :size="15" :stroke-width="2" class="chev" aria-hidden="true" />
						</div>
					</div>

					<!-- Actions Cluster -->
					<div class="control-actions">
						<button
							class="pa-btn pa-btn--ghost cli-test"
							type="button"
							:disabled="cli.testing"
							@click="$emit('test', cli)"
							title="发起 CLI 连通性与指令探测"
						>
							<Zap :size="13" :stroke-width="2.2" :class="{ 'zap-active': cli.testing }" aria-hidden="true" />
							<span>{{ cli.testing ? '探测中…' : '连通测试' }}</span>
						</button>

						<button
							v-if="selectedCliId !== cli.id"
							class="pa-btn pa-btn--acid cli-select"
							type="button"
							:disabled="pending"
							@click="$emit('select', cli)"
						>
							设为默认
						</button>
					</div>
				</div>

				<!-- Telemetry Diagnostic Toast -->
				<div class="test-toast" :class="{ show: cli.showToast, err: cli.testError }">
					<span class="tt-led" aria-hidden="true"></span>
					<span class="tt-msg">{{ cli.testMessage }}</span>
				</div>
			</div>
		</div>
	</article>
</template>

<style scoped>
@import './programming-assistant.css';
</style>
