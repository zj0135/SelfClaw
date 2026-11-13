// 主转录右侧「消息锚点」的几何契约：点列、上方 DockRail 与回到底部按钮共用同一竖轴。
import { test, expect } from '@playwright/test';
import { installActivityHost } from '../fixtures/activityHost.js';

const userItems = Array.from({ length: 40 }, (_, index) => ({
	id: `u${index + 1}`,
	kind: 'message',
	role: 'user',
	status: 'completed',
	timestamp: '12:00',
	segments: [{ kind: 'content', markdown: `第 ${index + 1} 轮提问` }],
}));

test('锚点与悬浮框及回到底部按钮同轴，长会话只显示有界窗口', async ({ page }) => {
	await page.setViewportSize({ width: 1280, height: 800 });
	await page.addInitScript(installActivityHost);
	await page.goto('/');
	// 宿主夹具默认空会话；第二个参数显式注入多轮用户消息。
	await page.evaluate((items) => window.activityFixture.transcript(undefined, items), userItems);
	await expect(page.locator('.user-message-nav .nav-item.active')).toBeVisible();
	// 滚离底部以唤出「定位到底部」圆形按钮。
	await page.locator('#transcript-scroll').evaluate((element) => {
		element.scrollTop = 0;
		element.dispatchEvent(new Event('scroll'));
	});
	await expect(page.locator('.nav-bottom-btn')).toBeVisible();

	const metrics = await page.evaluate(() => {
		const rect = (selector) => {
			const element = document.querySelector(selector);
			if (!element) return null;
			const { left, right, top, bottom, width, height } = element.getBoundingClientRect();
			return { left, right, top, bottom, width, height };
		};
		const active = document.querySelector('.nav-item.active');
		const bottomButton = document.querySelector('.nav-bottom-btn');
		const bottomStyle = bottomButton ? getComputedStyle(bottomButton) : null;
		return {
			rail: rect('.dock-rail'),
			nav: rect('.user-message-nav'),
			track: rect('.nav-track'),
			active: rect('.nav-item.active'),
			bottomButton: rect('.nav-bottom-btn'),
			bottomBorderRadius: bottomStyle?.borderRadius,
			bottomBackground: bottomStyle?.backgroundColor,
			bottomIconTag: bottomButton?.querySelector('svg')?.tagName.toLowerCase(),
			panel: rect('.transcript-panel'),
			visibleDots: document.querySelectorAll('.nav-item').length,
			totalRounds: document.querySelector('.nav-track')?.getAttribute('aria-label'),
			activeIndicatorText: active?.getAttribute('aria-label'),
		};
	});

	expect(metrics.rail).not.toBeNull();
	expect(metrics.nav).not.toBeNull();
	expect(metrics.track).not.toBeNull();
	expect(metrics.active).not.toBeNull();
	expect(metrics.bottomButton).not.toBeNull();
	// 圆点中心与 DockRail 按钮中心同轴；不同 DOM 容器下按绝对坐标比较。
	const railCenterX = (metrics.rail.left + metrics.rail.right) / 2;
	const activeCenterX = (metrics.active.left + metrics.active.right) / 2;
	const bottomCenterX = (metrics.bottomButton.left + metrics.bottomButton.right) / 2;
	expect(Math.abs(railCenterX - activeCenterX)).toBeLessThanOrEqual(1);
	expect(Math.abs(activeCenterX - bottomCenterX)).toBeLessThanOrEqual(1);
	// 回到底部按钮是带表面的正圆，仍用 lucide 的 SVG 箭头图标。
	expect(metrics.bottomButton.width).toBe(24);
	expect(metrics.bottomButton.height).toBe(24);
	expect(metrics.bottomBorderRadius).toBe('50%');
	expect(metrics.bottomIconTag).toBe('svg');
	expect(metrics.bottomBackground).not.toBe('rgba(0, 0, 0, 0)');
	// 轨道严格受转录区高度限制，不随 40 轮对话继续增长。
	expect(metrics.track.height).toBeLessThanOrEqual(metrics.panel.height);
	expect(metrics.track.bottom).toBeLessThanOrEqual(metrics.panel.bottom + 1);
	expect(metrics.visibleDots).toBeLessThan(userItems.length);
	expect(metrics.totalRounds).toContain('共 40 轮');
	// 当前活跃点仍在可见窗口内。
	expect(metrics.active.top).toBeGreaterThanOrEqual(metrics.track.top - 1);
	expect(metrics.active.bottom).toBeLessThanOrEqual(metrics.track.bottom + 1);
});
