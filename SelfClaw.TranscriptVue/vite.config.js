import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';
import { visualizer } from 'rollup-plugin-visualizer';

export default defineConfig({
	plugins: [
		vue(),
		visualizer({
			open: true,
			gzipSize: true,
			brotliSize: true,
			filename: 'stats.html',
		}),
	],
	base: './',
	build: {
		outDir: '../SelfClaw.Desktop/Assets/TranscriptVue',
		emptyOutDir: true,
		rollupOptions: {
			output: {
				manualChunks(id) {
					if (id.includes('node_modules')) {
						if (id.includes('vue')) return 'vue-vendor';
						if (id.includes('@xterm')) return 'xterm-vendor';
						if (id.includes('markdown-it') || id.includes('highlight.js') || id.includes('dompurify')) return 'markdown-vendor';
						if (id.includes('lucide-vue-next')) return 'icons-vendor';
					}
				},
			},
		},
		chunkSizeWarningLimit: 600,
	},
});
