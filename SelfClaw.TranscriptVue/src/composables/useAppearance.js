import { computed, reactive, ref } from 'vue';
import { hostBridge } from './hostBridge.js';

// 外观设置的唯一持有者。模块级单例而非 per-component 状态：外壳、设置页、终端、
// 插件桥都要读同一份，任何一处改动都必须让其余几处立刻看到。
//
// 真值在宿主的 desktop-settings.json（WPF 启动时也要读它设置原生标题栏），
// localStorage 只是缓存 —— 它的唯一职责是让 index.html 的内联脚本能同步拿到主题，
// 避免深色用户冷启动时先闪一下白底。两处都写，以宿主返回的为准。
//
// 落到 <html> 上的形式有三种：
//   data-theme        —— 切换整套调色板
//   --user-*          —— 用户显式设置的颜色/字体；未设置时删除该属性，回落主题默认值
//   --*-font-scale    —— 纯数字，直接写同名 inline 覆盖 tokens.css 的默认值

const STORAGE_KEY = 'selfclaw:appearance';

export const THEME_MODES = ['light', 'dark', 'system'];

// 字号档位与倍率。给离散档位而不是任意 px 输入：后者需要用户自己判断
// 「13 和 13.5 差多少」，而档位是可预期的。
export const FONT_SCALE_STEPS = [
	{ id: 'small', label: '小', scale: 0.9 },
	{ id: 'medium', label: '中', scale: 1 },
	{ id: 'large', label: '大', scale: 1.1 },
	{ id: 'xlarge', label: '特大', scale: 1.25 },
];

// 备选字族。空 id 表示「不覆盖」，回落 tokens.css 里的默认栈。
// 列表按 Windows 常见安装情况排；选中的字族会插到默认栈最前面，所以即使没装，
// 表现也只是回落到默认栈而不是彻底失效 —— 因此不做可用性探测。
export const UI_FONT_CHOICES = [
	{ id: '', label: '系统默认' },
	{ id: 'Microsoft YaHei UI', label: '微软雅黑 UI' },
	{ id: 'Segoe UI Variable Text', label: 'Segoe UI Variable' },
	{ id: 'Inter', label: 'Inter' },
	{ id: 'Noto Sans SC', label: 'Noto Sans SC' },
	{ id: 'PingFang SC', label: '苹方 SC' },
];

export const CODE_FONT_CHOICES = [
	{ id: '', label: '系统默认' },
	{ id: 'Cascadia Code', label: 'Cascadia Code' },
	{ id: 'Cascadia Mono', label: 'Cascadia Mono' },
	{ id: 'JetBrains Mono', label: 'JetBrains Mono' },
	{ id: 'Fira Code', label: 'Fira Code' },
	{ id: 'Consolas', label: 'Consolas' },
	{ id: 'Source Code Pro', label: 'Source Code Pro' },
];

export const DEFAULTS = {
	mode: 'system',
	uiFontFamily: '',
	uiFontScale: 1,
	textColor: '',
	codeFontFamily: '',
	codeFontScale: 1,
	codeSurface: '',
	codeInk: '',
};

const state = reactive({ ...DEFAULTS });

// 任何一项变化都自增。消费方（终端、插件桥）watch 它即可重读，不必各自
// 记住「哪几项与我有关」。
const revision = ref(0);

const systemPrefersDark = ref(false);
let loaded = false;

const resolvedTheme = computed(() => {
	if (state.mode === 'light' || state.mode === 'dark') {
		return state.mode;
	}

	return systemPrefersDark.value ? 'dark' : 'light';
});

function sanitize(raw) {
	if (!raw || typeof raw !== 'object') {
		return { ...DEFAULTS };
	}

	const scaleOf = (value, fallback) => {
		const numeric = Number(value);
		// 只接受档位表里的倍率。放开成任意数值会让 0.1 或 12 这种输入直接把界面毁掉，
		// 而这个值是持久化的 —— 用户很难自己恢复。
		return FONT_SCALE_STEPS.some((step) => step.scale === numeric) ? numeric : fallback;
	};

	return {
		mode: THEME_MODES.includes(raw.mode) ? raw.mode : DEFAULTS.mode,
		uiFontFamily: typeof raw.uiFontFamily === 'string' ? raw.uiFontFamily.trim() : '',
		uiFontScale: scaleOf(raw.uiFontScale, DEFAULTS.uiFontScale),
		textColor: typeof raw.textColor === 'string' ? raw.textColor.trim() : '',
		codeFontFamily: typeof raw.codeFontFamily === 'string' ? raw.codeFontFamily.trim() : '',
		codeFontScale: scaleOf(raw.codeFontScale, DEFAULTS.codeFontScale),
		codeSurface: typeof raw.codeSurface === 'string' ? raw.codeSurface.trim() : '',
		codeInk: typeof raw.codeInk === 'string' ? raw.codeInk.trim() : '',
	};
}

