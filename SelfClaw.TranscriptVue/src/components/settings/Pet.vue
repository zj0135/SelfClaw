<script setup>
import { ref, computed, onMounted } from 'vue'

/* ---- 持久化键（沿用原型行为） ---- */
const STORE_TAB = 'pet.settings.tab'
const STORE_VIS = 'pet.settings.visible'
const STORE_PET = 'pet.settings.selected'

/* ---- Tab 定义 ---- */
const tabs = [
  { id: 'builtin', label: '内置' },
  { id: 'custom', label: '自定义' },
  { id: 'community', label: '社区' },
]
const activeTab = ref('builtin')

/* ---- 显示宠物开关 ---- */
const petVisible = ref(false)

/* ---- 内置宠物数据 ---- */
const pets = [
  { id: 'yorha-si', name: 'YoRHa Si', desc: '沉稳静坐的 YoRHa 风格 chibi 程序员，专注写代码时不打扰你。', icon: 'yorha' },
  { id: 'yelling-dario', name: 'Yelling Dario', desc: '大声嘶吼的迷你 Dario Amodei，遇到棘手 bug 时会替你先喊出来。', icon: 'yelling' },
  { id: 'tux', name: 'Tux', desc: '像素风的 Linux 吉祥物，永远站在角落里看着你敲终端。', icon: 'tux' },
  { id: 'slavik', name: 'Slavik', desc: '黑袍下蹲的调皮小地精，偶尔会偷偷把你的 tab 键换个位置。', icon: 'slavik' },
  { id: 'nyako-shigure', name: 'Nyako Shigure', desc: '温暖沉稳的机械猫娘，编译等待时会哼一小段电子铃声。', icon: 'nyako' },
  { id: 'dentist', name: 'Dentist', desc: '亲切治愈的 chibi 牙医吉祥物，长时间坐姿时会提醒你起来喝水。', icon: 'dentist' },
  { id: 'dario', name: 'Dario', desc: '沮丧摸鱼的 Codex 小助手，看到重复代码会长长地叹一口气。', icon: 'dario' },
  { id: 'clippy', name: 'Clippy', desc: '经典回锅的曲别针助理，检测到你写文档时会礼貌地探出来。', icon: 'clippy' },
]
const selectedPet = ref('yorha-si')

const currentName = computed(() => {
  const p = pets.find((x) => x.id === selectedPet.value)
  return p ? p.name : '—'
})

/* ---- 交互 ---- */
function selectTab(id) {
  activeTab.value = id
  try { localStorage.setItem(STORE_TAB, id) } catch (_) {}
}

function onTabKey(e, index) {
  if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return
  e.preventDefault()
  const next = e.key === 'ArrowRight'
    ? (index + 1) % tabs.length
    : (index - 1 + tabs.length) % tabs.length
  selectTab(tabs[next].id)
  const btns = document.querySelectorAll('.pet-view .tab-btn')
  if (btns[next]) btns[next].focus()
}

function toggleVisible() {
  petVisible.value = !petVisible.value
  try { localStorage.setItem(STORE_VIS, petVisible.value ? '1' : '0') } catch (_) {}
}

function selectPet(id) {
  selectedPet.value = id
  try { localStorage.setItem(STORE_PET, id) } catch (_) {}
}

/* ---- 恢复上次状态 ---- */
onMounted(() => {
  try {
    const savedTab = localStorage.getItem(STORE_TAB)
    if (savedTab && tabs.some((t) => t.id === savedTab)) activeTab.value = savedTab
  } catch (_) {}
  try {
    petVisible.value = localStorage.getItem(STORE_VIS) === '1'
  } catch (_) {}
  try {
    const savedPet = localStorage.getItem(STORE_PET)
    if (savedPet && pets.some((p) => p.id === savedPet)) selectedPet.value = savedPet
  } catch (_) {}
})
</script>

