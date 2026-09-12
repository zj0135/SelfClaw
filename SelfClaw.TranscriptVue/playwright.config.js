import { defineConfig } from '@playwright/test';
export default defineConfig({
	testDir: './tests/e2e', timeout: 30000, use: { channel: 'msedge', baseURL: 'http://127.0.0.1:5181', viewport: { width: 1280, height: 800 } },
	webServer: { command: 'npm run dev -- --host 127.0.0.1 --port 5181 --strictPort', url: 'http://127.0.0.1:5181', reuseExistingServer: true },
	reporter: [['list'], ['html', { open: 'never' }]],
});