// 空值 = 用户没设置，必须移除属性而不是写空串：写空串会让 var(--user-x, 默认)
// 命中「已定义但为空」，回落链断掉，颜色直接失效。
function setSlot(name, value) {
	const root = document.documentElement;
	if (value) {
		root.style.setProperty(name, value);
	} else {
		root.style.removeProperty(name);
	}
}

// 用户选的字体是「插到最前面」，不是「整栈替换」：直接替换会丢掉后面的中文与
// 等宽兜底，选了个没装的字族就会掉到浏览器默认无衬线，中文变得很难看。
function composeStack(family, fallbackToken) {
	if (!family) {
		return '';
	}

	const quoted = /[",]/.test(family) ? family : `"${family}"`;
	return `${quoted}, var(${fallbackToken})`;
}

function apply() {
	const root = document.documentElement;
	root.dataset.theme = resolvedTheme.value;
	setSlot('--user-font-ui', composeStack(state.uiFontFamily, '--font-ui-default'));
	setSlot('--user-font-display', composeStack(state.uiFontFamily, '--font-display-default'));
	setSlot('--user-text', state.textColor);
	setSlot('--user-font-code', composeStack(state.codeFontFamily, '--font-code-default'));
	setSlot('--user-code-surface', state.codeSurface);
	setSlot('--user-code-ink', state.codeInk);
	root.style.setProperty('--ui-font-scale', String(state.uiFontScale));
	root.style.setProperty('--code-font-scale', String(state.codeFontScale));
	revision.value += 1;
}

function cacheLocally() {
	try {
		localStorage.setItem(STORAGE_KEY, JSON.stringify({ ...state }));
	} catch (_) {
		// 隐私模式或配额耗尽：真值在宿主那边，丢缓存只是下次冷启动会闪一下。
	}
}

// resolvedTheme 一起送过去：「跟随系统」只有前端能解（matchMedia），而 WPF 需要
// 一个确定的明暗值来设置原生标题栏。让宿主自己再猜一次就会有两套解析逻辑。
function pushToHost() {
	if (!hostBridge.hasHost()) {
		return;
	}

	hostBridge.post({
		type: 'appearance/save',
		settings: { ...state },
		resolvedTheme: resolvedTheme.value,
	});
}

function commit({ persist = true } = {}) {
	apply();
	if (persist) {
		cacheLocally();
		pushToHost();
	}
}

function assign(patch, options) {
	Object.assign(state, sanitize({ ...state, ...patch }));
	commit(options);
}

export function initAppearance() {
	if (loaded) {
		return;
	}

	loaded = true;

	// 监听必须在首次 apply 之前装好，否则 mode='system' 时 resolvedTheme 会先算出
	// 一个错的值。
	const query = window.matchMedia?.('(prefers-color-scheme: dark)');
	if (query) {
		systemPrefersDark.value = query.matches;
		query.addEventListener('change', (event) => {
			systemPrefersDark.value = event.matches;
			if (state.mode === 'system') {
				// 系统跟随不是用户改的设置，不必回写缓存；但要告诉宿主换标题栏。
				apply();
				pushToHost();
			}
		});
	}

	try {
		const cached = localStorage.getItem(STORAGE_KEY);
		if (cached) {
			Object.assign(state, sanitize(JSON.parse(cached)));
		}
	} catch (_) {
		// 缓存损坏就当没有，等宿主返回真值。
	}

	commit({ persist: false });

	// 宿主是真值。回包与缓存不一致时以宿主为准，并把缓存对齐。
	if (hostBridge.hasHost()) {
		hostBridge
			.request('appearance/get-state')
			.then((response) => {
				if (!response?.settings) {
					return;
				}

				Object.assign(state, sanitize(response.settings));
				apply();
				cacheLocally();
			})
			.catch(() => {
				// 宿主不可用：缓存值已经生效，什么都不用做。
			});
	}
}

export function useAppearance() {
	return {
		state,
		revision,
		resolvedTheme,
		setMode: (mode) => assign({ mode }),
		setUiFontFamily: (uiFontFamily) => assign({ uiFontFamily }),
		setUiFontScale: (uiFontScale) => assign({ uiFontScale }),
		setTextColor: (textColor) => assign({ textColor }),
		setCodeFontFamily: (codeFontFamily) => assign({ codeFontFamily }),
		setCodeFontScale: (codeFontScale) => assign({ codeFontScale }),
		setCodeSurface: (codeSurface) => assign({ codeSurface }),
		setCodeInk: (codeInk) => assign({ codeInk }),
		resetTypography: () =>
			assign({
				uiFontFamily: '',
				uiFontScale: DEFAULTS.uiFontScale,
				textColor: '',
			}),
		resetCode: () =>
			assign({
				codeFontFamily: '',
				codeFontScale: DEFAULTS.codeFontScale,
				codeSurface: '',
				codeInk: '',
			}),
		resetAll: () => assign({ ...DEFAULTS }),
	};
}
