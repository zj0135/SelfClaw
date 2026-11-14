import { test, expect } from '@playwright/test';
import { installActivityHost } from '../fixtures/activityHost.js';

test.beforeEach(async ({ page }) => {
	await page.addInitScript(installActivityHost);
	await page.goto('/');
	await page.evaluate(() => window.activityFixture.transcript());
	await expect(page.locator('.side-dock')).toBeHidden();
	await openSubagentDock(page);
});

// 打开子代理 tab：dock 收起时先点标题栏「面板」；启动页上点「子代理」，tab 已存在（dock 只是收起过）则直接点 tab。
// 面板打开后下拉框自动选中第一个任务，详情随之出现。
async function openSubagentDock(page) {
	await page.getByRole('button', { name: 'Panel', exact: true }).click();
	await expect(page.locator('.side-dock')).toBeVisible();
	const launcherItem = page.locator('.dock-launcher-item[data-tool="subagents"]');
	if (await launcherItem.count()) await launcherItem.click();
	else await page.locator('.dock-tab[data-tool="subagents"] .dock-tab-select').click();
	await expect(page.locator('.dock-tab.active')).toHaveText(/子代理/);
	await expect(page.locator('.task-select option')).toHaveCount(3);
	await expect(page.locator('.task-detail')).toBeVisible();
}

async function closeDock(page) {
	await page.locator('.dock-close').click();
	await expect(page.locator('.side-dock')).toBeHidden();
}

async function selectTask(page, index) {
	await page.locator('.task-select').selectOption({ index });
	const taskId = await page.evaluate((position) => window.activityFixture.ids[position], index);
	await expect(page.locator('.task-detail')).toHaveAttribute('data-task-id', taskId);
}

const detailScrollTop = (page) => page.locator('.detail-scroll').evaluate((element) => Math.round(element.scrollTop));

test('streaming detail, tool completion, reload, and parent isolation', async ({ page }) => {
	const errors = [];
	page.on('pageerror', (error) => errors.push(error.message));
	await page.locator('.task-detail .thinking-summary').click();
	await expect(page.locator('.task-detail .thinking-content')).toContainText('Reasoning for');
	// 单个工具默认折叠；点开后详情可见，完成后仍保持展开（手动优先）。
	await expect(page.locator('.task-detail .tool-details')).toHaveCount(0);
	await page.locator('.task-detail .tool-summary').click();
	await expect(page.locator('.task-detail .tool-details')).toContainText('example.cs');
	await page.evaluate(() => window.activityFixture.text(' **new tokens** <img src=x onerror="window.injected=true">'));
	await expect(page.locator('.task-detail .body-segment')).toContainText('new tokens');
	await page.evaluate(() => window.activityFixture.finish());
	await expect(page.locator('.task-detail .tool-block')).toHaveClass(/completed/);
	await expect(page.locator('.task-detail .tool-details')).toContainText('Recorded tool result');
	await expect(page.locator('.task-detail')).toContainText('等待父代理处理');
	await expect(page.locator('[data-message-id="parent-message"]')).not.toContainText('new tokens');
	expect(await page.evaluate(() => window.injected)).toBeUndefined();
	await page.reload();
	await page.evaluate(() => window.activityFixture.transcript());
	await openSubagentDock(page);
	expect(errors).toEqual([]);
});