<template>
  <main class="pet-view settings-content">
    <div class="panel-inner">

      <header class="panel-head">
        <h2 class="panel-title">宠物</h2>
        <p class="panel-desc">桌面宠物设置</p>
      </header>

      <div class="tab-bar">
        <div class="tab-strip" role="tablist" aria-label="宠物来源">
          <button
            v-for="(tab, i) in tabs"
            :key="tab.id"
            type="button"
            class="tab-btn"
            :class="{ active: activeTab === tab.id }"
            role="tab"
            :aria-selected="activeTab === tab.id ? 'true' : 'false'"
            :tabindex="activeTab === tab.id ? 0 : -1"
            @click="selectTab(tab.id)"
            @keydown="onTabKey($event, i)"
          >{{ tab.label }}</button>
        </div>

        <button
          type="button"
          class="pill-toggle"
          :aria-pressed="petVisible ? 'true' : 'false'"
          title="切换桌面宠物可见性"
          @click="toggleVisible"
        >
          <svg class="pt-ico" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12z"/><circle cx="12" cy="12" r="3"/></svg>
          <span class="pt-label">{{ petVisible ? '已显示' : '显示宠物' }}</span>
        </button>
      </div>

      <!-- 内置 -->
      <section v-show="activeTab === 'builtin'" class="tab-panel" role="tabpanel">
        <p class="tab-lead">Open Design 内置的精选宠物 — 一键领养。</p>

        <div class="pet-grid">
          <button
            v-for="pet in pets"
            :key="pet.id"
            type="button"
            class="pet-card"
            :data-selected="selectedPet === pet.id ? 'true' : 'false'"
            title="点击设为默认"
            @click="selectPet(pet.id)"
          >
            <span class="pet-avatar" aria-hidden="true">
              <!-- YoRHa Si -->
              <svg v-if="pet.icon === 'yorha'" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="M8 4h8l1.5 3v3h-11V7z"/><circle cx="10" cy="8" r="0.6" fill="currentColor" stroke="none"/><circle cx="14" cy="8" r="0.6" fill="currentColor" stroke="none"/><path d="M6.5 10h11v6a3.5 3.5 0 0 1-3.5 3.5h-4A3.5 3.5 0 0 1 6.5 16z"/><path d="M9 20l-1 2M15 20l1 2"/></svg>
              <!-- Yelling Dario -->
              <svg v-else-if="pet.icon === 'yelling'" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="9" r="4"/><path d="M8 8.5c-1-.2-2-.2-3 .2M16 8.5c1-.2 2-.2 3 .2"/><path d="M10 11a3 3 0 0 0 4 0"/><path d="M6 21c1-3 3.5-4.5 6-4.5s5 1.5 6 4.5"/></svg>
              <!-- Tux -->
              <svg v-else-if="pet.icon === 'tux'" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><ellipse cx="12" cy="13" rx="6" ry="8"/><path d="M12 5c1.5 0 2.6 1.2 2.6 2.5v.5h-5.2v-.5C9.4 6.2 10.5 5 12 5z"/><circle cx="10.5" cy="9.5" r="0.6" fill="currentColor" stroke="none"/><circle cx="13.5" cy="9.5" r="0.6" fill="currentColor" stroke="none"/><path d="M11 12l1 1 1-1"/><path d="M7 20l2-2M17 20l-2-2"/></svg>
              <!-- Slavik -->
              <svg v-else-if="pet.icon === 'slavik'" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="M6 12c0-3.3 2.7-6 6-6s6 2.7 6 6v3H6z"/><path d="M9 12v3M15 12v3"/><path d="M6 15l-2 6h16l-2-6"/><circle cx="10" cy="12" r="0.7" fill="currentColor" stroke="none"/><circle cx="14" cy="12" r="0.7" fill="currentColor" stroke="none"/><path d="M10.5 14.5c.5.5 2.5.5 3 0"/></svg>
              <!-- Nyako Shigure -->
              <svg v-else-if="pet.icon === 'nyako'" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="M6 12l-1-4 4 2h6l4-2-1 4z"/><path d="M5 12v5a4 4 0 0 0 4 4h6a4 4 0 0 0 4-4v-5"/><circle cx="10" cy="14" r="0.7" fill="currentColor" stroke="none"/><circle cx="14" cy="14" r="0.7" fill="currentColor" stroke="none"/><path d="M11 17c.5.3 1.5.3 2 0"/></svg>
              <!-- Dentist -->
              <svg v-else-if="pet.icon === 'dentist'" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="M8 4c-2 0-3 2-3 4 0 3 1 5 1 8 0 2 1 3 2 3s1.5-1 2-3 1-3 2-3 1.5 1 2 3 1 3 2 3 2-1 2-3c0-3 1-5 1-8 0-2-1-4-3-4-1.5 0-2 1-4 1s-2.5-1-4-1z"/></svg>
              <!-- Dario -->
              <svg v-else-if="pet.icon === 'dario'" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="9" r="4"/><rect x="8.5" y="7.5" width="7" height="2" rx="0.4"/><line x1="12" y1="7.5" x2="12" y2="9.5"/><path d="M6 21c1-3 3.5-4.5 6-4.5s5 1.5 6 4.5"/></svg>
              <!-- Clippy -->
              <svg v-else viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="M15 3v13a4 4 0 0 1-8 0V6a2.5 2.5 0 0 1 5 0v9a1 1 0 0 1-2 0V6.5"/></svg>
            </span>
            <span class="pet-body">
              <span class="pet-name-row">
                <span class="pet-name">{{ pet.name }}</span>
                <span v-if="selectedPet === pet.id" class="pet-badge">默认</span>
              </span>
              <span class="pet-desc">{{ pet.desc }}</span>
              <span v-if="selectedPet !== pet.id" class="pet-cta">设为默认</span>
            </span>
          </button>
        </div>

        <footer class="pet-footer">
          <span class="pf-current">
            <span class="pf-dot" aria-hidden="true"></span>
            <span class="pf-label">当前默认</span>
            <span class="pf-value">{{ currentName }}</span>
          </span>
          <span class="pf-count">
            {{ pets.length }} 只内置宠物 · 单击卡片切换默认
          </span>
        </footer>
      </section>

      <!-- 自定义 -->
      <section v-show="activeTab === 'custom'" class="tab-panel" role="tabpanel">
        <p class="tab-lead">你亲手定制的宠物会在这里出现。可以从形象、动作到出场频率完全按需调整。</p>

        <div class="empty-state">
          <svg class="es-ico" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M12 6v12M6 12h12"/><rect x="3" y="3" width="18" height="18" rx="4"/></svg>
          <h3>还没有自定义宠物</h3>
          <p>上传形象、编写行为脚本，或从内置模板派生一个属于自己的桌面伙伴。</p>
          <div class="es-actions">
            <button type="button" class="btn-primary">新建自定义宠物</button>
            <button type="button" class="btn-secondary">从内置派生</button>
          </div>
        </div>
      </section>

      <!-- 社区 -->
      <section v-show="activeTab === 'community'" class="tab-panel" role="tabpanel">
        <p class="tab-lead">来自社区分享的宠物 — 浏览、试用或投稿你自己的作品。</p>

        <div class="empty-state">
          <svg class="es-ico" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><circle cx="12" cy="12" r="9"/><path d="M3 12h18"/><path d="M12 3a13 13 0 0 1 0 18M12 3a13 13 0 0 0 0 18"/></svg>
          <h3>社区市场即将上线</h3>
          <p>正在打通同步与安全审核流程，稍后会在这里展示可安装的社区宠物。</p>
          <div class="es-actions">
            <button type="button" class="btn-secondary">了解投稿方式</button>
          </div>
        </div>
      </section>
    </div>
  </main>
