import { test, expect } from '@playwright/test';
import { installActivityHost } from '../fixtures/activityHost.js';

test.beforeEach(async ({ page }) => {
	await page.addInitScript(installActivityHost);
	await page.goto('/');
	await page.evaluate(() => window.activityFixture.transcript());
	await expect(page.locator('.task-row')).toHaveCount(3);
});

test('streaming detail, tool completion, reload, and parent isolation', async ({ page }) => {
	const errors = [];
	page.on('pageerror', (error) => errors.push(error.message));
	await page.locator('.task-select').first().click();
	await page.locator('.task-detail .thinking-summary').click();
	await expect(page.locator('.task-detail .thinking-content')).toContainText('Reasoning for');
	await page.locator('.task-detail .tool-summary').click();
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
	await expect(page.locator('.task-row')).toHaveCount(3);
	expect(errors).toEqual([]);
});

test('detail scroll and resizing do not move a parent reader or hide its final line', async ({ page }) => {
	await page.locator('.task-select').first().click();
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
	await page.locator('#transcript-scroll').evaluate((element) => { element.scrollTop = element.scrollHeight; });
	const bounds = await page.evaluate(() => ({
		lastLine: document.querySelector('[data-message-id="parent-message"]').getBoundingClientRect().bottom,
		panelTop: document.querySelector('.activity-panel').getBoundingClientRect().top,
	}));
	expect(bounds.lastLine).toBeLessThanOrEqual(bounds.panelTop);
});

test('a historical block window and its reading position survive switching and remount', async ({ page }) => {
	await page.evaluate(() => window.activityFixture.longContent());
	await page.locator('.task-select').first().click();
	await expect(page.locator('.task-detail .body-segment').first()).toContainText('History block 86');
	await page.getByRole('button', { name: '更早内容', exact: true }).click();
	await expect(page.locator('.task-detail .body-segment').first()).toContainText('History block 22');
	await page.locator('.detail-scroll').evaluate((element) => { element.scrollTop = 180; element.dispatchEvent(new Event('scroll')); });
	await page.locator('.task-select').nth(1).click();
	await page.locator('.task-select').first().click();
	await expect(page.locator('.task-detail .body-segment').first()).toContainText('History block 22');
	await expect.poll(() => page.locator('.detail-scroll').evaluate((element) => Math.round(element.scrollTop))).toBe(180);
	await page.locator('.activity-toggle').click();
	await page.evaluate(() => window.activityFixture.appendBlock('Output while hidden'));
	await page.locator('.activity-toggle').click();
	await expect(page.locator('.task-detail .body-segment').first()).toContainText('History block 22');
	await expect.poll(() => page.locator('.detail-scroll').evaluate((element) => Math.round(element.scrollTop))).toBe(180);
	await page.getByRole('button', { name: '系统设置', exact: true }).click();
	await page.locator('.kind-chat').filter({ hasText: 'Activity verification' }).click();
	await expect(page.locator('.task-detail .body-segment').first()).toContainText('History block 22');
	await expect.poll(() => page.locator('.detail-scroll').evaluate((element) => Math.round(element.scrollTop))).toBe(180);
});

