<script setup>
import { RefreshCw, TriangleAlert } from 'lucide-vue-next';
import ProgrammingCliCard from './ProgrammingCliCard.vue';
import { useProgrammingAssistantPage } from '../../composables/useProgrammingAssistantPage.js';
const { cliTools, isLoading, isRescanning, scanError, selectedCliId, pending, scanStatusText,
    testCli, toggleOpen, selectCli, rescanCliTools, selectModel, selectReasoning } = useProgrammingAssistantPage();
</script>

<template>
	<section class="programming-assistant sc-root sc-stage sc-page">
		<header class="sc-page-head sc-rise" style="--i: 0">
			<span class="sc-page-ghost" aria-hidden="true">Assistant</span>
			<div>
				<span class="sc-page-kicker">LOCAL CLI RUNTIMES</span>
				<h1 class="sc-page-title">编程助手</h1>
				<p class="sc-page-sub">检测本机安装的智能体 CLI，选择默认运行时与模型。</p>
			</div>
			<button class="pa-btn scan-btn" :class="{ 'is-rescanning': isRescanning }" type="button"
				:disabled="isRescanning" @click="rescanCliTools">
				<RefreshCw :size="14" :stroke-width="2" class="scan-icon" aria-hidden="true" />
				{{ isRescanning ? '扫描中…' : '重新扫描' }}
			</button>
		</header>

		<main class="sc-page-body">
			<div class="section-bar sc-rise" style="--i: 1">
				<h3>本地 CLI <span class="count">[{{ String(cliTools.length).padStart(2, '0') }}]</span></h3>
				<span class="section-line" aria-hidden="true"></span>
			</div>

			<div class="cli-list">
				<div v-if="scanStatusText" class="scan-state sc-rise" style="--i: 2"
					:class="{ 'scan-state--error': scanError }">
					<TriangleAlert v-if="scanError" :size="15" :stroke-width="2" aria-hidden="true" />
					{{ scanStatusText }}
				</div>

                <ProgrammingCliCard v-for="(cli, index) in cliTools" :key="cli.id" :cli="cli" :index="index"
                    :selected-cli-id="selectedCliId" :pending="pending" @toggle="toggleOpen" @test="testCli"
                    @select="selectCli" @model="selectModel" @reasoning="selectReasoning" />
			</div>
		</main>
	</section>
</template>

<style scoped>
@import './programming-assistant.css';
</style>