test('the dock sits flush right of the workspace and leaves the transcript reader alone', async ({ page }) => {
	await page.evaluate(() => window.activityFixture.text('\n\n' + 'Child line.\n\n'.repeat(80)));
	await expect(page.locator('.task-detail .body-segment')).toContainText('Child line.');
	await page.locator('#transcript-scroll').evaluate((element) => { element.scrollTop = 60; element.dispatchEvent(new Event('scroll')); });
	await page.locator('.detail-scroll').evaluate((element) => { element.scrollTop = 0; element.dispatchEvent(new Event('scroll')); });
	await page.evaluate(() => window.activityFixture.text('Frozen latest token'));
	await expect(page.locator('.new-content')).toBeVisible();
	await expect(page.locator('.task-detail .body-segment')).not.toContainText('Frozen latest token');
	expect(await page.locator('#transcript-scroll').evaluate((element) => element.scrollTop)).toBe(60);
	await page.locator('.new-content').click();
	await expect(page.locator('.task-detail .body-segment')).toContainText('Frozen latest token');
	expect(await page.locator('#transcript-scroll').evaluate((element) => element.scrollTop)).toBe(60);
	const metrics = await page.evaluate(() => {
		const rect = (selector) => document.querySelector(selector).getBoundingClientRect();
		const dock = rect('.side-dock');
		const workspace = rect('.workspace');
		const main = rect('.main-body');
		return {
			dock: { left: dock.left, right: dock.right, width: dock.width, top: dock.top, bottom: dock.bottom },
			workspace: { right: workspace.right },
			main: { right: main.right, top: main.top, bottom: main.bottom },
		};
	});
	expect(Math.abs(metrics.dock.right - metrics.main.right)).toBeLessThanOrEqual(1);
	expect(Math.abs(metrics.workspace.right - metrics.dock.left)).toBeLessThanOrEqual(1);
	expect(metrics.dock.width).toBeGreaterThanOrEqual(300);
	expect(Math.abs(metrics.dock.top - metrics.main.top)).toBeLessThanOrEqual(1);
	expect(Math.abs(metrics.dock.bottom - metrics.main.bottom)).toBeLessThanOrEqual(1);
	await closeDock(page);
	expect(await page.locator('#transcript-scroll').evaluate((element) => element.scrollTop)).toBe(60);
});

test('a historical block window and its reading position survive switching, closing, and remount', async ({ page }) => {
	await page.evaluate(() => window.activityFixture.longContent());
	await expect(page.locator('.task-detail .body-segment').first()).toContainText('History block 86');
	await page.getByRole('button', { name: '更早内容', exact: true }).click();
	await expect(page.locator('.task-detail .body-segment').first()).toContainText('History block 22');
	await page.locator('.detail-scroll').evaluate((element) => { element.scrollTop = 180; element.dispatchEvent(new Event('scroll')); });
	await selectTask(page, 1);
	await selectTask(page, 0);
	await expect(page.locator('.task-detail .body-segment').first()).toContainText('History block 22');
	await expect.poll(() => detailScrollTop(page)).toBe(180);
	await closeDock(page);
	await page.evaluate(() => window.activityFixture.appendBlock('Output while hidden'));
	await openSubagentDock(page);
	await expect(page.locator('.task-detail .body-segment').first()).toContainText('History block 22');
	await expect.poll(() => detailScrollTop(page)).toBe(180);
	await page.getByRole('button', { name: '系统设置', exact: true }).click();
	await page.locator('.kind-chat').filter({ hasText: 'Activity verification' }).click();
	await expect(page.locator('.task-detail .body-segment').first()).toContainText('History block 22');
	await expect.poll(() => detailScrollTop(page)).toBe(180);
});

test('returning from invalidated history resumes the live tail and continues scrolling', async ({ page }) => {
	await page.evaluate(() => window.activityFixture.longContent());
	await page.getByRole('button', { name: '更早内容', exact: true }).click();
	await expect(page.locator('.task-detail .body-segment').first()).toContainText('History block 22');
	await page.evaluate(() => window.activityFixture.appendBlock('New tail after history'));
	await expect(page.locator('.task-detail .body-segment').first()).toContainText('History block 22');
	await page.locator('.new-content').click();
	await expect(page.locator('.task-detail .body-segment').last()).toContainText('New tail after history');
	await page.evaluate(() => window.activityFixture.appendBlock('Continues following'));
	await expect(page.locator('.task-detail .body-segment').last()).toContainText('Continues following');
	await expect(page.locator('.new-content')).toHaveCount(0);
	await expect.poll(() => page.locator('.detail-scroll').evaluate((element) => element.scrollHeight - element.scrollTop - element.clientHeight)).toBeLessThan(2);
});

test('task reading positions survive switching, closing, and settings navigation', async ({ page }) => {
	await page.evaluate(() => window.activityFixture.text('\n\n' + 'First child history.\n\n'.repeat(80)));
	await expect(page.locator('.task-detail .body-segment')).toContainText('First child history.');
	await page.locator('.detail-scroll').evaluate((element) => { element.scrollTop = 180; element.dispatchEvent(new Event('scroll')); });
	await selectTask(page, 1);
	await page.evaluate(() => window.activityFixture.text('\n\n' + 'Second child history.\n\n'.repeat(80), 1));
	await expect(page.locator('.task-detail .body-segment')).toContainText('Second child history.');
	await page.locator('.detail-scroll').evaluate((element) => { element.scrollTop = 320; element.dispatchEvent(new Event('scroll')); });
	await selectTask(page, 0);
	await expect.poll(() => detailScrollTop(page)).toBe(180);
	await closeDock(page);
	await openSubagentDock(page);
	await expect.poll(() => detailScrollTop(page)).toBe(180);
	await page.getByRole('button', { name: '系统设置', exact: true }).click();
	await page.locator('.kind-chat').filter({ hasText: 'Activity verification' }).click();
	await expect.poll(() => detailScrollTop(page)).toBe(180);
	await selectTask(page, 1);
	await expect.poll(() => detailScrollTop(page)).toBe(320);
});

