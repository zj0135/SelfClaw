import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { expect, it } from 'vitest';
import { pluginIconMap } from '../src/renderers/pluginIcons.js';

// 图标名有两份：后端 PluginViewIcons 的白名单（安装时校验）与前端 pluginIconMap（渲染）。
// 前端未知的名字退回 puzzle，所以漂移的表现只是「图标不对」，不会立刻暴露——用这条测试钉住。
it('keeps the front-end icon map in step with the manifest whitelist', () => {
    const source = readFileSync(
        resolve(process.cwd(), '../SelfClaw.Infrastructure/Extensions/Plugins/PluginViewIcons.cs'),
        'utf8');
    const quoted = [...source.matchAll(/"([a-z0-9-]+)"/g)].map((match) => match[1]);
    const whitelist = new Set(quoted.filter((name) => name.includes('-') || pluginIconMap[name]));

    expect([...whitelist].sort()).toEqual(Object.keys(pluginIconMap).sort());
});
