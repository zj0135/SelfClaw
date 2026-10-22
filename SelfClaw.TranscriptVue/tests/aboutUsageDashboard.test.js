import { beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';

const { bridge } = vi.hoisted(() => ({
	bridge: {
		request: vi.fn(),
		requestLatest: vi.fn(),
		post: vi.fn(),
		hasHost: () => true,
		on: () => () => {},
	},
}));

vi.mock('../src/composables/hostBridge.js', () => ({
	hostBridge: bridge,
	useHostBridge: () => bridge,
	isSuperseded: (error) => error?.name === 'SupersededError',
}));

function dayKey(offsetDays) {
	const date = new Date();
	const local = new Date(date.getFullYear(), date.getMonth(), date.getDate() - offsetDays);
	return `${local.getFullYear()}-${String(local.getMonth() + 1).padStart(2, '0')}-${String(local.getDate()).padStart(2, '0')}`;
}

function summary(totalTokens) {
	return {
		totalTokens,
		inputTokens: Math.round(totalTokens * 0.8),
		outputTokens: Math.round(totalTokens * 0.2),
		reasoningTokens: 42_000,
		cachedInputTokens: 510_000,
		cacheWriteInputTokens: 32_000,
		cacheHitRate: 0.52,
		totalTurns: 86,
		totalSessions: 14,
		activeDays: 12,
		currentStreakDays: 3,
		longestStreakDays: 7,
		longestTurnMs: 412_000,
		peakDayTokens: 86_400,
		peakDayDate: dayKey(0),
		favoriteModel: { model: 'glm-4.6', totalTokens: 620_000, share: 0.5 },
		toolCallCount: 128,
		toolErrorRate: 0.023,
		firstActivityDate: dayKey(80),
		lastActivityDate: dayKey(0),
	};
}

function overviewPayload(totalTokens = 1_240_000) {
	const days = Array.from({ length: 7 }, (_, index) => ({
		date: dayKey(6 - index),
		totalTokens: (index + 1) * 1000,
		turnCount: 1,
		toolCallCount: 2,
	}));
	const daily = days.map((day) => ({
		date: day.date,
		totalTokens: day.totalTokens,
		inputTokens: day.totalTokens - 100,
		outputTokens: 100,
		turnCount: day.turnCount,
		models: [{ model: 'glm-4.6', totalTokens: day.totalTokens }],
	}));
	return {
		type: 'usage-overview',
		requestId: 'host-1',
		generatedAt: 1_730_000_000_000,
		timeZone: 'Asia/Shanghai',
		range: '7d',
		app: { version: '1.0.0', build: '38f3535', runtime: '.NET 10.0', os: 'Microsoft Windows NT 10.0' },
		lifetime: {
			summary: summary(totalTokens),
			heatmap: { startDate: dayKey(363), endDate: dayKey(0), maxTokens: 7000, days },
		},
		rangeData: {
			daily,
			models: totalTokens > 0
				? [{ model: 'glm-4.6', totalTokens: 620_000, inputTokens: 500_000, outputTokens: 120_000, turnCount: 42, share: 0.5 }]
				: [],
			tools: totalTokens > 0
				? [
					{ toolName: 'search_text', displayName: '搜索文本', callCount: 128, errorCount: 3, errorRate: 0.023, avgDurationMs: 420 },
					{ toolName: 'read_file', displayName: 'read_file', callCount: 32, errorCount: 0, errorRate: 0, avgDurationMs: 18 },
				]
				: [],
		},
	};
}

async function mountAbout() {
	// 每个用例都从干净的模块图开始：useUsageOverview 的 30 秒缓存是模块级的。
	vi.resetModules();
	const About = (await import('../src/components/settings/About.vue')).default;
	const wrapper = mount(About);
	await flushPromises();
	return wrapper;
}

describe('About usage dashboard', () => {
	beforeEach(() => {
		vi.clearAllMocks();
		bridge.requestLatest.mockResolvedValue(overviewPayload());
	});

	it('renders the brand strip and all four dashboard blocks when there is activity', async () => {
		const wrapper = await mountAbout();

		expect(wrapper.find('.about-hero').text()).toContain('SELFCLAW');
		expect(wrapper.find('.hero-meta').text()).toContain('1.0.0');
		expect(wrapper.find('.hero-meta').text()).toContain('38f3535');

		expect(wrapper.findAll('.summary-item')).toHaveLength(5);
		expect(wrapper.findAll('.heatmap-grid .heatmap-week')).toHaveLength(52);
		expect(wrapper.find('.trend-card').exists()).toBe(true);
		expect(wrapper.find('.ranking-card').exists()).toBe(true);
		expect(wrapper.find('.tools-card').exists()).toBe(true);
		const toolRows = wrapper.findAll('.tools-table tbody tr');
		expect(toolRows).toHaveLength(2);
		expect(toolRows[0].text()).toContain('搜索文本');
		expect(toolRows[0].text()).toContain('128');
		expect(toolRows[1].text()).toContain('18ms');
		expect(wrapper.find('.empty-card').exists()).toBe(false);
		expect(wrapper.find('.error-card').exists()).toBe(false);

		wrapper.unmount();
	});

	it('fills the whole current week column when the heatmap switches to weekly mode', async () => {
		const wrapper = await mountAbout();
		await wrapper.findAll('.heatmap-modes button')[1].trigger('click');

		const weeks = wrapper.findAll('.heatmap-grid .heatmap-week');
		expect(weeks).toHaveLength(52);
		expect(weeks[weeks.length - 1].findAll('.heatmap-cell.is-future')).toHaveLength(0);

		wrapper.unmount();
	});

	it('shows the empty state instead of a row of zeros for an empty database', async () => {
		bridge.requestLatest.mockResolvedValue(overviewPayload(0));
		const wrapper = await mountAbout();

		expect(wrapper.find('.empty-card').exists()).toBe(true);
		expect(wrapper.find('.summary-card').exists()).toBe(false);
		expect(wrapper.find('.heatmap-card').exists()).toBe(false);
		expect(wrapper.find('.trend-card').exists()).toBe(false);
		expect(wrapper.find('.tools-card').exists()).toBe(false);
		expect(wrapper.text()).not.toContain('NaN');
		expect(wrapper.text()).not.toContain('undefined');

		wrapper.unmount();
	});

	it('renders the error notice with a working retry', async () => {
		bridge.requestLatest.mockRejectedValueOnce(new Error('数据库不可读'));
		const wrapper = await mountAbout();

		const notice = wrapper.find('.error-card');
		expect(notice.exists()).toBe(true);
		expect(notice.text()).toContain('数据库不可读');

		await notice.find('.error-retry').trigger('click');
		await flushPromises();

		expect(bridge.requestLatest).toHaveBeenCalledTimes(2);
		expect(wrapper.find('.error-card').exists()).toBe(false);
		expect(wrapper.findAll('.summary-item')).toHaveLength(5);

		wrapper.unmount();
	});

	it('swallows a superseded response instead of reporting it as an error', async () => {
		const superseded = Object.assign(new Error('请求已被更新的请求取代。'), { name: 'SupersededError' });
		bridge.requestLatest.mockRejectedValueOnce(superseded);
		const wrapper = await mountAbout();

		expect(wrapper.find('.error-card').exists()).toBe(false);

		wrapper.unmount();
	});

	it('requests the new range when the tabs switch and keeps the latest answer', async () => {
		const wrapper = await mountAbout();
		bridge.requestLatest.mockResolvedValueOnce({ ...overviewPayload(2_000_000), range: '30d' });

		const tabs = wrapper.findAll('.range-tabs button');
		expect(tabs).toHaveLength(2);
		await tabs[1].trigger('click');
		await flushPromises();

		expect(bridge.requestLatest).toHaveBeenLastCalledWith(
			'usage-overview',
			'usage/get-overview',
			expect.objectContaining({ range: '30d' }),
		);
		expect(tabs[1].classes()).toContain('is-active');
		expect(wrapper.find('.summary-card').text()).toContain('2M');

		wrapper.unmount();
	});

	it('serves a second open inside the cache window from the module cache', async () => {
		vi.resetModules();
		const { useUsageOverview } = await import('../src/composables/useUsageOverview.js');

		const first = useUsageOverview();
		await flushPromises();
		expect(bridge.requestLatest).toHaveBeenCalledTimes(1);
		expect(first.overview.value).not.toBeNull();

		const second = useUsageOverview();
		await flushPromises();
		expect(bridge.requestLatest).toHaveBeenCalledTimes(1);
		expect(second.overview.value).not.toBeNull();
		expect(bridge.requestLatest).toHaveBeenCalledWith(
			'usage-overview',
			'usage/get-overview',
			expect.objectContaining({ range: '7d', timeZone: expect.any(String) }),
		);
	});

	it('ignores a slow response for a range the user already left', async () => {
		vi.resetModules();
		const { useUsageOverview } = await import('../src/composables/useUsageOverview.js');
		const usage = useUsageOverview();
		await flushPromises();
		expect(usage.overview.value.range).toBe('7d');

		let resolveSlow;
		bridge.requestLatest.mockImplementationOnce(
			() => new Promise((resolve) => { resolveSlow = resolve; }),
		);
		usage.range.value = '30d';
		await flushPromises();
		expect(usage.loading.value).toBe(true);

		// 切回缓存里的 7d：慢的 30d 请求必须作废，不能盖掉当前选择。
		usage.range.value = '7d';
		await flushPromises();
		expect(usage.loading.value).toBe(false);

		resolveSlow({ ...overviewPayload(9_000_000), range: '30d' });
		await flushPromises();

		expect(usage.range.value).toBe('7d');
		expect(usage.overview.value.range).toBe('7d');
		expect(usage.error.value).toBe('');
	});
});