</template>

<style scoped>
/* ---- Tokens (aligned with App.vue :root) ---- */
.pet-view {
  --panel: #ffffff;
  --panel-soft: #f7f8fa;
  --panel-muted: #f1f3f6;
  --border: #e5e7eb;
  --border-strong: #d8dde5;
  --text: #171a1f;
  --muted: #6b7280;
  --muted-soft: #8a929e;
  --accent: #4f73c8;
  --accent-2: #375fae;
  --accent-soft: #eef2fb;
  --success: #2f855a;
  --font-display: 'Segoe UI Variable Display', 'Segoe UI', -apple-system, BlinkMacSystemFont, 'PingFang SC', 'Microsoft YaHei', sans-serif;
  --shadow-sm: 0 1px 2px rgba(23, 26, 31, 0.06);

  color: var(--text);
  font: 14px/1.5 inherit;
}
.pet-view * { box-sizing: border-box; }
.pet-view button { cursor: pointer; font: inherit; color: inherit; }

/* Content panel */
.settings-content {
  flex: 1;
  min-width: 0;
  height: 100%;
  overflow-y: auto;
  background: #ffffff;
}
.panel-inner {
  padding: 28px 32px 40px;
  max-width: 1120px;
}

/* Header block */
.panel-head { margin-bottom: 18px; }
.panel-title {
  margin: 0;
  font-family: var(--font-display);
  font-size: 20px;
  font-weight: 650;
  line-height: 1.3;
  letter-spacing: -0.005em;
  color: var(--text);
}
.panel-desc {
  margin: 4px 0 0;
  color: var(--muted);
  font-size: 13px;
  line-height: 1.5;
}

