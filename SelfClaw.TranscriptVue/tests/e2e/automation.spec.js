import { test, expect } from '@playwright/test';
import { installAutomationHost } from '../fixtures/automationHost.js';

test.beforeEach(async ({ page }) => {
	await page.addInitScript(installAutomationHost);
	await page.goto('/');
});

test('open automation, create a recurring task, project it onto the calendar, and open a run drawer', async ({ page }) => {
	const errors = [];
	page.on('pageerror', (error) => errors.push(error.message));

	await page.locator('.sidebar .tool-btn').filter({ hasText: '自动化' }).click();
	await expect(page.locator('.automation-view')).toBeVisible();

	await page.locator('.automation-primary').click();
	await expect(page.locator('.ac-dialog')).toBeVisible();

	await page.locator('.ac-dialog input[type="text"]').first().fill('每日代码审查');
	await page.getByPlaceholder('添加备注…').fill('审查昨晚代码提交与变动');
	await page.getByRole('button', { name: '每天', exact: true }).click();
	await page.locator('.ac-dialog textarea[rows="5"]').fill('review the nightly diff');
	await page.locator('.ac-confirm input[type="checkbox"]').check();
	await expect(page.locator('.ac-preview')).toContainText('下次触发');

	await page.getByRole('button', { name: '保存任务' }).click();
	await expect(page.locator('.ac-dialog')).toHaveCount(0);

	await expect(page.locator('.automation-task').filter({ hasText: '每日代码审查' })).toBeVisible();
	const eventChip = page.locator('.ac-chip').filter({ hasText: '每日代码审查' }).first();
	await expect(eventChip).toBeVisible();

	// Hover over the event chip to inspect notes and execution time without opening the setting modal
	await eventChip.hover();
	const hoverPopover = page.locator('.ac-hover-popover');
	await expect(hoverPopover).toBeVisible();
	await expect(hoverPopover).toContainText('每日代码审查');
	await expect(hoverPopover).toContainText('执行时间');
	await expect(hoverPopover).toContainText('审查昨晚代码提交与变动');
	await expect(hoverPopover.locator('.ac-popover-footer')).toContainText('打开任务设置');

	// Clicking the popover action opens the setting dialog as expected
	await hoverPopover.locator('.ac-popover-footer').click();
	await expect(page.locator('.ac-dialog')).toBeVisible();
	await page.locator('.ac-dialog-close').click();
	await expect(page.locator('.ac-dialog')).toHaveCount(0);

	await page.getByRole('tab', { name: /运行历史/ }).click();
	await expect(page.locator('.automation-run').first()).toBeVisible();
	await page.locator('.automation-run').first().click();

	await expect(page.locator('.automation-drawer')).toBeVisible();
	await expect(page.locator('.automation-drawer')).toContainText('Seed transcript content');
	await page.waitForTimeout(300);
	await page.screenshot({ path: 'test-results/automation-drawer.png', fullPage: true });
	expect(errors).toEqual([]);
});

test('rejects a trigger without a prompt in the dialog', async ({ page }) => {
	await page.locator('.sidebar .tool-btn').filter({ hasText: '自动化' }).click();
	await page.locator('.automation-primary').click();
	await page.locator('.ac-dialog input[type="text"]').first().fill('缺少提示词');
	await page.getByRole('button', { name: '每天', exact: true }).click();

	await expect(page.getByRole('button', { name: '保存任务' })).toBeDisabled();
	await expect(page.locator('.ac-dialog-error')).toContainText('必须填写提示词');
});

test('deletes a run record and clears task history through confirmation', async ({ page }) => {
	const errors = [];
	page.on('pageerror', (error) => errors.push(error.message));

	await page.locator('.sidebar .tool-btn').filter({ hasText: '自动化' }).click();
	await page.getByRole('tab', { name: /运行历史/ }).click();
	await expect(page.locator('.automation-run').first()).toBeVisible();
	await expect(page.locator('.automation-history-size')).toContainText('历史体积');

	await page.locator('.automation-run-delete').first().click();
	await expect(page.locator('.confirm')).toBeVisible();
	await page.locator('.confirm-btn.primary').click();
	await expect(page.locator('.automation-run')).toHaveCount(0);

	await page.getByRole('tab', { name: /清单/ }).click();
	await expect(page.locator('.automation-meta-item').filter({ hasText: '历史：' })).toHaveCount(1);
	await page.locator('.automation-icon-btn[title="清空该任务历史"]').first().click();
	await expect(page.locator('.confirm')).toBeVisible();
	await page.locator('.confirm-btn.primary').click();
	await expect(page.locator('.automation-meta-item').filter({ hasText: '历史：' })).toHaveCount(0);

	expect(errors).toEqual([]);
});

test('switching to list view (日程) on a narrow window condenses title and avoids ugly word wrapping', async ({ page }) => {
	const errors = [];
	page.on('pageerror', (error) => errors.push(error.message));

	// Test narrow window matching the user's environment
	await page.setViewportSize({ width: 980, height: 720 });
	await page.locator('.sidebar .tool-btn').filter({ hasText: '自动化' }).click();
	await expect(page.locator('.automation-view')).toBeVisible();

	// Switch to "日程"
	await page.locator('.ac-view-tab').filter({ hasText: '日程' }).click();
	await expect(page.locator('.ac-view-tab.is-active')).toHaveText('日程');

	// Verify the title is cleanly parsed (not the 23-char slash date like 2026/10/12 – 2026/10/18)
	const titleKicker = page.locator('.ac-title-kicker');
	const titleMain = page.locator('.ac-title-main');
	await expect(titleKicker).not.toHaveText('CALENDAR');
	await expect(titleMain).toContainText('月');
	await expect(titleMain).not.toContainText('/');

	// Verify the list view renders
	await expect(page.locator('.ac-stage [role="list"]').first()).toBeVisible();

	// Verify buttons have whitespace nowrap and do not vertically split into single characters
	const buttons = page.locator('.automation-ghost, .ac-view-tab, .ac-today-btn');
	const count = await buttons.count();
	for (let i = 0; i < count; i++) {
		const btn = buttons.nth(i);
		const whiteSpace = await btn.evaluate((el) => window.getComputedStyle(el).whiteSpace);
		expect(whiteSpace).toBe('nowrap');
	}

	await page.screenshot({ path: 'test-results/automation-list-narrow.png', fullPage: true });
	expect(errors).toEqual([]);
});

