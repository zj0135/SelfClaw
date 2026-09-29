<script setup>
defineProps({
	kicker: { type: String, default: 'COMING SOON' },
	title: { type: String, required: true },
	sub: { type: String, default: '' },
	word: { type: String, default: '' },
	note: { type: String, default: '' },
	status: { type: String, default: 'STANDBY' },
});
</script>

<template>
	<main class="sc-root sc-stage sc-page">
		<header class="sc-page-head sc-rise" style="--i: 0">
			<span class="sc-page-ghost" aria-hidden="true">{{ word || title }}</span>
			<div>
				<span class="sc-page-kicker">{{ kicker }}</span>
				<h1 class="sc-page-title">{{ title }}</h1>
				<p class="sc-page-sub">{{ sub }}</p>
			</div>
		</header>
		<div class="sc-page-body">
			<div class="sc-empty-stage sc-rise" style="--i: 1">
				<span class="sc-empty-kicker">{{ kicker }}</span>
				<h2 class="sc-empty-word">{{ word || title }}</h2>
				<p class="sc-empty-note">{{ note }}</p>
				<span class="sc-empty-status">{{ status }}</span>
			</div>
		</div>
	</main>
</template>

<style scoped>
@import '../../styles/settings-console.css';
</style>
