<script setup>
import { ChevronDown, Check, Zap } from 'lucide-vue-next';
defineProps({ cli: Object, index: Number, selectedCliId: String, pending: Boolean });
defineEmits(['toggle', 'test', 'select', 'model', 'reasoning']);
</script>
<template>
				<article class="cli-card sc-rise" :style="{ '--i': index + 2 }"
					:class="{ 'is-open': cli.isOpen }">
					<div class="cli-row" role="button" tabindex="0" :aria-expanded="cli.isOpen ? 'true' : 'false'"
						@click="$emit('toggle', cli)" @keydown.enter.prevent="$emit('toggle', cli)"
						@keydown.space.prevent="$emit('toggle', cli)">
						<div class="cli-icon" :style="{ background: cli.iconBackground }">
							<img v-if="cli.iconSrc" class="cli-svg" :src="cli.iconSrc" alt="" aria-hidden="true" />
							<span v-else class="cli-initials" aria-hidden="true">{{ cli.iconFallback }}</span>
						</div>

						<div class="cli-body">
							<div class="cli-titleline">
								<span class="cli-name">{{ cli.name }}</span>
								<span v-if="cli.vendor" class="cli-vendor">{{ cli.vendor }}</span>
								<span v-if="selectedCliId === cli.id" class="badge badge--selected">
									<Check :size="11" :stroke-width="3" aria-hidden="true" />
									已选择
								</span>
							</div>

							<div class="cli-meta">
								<span v-if="cli.version" class="ver">{{ cli.version }}</span>
								<span class="label">MODEL</span>
								<span class="model-name">{{ cli.selectedModel }}</span>
							</div>
						</div>

						<button class="cli-expand" type="button" :aria-label="cli.isOpen ? '收起' : '展开'"
							@click.stop="$emit('toggle', cli)">
							<ChevronDown :size="17" :stroke-width="2" aria-hidden="true" />
						</button>
					</div>

					<div class="cli-config">
						<div class="cli-config__inner">
							<div class="cli-config__label">
								模型
								<span class="badge badge--live">LIVE · 来自 CLI 的实时列表</span>
							</div>

							<div class="cli-config__controls">
								<div class="select-wrap">
									<select  :value="cli.selectedModel" :disabled="pending || selectedCliId !== cli.id" :aria-label="`${cli.name} 模型选择`"
										@change="$emit('model', $event.target.value)">
										<option v-for="model in cli.models" :key="model" :value="model">{{ model }}
										</option>
									</select>
									<ChevronDown :size="15" :stroke-width="2" class="chev" aria-hidden="true" />
								</div>

								<div v-if="cli.reasoningLevels.length" class="select-field">
									<label class="select-field__label" :for="`reasoning-${cli.id}`">推理等级</label>
									<div class="select-wrap select-wrap--reasoning">
										<select :id="`reasoning-${cli.id}`" :value="cli.selectedReasoningLevel" :disabled="pending || selectedCliId !== cli.id"
											:aria-label="`${cli.name} 推理等级选择`" @change="$emit('reasoning', $event.target.value)">
											<option v-for="level in cli.reasoningLevels" :key="level" :value="level">{{
												level }}
											</option>
										</select>
										<ChevronDown :size="15" :stroke-width="2" class="chev" aria-hidden="true" />
									</div>
								</div>

								<button class="pa-btn pa-btn--ghost cli-test" type="button" :disabled="cli.testing"
									@click="$emit('test', cli)">
									<Zap :size="13" :stroke-width="2" aria-hidden="true" />
									{{ cli.testing ? '测试中…' : '测试' }}
								</button>

								<button v-if="selectedCliId !== cli.id" class="pa-btn pa-btn--acid cli-select"
									type="button" :disabled="pending" @click="$emit('select', cli)">
									设为默认
								</button>
							</div>

							<p class="cli-config__hint">
								先将此 CLI 设为默认，再调整模型或推理等级。“默认”沿用 CLI 自身配置。
							</p>

							<div class="test-toast" :class="{ show: cli.showToast, err: cli.testError }">
								<span class="tt-led" aria-hidden="true"></span>
								{{ cli.testMessage }}
							</div>
						</div>
					</div>
				</article>
</template>
<style scoped>
@import './programming-assistant.css';
</style>
