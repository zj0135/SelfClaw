// 侧栏会话项契约：时间不再以 .ntime 占据行内宽度（标题因此可以更长），
// 改为节点按钮的 title 悬浮提示（标题 + 时间）。
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

const sidebar = readFileSync(
	resolve(process.cwd(), 'src/components/SideBar/AppSidebar.vue'),
	'utf8',
);

describe('sidebar conversation time display', () => {
	it('不再渲染内联 .ntime 时间元素', () => {
		expect(sidebar).not.toContain('class="ntime"');
		expect(sidebar).not.toMatch(/\.ntime\s*\{/);
	});

	it('项目会话与普通会话节点都通过 title 悬浮展示时间', () => {
		const tips = sidebar.match(/:title="\w+\.time \? `\$\{\w+\.label\}\\n\$\{\w+\.time\}` : \w+\.label"/g) ?? [];
		// 一处是项目下的会话（session），一处是普通对话（child）。
		expect(tips.length).toBe(2);
	});
});