test('returning from invalidated history resumes the live tail and continues scrolling', async ({ page }) => {
	await page.evaluate(() => window.activityFixture.longContent());
	await page.locator('.task-select').first().click();
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

test('task reading positions survive switching, collapse, and settings navigation', async ({ page }) => {
	await page.locator('.task-select').first().click();
	await page.evaluate(() => window.activityFixture.text('\n\n' + 'First child history.\n\n'.repeat(80)));
	await expect(page.locator('.task-detail .body-segment')).toContainText('First child history.');
	await page.locator('.detail-scroll').evaluate((element) => { element.scrollTop = 180; element.dispatchEvent(new Event('scroll')); });
	await page.locator('.task-select').nth(1).click();
	await page.evaluate(() => window.activityFixture.text('\n\n' + 'Second child history.\n\n'.repeat(80), 1));
	await expect(page.locator('.task-detail .body-segment')).toContainText('Second child history.');
	await page.locator('.detail-scroll').evaluate((element) => { element.scrollTop = 320; element.dispatchEvent(new Event('scroll')); });
	await page.locator('.task-select').first().click();
	await expect.poll(() => page.locator('.detail-scroll').evaluate((element) => Math.round(element.scrollTop))).toBe(180);
	await page.locator('.activity-toggle').click();
	await page.locator('.activity-toggle').click();
	await expect.poll(() => page.locator('.detail-scroll').evaluate((element) => Math.round(element.scrollTop))).toBe(180);
	await page.getByRole('button', { name: '系统设置', exact: true }).click();
	await page.locator('.kind-chat').filter({ hasText: 'Activity verification' }).click();
	await expect.poll(() => page.locator('.detail-scroll').evaluate((element) => Math.round(element.scrollTop))).toBe(180);
	await page.locator('.task-select').nth(1).click();
	await expect.poll(() => page.locator('.detail-scroll').evaluate((element) => Math.round(element.scrollTop))).toBe(320);
});

test('terminal, dark theme, larger text, and settings remount preserve usable activity', async ({ page }) => {
	await page.locator('.task-select').first().click();
	await page.evaluate(() => {
		document.documentElement.dataset.theme = 'dark';
		document.documentElement.style.setProperty('--ui-font-scale', '1.25');
		window.activityFixture.send({ type: 'terminal-state', isOpen: true, isRunning: false });
	});
	await expect(page.locator('.terminal-panel')).toBeVisible();
	await expect.poll(() => page.locator('.terminal-panel').evaluate((element) => element.getBoundingClientRect().height)).toBeGreaterThanOrEqual(280);
	const bounds = await page.evaluate(() => {
		const panel = document.querySelector('.activity-panel').getBoundingClientRect();
		const composer = document.querySelector('.composer-shell').getBoundingClientRect();
		const stage = document.querySelector('.activity-stage').getBoundingClientRect();
		const terminal = document.querySelector('.terminal-panel').getBoundingClientRect();
		return { top: panel.top, bottom: panel.bottom, stageTop: stage.top, composerTop: composer.top, terminalBottom: terminal.bottom, viewportBottom: innerHeight };
	});
	expect(bounds.top).toBeGreaterThanOrEqual(bounds.stageTop);
	expect(bounds.bottom).toBeLessThanOrEqual(bounds.composerTop);
	expect(bounds.terminalBottom).toBeLessThanOrEqual(bounds.viewportBottom + 1);
	await page.screenshot({ path: 'test-results/activity-terminal-dark.png', fullPage: true });
	await page.getByRole('button', { name: '系统设置', exact: true }).click();
	await expect(page.locator('.activity-panel')).toHaveCount(0);
	await page.locator('.kind-chat').filter({ hasText: 'Activity verification' }).click();
	await expect(page.locator('.task-detail .body-segment')).toContainText('Partial answer');
});

test('plugin column confines the dock and a short stage collapses without losing the selected task', async ({ page }) => {
	await page.route('https://fixture.plugin.selfclaw.local/**', (route) => route.fulfill({ contentType: 'text/html', body: '<!doctype html><title>Fixture panel</title><p>Plugin fixture</p>' }));
	await page.evaluate(() => localStorage.setItem('activity:plugin', '1'));
	await page.reload();
	await page.evaluate(() => window.activityFixture.transcript());
	await expect(page.locator('.plugin-panel-host')).toBeVisible();
	await page.locator('.task-select').first().click();
	await expect(page.locator('.task-detail .body-segment')).toContainText('Partial answer');
	const bounds = await page.evaluate(() => ({
		panelRight: document.querySelector('.activity-panel').getBoundingClientRect().right,
		pluginLeft: document.querySelector('.plugin-panel-host').getBoundingClientRect().left,
	}));
	expect(bounds.panelRight).toBeLessThanOrEqual(bounds.pluginLeft);
	await page.screenshot({ path: 'test-results/activity-plugin.png', fullPage: true });
	await page.locator('.activity-stage').evaluate((stage) => { stage.style.height = '150px'; });
	await expect(page.locator('.activity-panel')).toHaveClass(/collapsed/);
	await page.evaluate(() => window.activityFixture.text(' while short'));
	await page.locator('.activity-stage').evaluate((stage) => { stage.style.height = ''; });
	await expect(page.locator('.task-detail .body-segment')).toContainText('while short');
});

for (const viewport of [{ width: 1920, height: 1080 }, { width: 1280, height: 800 }, { width: 1024, height: 768 }, { width: 640, height: 768 }]) {
	test(`activity stays inside stage at ${viewport.width}x${viewport.height}`, async ({ page }) => {
		await page.setViewportSize(viewport);
		if (viewport.width === 640) await page.evaluate(() => { const shell = document.querySelector('.activity-stage'); shell.style.width = '360px'; shell.style.maxWidth = '100%'; });
		await page.locator('.task-select').first().click();
		await expect(page.locator('.task-detail .thinking-summary')).toBeVisible();
		await page.locator('.task-detail .thinking-summary').click();
		await expect(page.locator('.task-detail .body-segment')).toContainText('Partial answer');
		const bounds = await page.evaluate(() => {
			const stage = document.querySelector('.activity-stage').getBoundingClientRect();
			const panel = document.querySelector('.activity-panel').getBoundingClientRect();
			const composer = document.querySelector('.composer-shell').getBoundingClientRect();
			return { composerTop: composer.top, stage: { x: stage.x, y: stage.y, right: stage.right, bottom: stage.bottom }, panel: { x: panel.x, y: panel.y, right: panel.right, bottom: panel.bottom }, overflow: document.querySelector('.activity-panel').scrollWidth - panel.width };
		});
		expect(bounds.panel.x).toBeGreaterThanOrEqual(bounds.stage.x);
		expect(bounds.panel.y).toBeGreaterThanOrEqual(bounds.stage.y);
		expect(bounds.panel.right).toBeLessThanOrEqual(bounds.stage.right);
		expect(bounds.panel.bottom).toBeLessThanOrEqual(bounds.stage.bottom);
		expect(bounds.panel.bottom).toBeLessThanOrEqual(bounds.composerTop);
		expect(bounds.overflow).toBeLessThanOrEqual(1);
		await page.screenshot({ path: `test-results/activity-${viewport.width}.png`, fullPage: true });
	});
}
