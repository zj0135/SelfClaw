<script setup>
// 预览刻意不接收任何 props：它只是一块用真 token 渲染的普通 DOM，
// 设置一改，CSS 变量就变，它自然跟着变 —— 不需要把状态再传一遍。
</script>

<template>
	<div class="preview sc-rise" :style="{ '--i': 1 }">
		<div class="pv-head">
			<span class="pv-kicker">LIVE PREVIEW</span>
			<span class="pv-note">下面这块用的是真实 token，改动即时生效</span>
		</div>

		<div class="pv-body">
			<h3 class="pv-title">界面排版预览</h3>
			<p class="pv-para">
				这一段是正文字号，用于判断阅读密度是否合适。混排一段 English text 与数字 1234567890
				可以看出字族的西文与数字部分。
			</p>
			<div class="pv-meta-row">
				<span class="pv-meta">辅助说明文字</span>
				<span class="pv-mono">MONO · 09:42:17</span>
			</div>

			<!-- 外层必须是 .markdown-content：markdown.css 里的规则是
				 `.markdown-content pre`，pre 得是它的后代而不是它本身。 -->
			<div class="markdown-content pv-code">
				<pre><code><span class="hljs-comment">// 代码块预览：字族、字号与配色都独立于界面设置</span>
<span class="hljs-keyword">export function</span> <span class="hljs-title">resolveTheme</span>(mode) {
  <span class="hljs-keyword">const</span> dark = mode === <span class="hljs-string">'dark'</span>;
  <span class="hljs-keyword">return</span> { dark, level: <span class="hljs-number">2</span>, kind: <span class="hljs-type">ThemeKind</span>.Resolved };
}</code></pre>
			</div>
		</div>
	</div>
</template>

<style scoped>
@import '../../../styles/settings-console.css';

.preview {
	overflow: hidden;
	border: 1px solid var(--sc-line-2);
	border-radius: 12px;
	background: var(--sc-panel);
}

.pv-head {
	display: flex;
	align-items: center;
	gap: 10px;
	padding: 10px 14px;
	border-bottom: 1px solid var(--sc-line);
	background: var(--sc-raise);
}

.pv-kicker {
	display: inline-flex;
	align-items: center;
	gap: 7px;
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
	font-weight: 650;
	letter-spacing: 0.2em;
}

.pv-kicker::before {
	width: 12px;
	height: 1px;
	background: var(--sc-acid);
	content: '';
}

.pv-note {
	color: var(--sc-mute);
	font-size: var(--fs-115);
}

.pv-body {
	padding: 16px 18px 18px;
}

.pv-title {
	margin: 0 0 8px;
	color: var(--sc-text);
	font-family: var(--sc-display);
	font-size: var(--fs-17);
	font-weight: 640;
}

.pv-para {
	margin: 0;
	max-width: 62ch;
	color: var(--sc-text);
	font-size: var(--fs-135);
	line-height: 1.72;
}

.pv-meta-row {
	display: flex;
	align-items: center;
	gap: 12px;
	margin-top: 10px;
}

.pv-meta {
	color: var(--sc-mute);
	font-size: var(--fs-12);
}

.pv-mono {
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-105);
	letter-spacing: 0.04em;
}

/*
 * 复用 markdown-content 的 pre 规则（背景、滚动条、语法色都在那边），
 * 这样预览与真实消息里的代码块不可能走偏。此处只补一个外边距。
 */
.pv-code {
	margin-top: 14px;
}

.pv-code :deep(pre) {
	margin: 0;
}
</style>
