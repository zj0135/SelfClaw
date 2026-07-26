<script setup>
import { AlertTriangle, Check, ShieldCheck, X } from 'lucide-vue-next';

defineProps({
	open: { type: Boolean, default: false },
	plugin: { type: Object, default: null },
	pending: { type: Boolean, default: false },
});
defineEmits(['close', 'confirm']);
</script>

<template>
	<div v-if="open && plugin" class="backdrop" @mousedown.self="$emit('close')">
		<section class="dialog" role="dialog" aria-modal="true" aria-labelledby="permission-review-title">
			<header>
				<div class="mark"><ShieldCheck :size="18" aria-hidden="true" /></div>
				<div>
					<span>PERMISSION REVIEW</span>
					<h2 id="permission-review-title">确认 {{ plugin.name }} 的权限</h2>
				</div>
				<button type="button" class="icon" title="关闭" :disabled="pending" @click="$emit('close')">
					<X :size="16" aria-hidden="true" />
				</button>
			</header>

			<div class="notice">
				<AlertTriangle :size="15" aria-hidden="true" />
				<span>启用后，插件贡献的 Skill 与 MCP 将在已绑定的 Agent 中生效。</span>
			</div>

			<div class="permissions">
				<div v-for="permission in plugin.permissions" :key="permission" class="permission">
					<Check :size="14" aria-hidden="true" />
					<code>{{ permission }}</code>
					<span v-if="plugin.unacknowledgedPermissions?.includes(permission)">新增</span>
				</div>
				<p v-if="!plugin.permissions?.length">此插件未声明额外权限。</p>
			</div>

			<footer>
				<button type="button" class="secondary" :disabled="pending" @click="$emit('close')">取消</button>
				<button type="button" class="primary" :disabled="pending" @click="$emit('confirm')">
					<ShieldCheck :size="14" aria-hidden="true" />{{ pending ? '正在确认' : '确认并启用' }}
				</button>
			</footer>
		</section>
	</div>
</template>

<style scoped>
@import '../settings-console.css';
.backdrop { position: fixed; inset: 0; z-index: 55; display: grid; place-items: center; padding: 20px; background: rgba(3, 5, 8, .76); animation: sc-fade 140ms ease-out both; }
.dialog { width: min(500px, 100%); border: 1px solid var(--sc-line-2); border-radius: 7px; background: var(--sc-panel); box-shadow: 0 24px 70px rgba(0, 0, 0, .42); color: var(--sc-text); }
header { display: grid; grid-template-columns: 34px minmax(0, 1fr) 32px; align-items: center; gap: 10px; padding: 18px 20px; border-bottom: 1px solid var(--sc-line-1); }
.mark { display: grid; width: 30px; height: 30px; place-items: center; border-radius: 50%; background: rgba(183, 121, 31, .14); color: #d29a48; }
header span { color: var(--sc-faint); font-family: var(--sc-mono); font-size: 9px; }
h2 { margin: 3px 0 0; overflow-wrap: anywhere; font-size: 15px; font-weight: 650; letter-spacing: 0; }
button { display: inline-flex; align-items: center; justify-content: center; height: 34px; gap: 7px; border: 1px solid var(--sc-line-2); border-radius: 5px; background: var(--sc-panel-2); color: var(--sc-soft); }
button:disabled { cursor: wait; opacity: .55; }
.icon { width: 32px; height: 32px; padding: 0; border: 0; background: transparent; }
.notice { display: grid; grid-template-columns: 18px minmax(0, 1fr); gap: 8px; margin: 16px 20px 0; padding: 10px 11px; border-left: 2px solid #b7791f; background: rgba(183, 121, 31, .08); color: var(--sc-mute); font-size: 11px; line-height: 1.55; }
.notice svg { color: #d29a48; }
.permissions { margin: 14px 20px 18px; border-top: 1px solid var(--sc-line-1); }
.permission { display: grid; grid-template-columns: 18px minmax(0, 1fr) auto; align-items: center; min-height: 40px; gap: 8px; border-bottom: 1px solid var(--sc-line-1); color: var(--sc-soft); }
.permission svg { color: var(--sc-ok); }
.permission code { overflow-wrap: anywhere; font-family: var(--sc-mono); font-size: 11px; }
.permission span { padding: 2px 5px; border: 1px solid rgba(183, 121, 31, .35); border-radius: 4px; color: #d29a48; font-size: 9px; }
.permissions p { margin: 14px 0; color: var(--sc-faint); font-size: 11px; }
footer { display: flex; justify-content: flex-end; gap: 8px; padding: 14px 20px; border-top: 1px solid var(--sc-line-1); }
footer button { min-width: 86px; padding: 0 14px; }
.primary { border-color: var(--sc-acid); background: var(--sc-acid); color: var(--sc-acid-ink); }
</style>