/* Tab bar row */
.tab-bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  margin-bottom: 12px;
}
.tab-strip {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  padding: 4px;
  border: 1px solid var(--border);
  border-radius: 10px;
  background: var(--panel-soft);
}
.tab-btn {
  padding: 6px 16px;
  border: 0;
  border-radius: 7px;
  background: transparent;
  color: var(--muted);
  font-size: 13px;
  font-weight: 500;
  letter-spacing: 0.01em;
  transition: background 0.14s, color 0.14s, box-shadow 0.14s;
}
.tab-btn:hover { color: var(--text); }
.tab-btn.active {
  background: var(--panel);
  color: var(--text);
  font-weight: 600;
  box-shadow: var(--shadow-sm), 0 0 0 1px rgba(23, 26, 31, 0.04);
}
.tab-btn:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

.pill-toggle {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  padding: 7px 14px 7px 12px;
  border: 1px solid var(--border);
  border-radius: 999px;
  background: var(--panel);
  color: var(--text);
  font-size: 13px;
  font-weight: 500;
  transition: border-color 0.14s, background 0.14s, color 0.14s;
}
.pill-toggle:hover {
  border-color: var(--border-strong);
  background: var(--panel-soft);
}
.pill-toggle .pt-ico {
  width: 14px;
  height: 14px;
  color: var(--muted);
  transition: color 0.14s;
}
.pill-toggle[aria-pressed="true"] {
  border-color: color-mix(in oklab, var(--accent) 34%, var(--border));
  background: var(--accent-soft);
  color: var(--accent-2);
}
.pill-toggle[aria-pressed="true"] .pt-ico { color: var(--accent-2); }

/* Tab-level lead-in copy */
.tab-lead {
  color: var(--muted);
  font-size: 13px;
  line-height: 1.55;
  margin-bottom: 18px;
  max-width: 60ch;
}

/* Pet grid */
.pet-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
  gap: 12px;
}
.pet-card {
  display: flex;
  align-items: flex-start;
  gap: 14px;
  padding: 14px;
  border: 1px solid var(--border);
  border-radius: 12px;
  background: var(--panel);
  text-align: left;
  font-size: 13px;
  color: inherit;
  transition: border-color 0.14s, box-shadow 0.14s, background 0.14s;
}
.pet-card:hover {
  border-color: var(--border-strong);
  background: var(--panel-soft);
  box-shadow: var(--shadow-sm);
}
.pet-card:focus-visible {
  outline: none;
  border-color: color-mix(in oklab, var(--accent) 50%, var(--border));
  box-shadow: 0 0 0 3px color-mix(in oklab, var(--accent) 22%, transparent);
}
.pet-card[data-selected="true"] {
  border-color: color-mix(in oklab, var(--accent) 40%, var(--border));
  background: var(--panel);
  box-shadow: 0 0 0 1px color-mix(in oklab, var(--accent) 22%, transparent) inset;
}

