<script setup>
</script>

<template>
	<div class="settings-view">
		<div class="settings-view-card">
			<h1>设置</h1>
			<p>设置页面（目前为占位视图）。</p>
		</div>
	</div>
</template>

<style scoped>
.settings-view {
	width: 100%;
	height: 100%;
	display: flex;
	align-items: center;
	justify-content: center;
	padding: 28px;
	background: #ffffff;
	overflow: auto;
}

.settings-view-card {
	width: min(100%, 560px);
	padding: 32px;
	border: 1px solid var(--border);
	border-radius: 16px;
	background: var(--panel-soft);
	text-align: center;
}

.settings-view-card h1 {
	margin: 0 0 10px;
	font-family: var(--font-display);
	font-size: 1.4rem;
	font-weight: 700;
}

.settings-view-card p {
	margin: 0;
	color: var(--muted);
	font-size: 14px;
}
</style>
