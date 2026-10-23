// 主对话列几何契约：转录正文列与输入框列必须同宽同轴——左右边缘在主流窗口宽度下都重合。
// jsdom 里量不到几何，只能靠真浏览器；这里用宿主夹具喂一条助手消息后直接比对矩形。
import { test, expect } from '@playwright/test';
import { installActivityHost } from '../fixtures/activityHost.js';

const viewports = [
	{ width: 1920, height: 1080 },
	{ width: 1280, height: 800 },
	{ width: 1024, height: 768 },
	{ width: 900, height: 720 },
];

for (const viewport of viewports) {
	test(`内容列与输入框同宽同轴 @${viewport.width}x${viewport.height}`, async ({ page }) => {
		await page.setViewportSize(viewport);
		await page.addInitScript(installActivityHost);
		await page.goto('/');
		await page.evaluate(() => window.activityFixture.transcript());
		await expect(page.locator('.transcript-content')).toBeVisible();

		const box = await page.evaluate(() => {
			const rect = (selector) => {
				const element = document.querySelector(selector);
				if (!element) return null;
				const { left, right, width } = element.getBoundingClientRect();
				return { left: Math.round(left), right: Math.round(right), width: Math.round(width) };
			};
			return {
				content: rect('.transcript-content'),
				message: rect('.message-row.assistant .message-main'),
				composer: rect('.composer-shell'),
			};
		});

		expect(box.composer).not.toBeNull();
		expect(box.content).not.toBeNull();
		expect(box.message).not.toBeNull();
		// 助手消息铺满内容列：既不能比列宽窄（右侧空档），也不能溢出。
		expect(Math.abs(box.message.width - box.content.width)).toBeLessThanOrEqual(1);
		// 内容列与输入框：左右留白一致，两条边都对齐。
		expect(Math.abs(box.content.left - box.composer.left)).toBeLessThanOrEqual(1);
		expect(Math.abs(box.content.right - box.composer.right)).toBeLessThanOrEqual(1);
	});
}