test('terminal tab, dark theme, larger text, and settings remount preserve usable activity', async ({ page }) => {
	await page.evaluate(() => {
		document.documentElement.dataset.theme = 'dark';
		document.documentElement.style.setProperty('--ui-font-scale', '1.25');
	});
	await page.locator('.dock-new-tab').click();
	await page.locator('.dock-launcher-item[data-tool="terminal"]').click();
	await expect(page.locator('.dock-tab.active')).toHaveText(/命令行/);
	await expect(page.locator('.terminal-pane')).toBeVisible();
	await page.evaluate(() => window.activityFixture.terminal(true));
	await expect(page.locator('.terminal-status')).toHaveClass(/running/);
	const bounds = await page.evaluate(() => {
		const terminal = document.querySelector('.terminal-pane').getBoundingClientRect();
		const dock = document.querySelector('.side-dock').getBoundingClientRect();
		return { terminalBottom: terminal.bottom, terminalHeight: terminal.height, dockBottom: dock.bottom, viewportBottom: innerHeight };
	});
	expect(bounds.terminalBottom).toBeLessThanOrEqual(bounds.viewportBottom + 1);
	expect(bounds.terminalHeight).toBeGreaterThanOrEqual(280);
	expect(Math.abs(bounds.terminalBottom - bounds.dockBottom)).toBeLessThanOrEqual(1);
	await page.locator('.dock-tab[data-tool="subagents"] .dock-tab-select').click();
	await expect(page.locator('.task-detail .body-segment')).toContainText('Partial answer');
	await page.screenshot({ path: 'test-results/activity-terminal-dark.png', fullPage: true });
	await page.getByRole('button', { name: '系统设置', exact: true }).click();
	await expect(page.locator('.side-dock')).toHaveCount(0);
	await page.locator('.kind-chat').filter({ hasText: 'Activity verification' }).click();
	await expect(page.locator('.task-detail .body-segment')).toContainText('Partial answer');
});

for (const viewport of [{ width: 1920, height: 1080 }, { width: 1280, height: 800 }, { width: 1024, height: 768 }, { width: 640, height: 768 }]) {
	test(`dock stays inside the viewport at ${viewport.width}x${viewport.height}`, async ({ page }) => {
		await page.setViewportSize(viewport);
		await expect(page.locator('.task-detail .thinking-summary')).toBeVisible();
		// 完成的思考块默认折叠；点击展开不应破坏 dock 的视口边界。
		await expect(page.locator('.task-detail .thinking-content')).toHaveCount(0);
		await page.locator('.task-detail .thinking-summary').click();
		await expect(page.locator('.task-detail .body-segment')).toContainText('Partial answer');
		const bounds = await page.evaluate(() => {
			const dock = document.querySelector('.side-dock').getBoundingClientRect();
			const detail = document.querySelector('.task-detail').getBoundingClientRect();
			return {
				dock: { left: dock.left, right: dock.right, bottom: dock.bottom },
				detail: { right: detail.right, bottom: detail.bottom },
				viewport: { width: innerWidth, height: innerHeight },
				overflow: document.documentElement.scrollWidth - innerWidth,
			};
		});
		expect(bounds.dock.left).toBeGreaterThanOrEqual(0);
		expect(bounds.dock.right).toBeLessThanOrEqual(bounds.viewport.width + 1);
		expect(bounds.dock.bottom).toBeLessThanOrEqual(bounds.viewport.height + 1);
		expect(bounds.detail.right).toBeLessThanOrEqual(bounds.dock.right + 1);
		expect(bounds.detail.bottom).toBeLessThanOrEqual(bounds.dock.bottom + 1);
		expect(bounds.overflow).toBeLessThanOrEqual(1);
		await page.screenshot({ path: `test-results/activity-${viewport.width}.png`, fullPage: true });
	});
}
