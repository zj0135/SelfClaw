import { fileURLToPath, URL } from 'node:url';
import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';

export default defineConfig({
	plugins: [vue()],
	base: './',
	build: {
		outDir: fileURLToPath(new URL('../SelfClaw.Desktop/Assets/TranscriptVue', import.meta.url)),
		emptyOutDir: true,
	},
});