.pet-avatar {
  flex: 0 0 auto;
  width: 52px;
  height: 52px;
  border-radius: 10px;
  border: 1px solid var(--border);
  background: var(--panel-muted);
  display: grid;
  place-items: center;
  color: var(--muted);
}
.pet-avatar svg { width: 26px; height: 26px; }

.pet-body {
  min-width: 0;
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 3px;
}
.pet-name-row {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}
.pet-name {
  flex: 1 1 auto;
  min-width: 0;
  color: var(--text);
  font-size: 14px;
  font-weight: 600;
  line-height: 1.35;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.pet-badge {
  flex: 0 0 auto;
  padding: 1px 8px;
  border-radius: 999px;
  background: var(--accent-soft);
  color: var(--accent-2);
  font-size: 11px;
  font-weight: 600;
  letter-spacing: 0.02em;
  line-height: 1.55;
}
.pet-desc {
  color: var(--muted);
  font-size: 12.5px;
  line-height: 1.5;
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
}
.pet-cta {
  margin-top: 4px;
  display: inline-flex;
  align-items: center;
  gap: 4px;
  min-height: 16px;
  color: var(--accent-2);
  font-size: 12px;
  font-weight: 550;
  letter-spacing: 0.01em;
  opacity: 0;
  transform: translateX(-2px);
  transition: opacity 0.14s, transform 0.14s;
}
.pet-card:hover .pet-cta,
.pet-card:focus-visible .pet-cta {
  opacity: 1;
  transform: none;
}

/* Footer strip under the 内置 grid */
.pet-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  margin-top: 20px;
  padding: 12px 16px;
  border: 1px solid var(--border);
  border-radius: 10px;
  background: var(--panel-soft);
  color: var(--muted);
  font-size: 12.5px;
  line-height: 1.5;
}
.pf-current {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
}
.pf-dot {
  flex: 0 0 auto;
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: var(--success);
  box-shadow: 0 0 0 3px color-mix(in oklab, var(--success) 18%, transparent);
}
.pf-label {
  color: var(--muted-soft);
  font-weight: 500;
  letter-spacing: 0.01em;
}
.pf-value {
  color: var(--text);
  font-weight: 600;
  letter-spacing: -0.005em;
}
.pf-count { color: var(--muted-soft); }

/* Empty state for 自定义 / 社区 tabs */
.empty-state {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 6px;
  padding: 60px 24px 68px;
  text-align: center;
  border: 1px dashed var(--border-strong);
  border-radius: 12px;
  background: var(--panel-soft);
}
.empty-state .es-ico {
  width: 32px;
  height: 32px;
  color: var(--muted-soft);
  margin-bottom: 6px;
}
.empty-state h3 {
  margin: 0;
  font-family: var(--font-display);
  font-size: 15px;
  font-weight: 600;
  color: var(--text);
  letter-spacing: -0.005em;
}
.empty-state p {
  margin: 0;
  color: var(--muted);
  font-size: 13px;
  line-height: 1.55;
  max-width: 46ch;
}
.es-actions {
  margin-top: 12px;
  display: flex;
  gap: 10px;
}
.btn-primary {
  padding: 8px 16px;
  border: 1px solid var(--accent-2);
  border-radius: 8px;
  background: var(--accent);
  color: #fff;
  font-size: 13px;
  font-weight: 550;
  letter-spacing: 0.01em;
  transition: background 0.14s;
}
.btn-primary:hover { background: var(--accent-2); }
.btn-secondary {
  padding: 8px 14px;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: var(--panel);
  color: var(--text);
  font-size: 13px;
  font-weight: 500;
  transition: border-color 0.14s, background 0.14s;
}
.btn-secondary:hover {
  border-color: var(--border-strong);
  background: var(--panel-soft);
}

/* Scrollbars — match main app */
.settings-content::-webkit-scrollbar { width: 9px; }
.settings-content::-webkit-scrollbar-thumb {
  background: #d7dae1;
  border-radius: 9px;
  border: 2px solid var(--panel-soft);
}

@media (prefers-reduced-motion: reduce) {
  .pet-view * { transition-duration: 0.001ms !important; }
}
</style>
