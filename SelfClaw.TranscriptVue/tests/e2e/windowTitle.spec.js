// 顶栏标题的几何契约：标题横在窗口拖动区上面，但必须完全不吃鼠标事件——否则标题栏中间
// 那一段既拖不动窗口、双击也不最大化。jsdom 里量不到命中测试，只能用真浏览器。
import { test, expect } from '@playwright/test';
import { installActivityHost } from '../fixtures/activityHost.js';

test('顶栏显示选中会话的标题', async ({ page }) => {
	await page.addInitScript(installActivityHost);
	await page.goto('/');
	await expect(page.locator('.window-title')).toHaveCount(0);

	await page.evaluate(() => window.activityFixture.transcript());
	await expect(page.locator('.window-title')).toHaveText('Activity verification');
});

test('标题不拦截标题栏的拖动与双击最大化', async ({ page }) => {
	await page.addInitScript(installActivityHost);
	await page.goto('/');
	await page.evaluate(() => window.activityFixture.transcript());
	const title = page.locator('.window-title-text');
	await expect(title).toBeVisible();

	// 命中测试而不是坐标点击：坐标点击会落到窗口拖动区上并触发一次真实的窗口拖动。
	const hit = await page.evaluate(() => {
		const element = document.querySelector('.window-title-text');
		const box = element.getBoundingClientRect();
		const top = document.elementFromPoint(Math.round(box.left + box.width / 2), Math.round(box.top + box.height / 2));
		return { inTitle: Boolean(top?.closest('.window-title')), inDrag: Boolean(top?.closest('.window-drag-region')) };
	});
	expect(hit.inTitle).toBe(false);
	expect(hit.inDrag).toBe(true);
});

test('切到独立视图后标题消失', async ({ page }) => {
	await page.addInitScript(installActivityHost);
	await page.goto('/');
	await page.evaluate(() => window.activityFixture.transcript());
	await expect(page.locator('.window-title')).toHaveCount(1);

	await page.getByRole('button', { name: '系统设置', exact: true }).click();
	await expect(page.locator('.window-title')).toHaveCount(0);
});
