// xterm 自己解析颜色并画到 canvas，读不到 var()。这里把 tokens.css 的 --term-* 读成
// 真实色串再交给它；主题一变就得整份重读，没有办法只让 CSS 生效。
export function readTerminalTheme() {
	const styles = getComputedStyle(document.documentElement);
	const pick = (name) => styles.getPropertyValue(name).trim();

	return {
		background: pick('--term-bg'),
		foreground: pick('--term-fg'),
		cursor: pick('--term-cursor'),
		selectionBackground: pick('--term-selection'),
		black: pick('--term-black'),
		red: pick('--term-red'),
		green: pick('--term-green'),
		yellow: pick('--term-yellow'),
		blue: pick('--term-blue'),
		magenta: pick('--term-magenta'),
		cyan: pick('--term-cyan'),
		white: pick('--term-white'),
		brightBlack: pick('--term-bright-black'),
		brightRed: pick('--term-bright-red'),
		brightGreen: pick('--term-bright-green'),
		brightYellow: pick('--term-bright-yellow'),
		brightBlue: pick('--term-bright-blue'),
		brightMagenta: pick('--term-bright-magenta'),
		brightCyan: pick('--term-bright-cyan'),
		brightWhite: pick('--term-bright-white'),
	};
}

// 终端是等宽内容，跟代码块用同一组字体设置，而不是界面字体。
export function readTerminalFont() {
	const styles = getComputedStyle(document.documentElement);
	const family = styles.getPropertyValue('--font-code').trim();
	const size = Number.parseFloat(styles.getPropertyValue('--code-fs'));

	return {
		fontFamily: family || '"Cascadia Mono", "Cascadia Code", Consolas, monospace',
		fontSize: Number.isFinite(size) && size >= 6 ? size : 12,
	};
}
