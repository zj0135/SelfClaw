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
				manualChunks: {
					// 将 Vue 核心库单独分割
					'vue-vendor': ['vue'],
					// 将终端相关库分割
					'xterm-vendor': ['@xterm/xterm', '@xterm/addon-fit'],
					// 将 Markdown 相关库分割
					'markdown-vendor': ['markdown-it', 'highlight.js', 'dompurify'],
					// 将图标库分割
					'icons-vendor': ['lucide-vue-next'],
				},
			},
		},
		chunkSizeWarningLimit: 600,
	},
});
