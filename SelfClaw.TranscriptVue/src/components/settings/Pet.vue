<script setup>
import { computed, onMounted, ref } from 'vue'

const DEFAULT_PET_ID = 'yorha-sit-2b'

const manifestModules = import.meta.glob('../../../assets/pets/*/pet.json', {
  eager: true,
  import: 'default',
})
const spriteModules = import.meta.glob('../../../assets/pets/*/spritesheet.webp', {
  eager: true,
  import: 'default',
  query: '?url',
})

const tabs = [
  { id: 'builtin', label: '内置' },
  { id: 'custom', label: '自定义' },
  { id: 'community', label: '社区' },
]
const builtinOrder = [
  'yorha-sit-2b',
  'yelling-dario',
  'tux',
  'slavik',
  'nyako-shigure',
  'dentist',
  'dario',
  'clippit',
]
const legacyPetIds = {
  'yorha-si': 'yorha-sit-2b',
  clippy: 'clippit',
}

const pets = buildBuiltinPets()
const defaultPetId = pets.some((pet) => pet.id === DEFAULT_PET_ID)
  ? DEFAULT_PET_ID
  : pets[0]?.id || DEFAULT_PET_ID

const activeTab = ref('builtin')
const petVisible = ref(false)
const selectedPet = ref(defaultPetId)
const syncPending = ref(false)
const syncError = ref('')
const pendingRequests = new Set()
let requestSeq = 0

const currentName = computed(() => {
  const pet = pets.find((item) => item.id === selectedPet.value)
  return pet ? pet.name : '-'
})

const footerStatus = computed(() => {
  if (syncError.value) return `同步失败：${syncError.value}`
  if (syncPending.value) return '正在同步桌面设置...'
  return `${pets.length} 只内置宠物 · 单击卡片切换默认`
})

function buildBuiltinPets() {
  return Object.entries(manifestModules)
    .map(([path, manifest]) => {
      const packageId = getPackageId(path)
      const id = normalizeString(manifest?.id) || packageId
      const spritePath = path.replace('/pet.json', '/spritesheet.webp')
      const grid = manifest?.grid || {}

      return {
        id,
        packageId,
        name: normalizeString(manifest?.displayName) || id,
        desc: normalizeString(manifest?.description) || '内置桌面宠物包。',
        author: normalizeString(manifest?.author),
        tags: Array.isArray(manifest?.tags) ? manifest.tags.filter(Boolean) : [],
        previewSrc: spriteModules[spritePath] || '',
        cols: Number(grid.cols) > 0 ? Number(grid.cols) : 8,
        rows: Number(grid.rows) > 0 ? Number(grid.rows) : 9,
      }
    })
    .sort((a, b) => {
      const ai = builtinOrder.indexOf(a.id)
      const bi = builtinOrder.indexOf(b.id)
      if (ai !== -1 || bi !== -1) {
        return (ai === -1 ? Number.MAX_SAFE_INTEGER : ai) -
          (bi === -1 ? Number.MAX_SAFE_INTEGER : bi)
      }

      return a.name.localeCompare(b.name)
    })
}

function normalizeString(value) {
  return typeof value === 'string' ? value.trim() : ''
}

function getPackageId(path) {
  const match = path.match(/\/assets\/pets\/([^/]+)\/pet\.json$/)
  return match?.[1] || ''
}

function normalizePetId(id) {
  const normalized = legacyPetIds[id] || id
  return pets.some((pet) => pet.id === normalized) ? normalized : defaultPetId
}

function previewStyle(pet) {
  if (!pet.previewSrc) return {}
  return {
    backgroundImage: `url("${pet.previewSrc}")`,
    backgroundSize: `${pet.cols * 100}% ${pet.rows * 100}%`,
    backgroundPosition: '0 0',
  }
}

function initials(name) {
  return String(name || '?')
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part.charAt(0).toUpperCase())
    .join('') || '?'
}

function selectTab(id) {
  activeTab.value = id
}

function onTabKey(event, index) {
  if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return
  event.preventDefault()
  const next = event.key === 'ArrowRight'
    ? (index + 1) % tabs.length
    : (index - 1 + tabs.length) % tabs.length
  selectTab(tabs[next].id)
  const buttons = document.querySelectorAll('.pet-view .tab-btn')
  buttons[next]?.focus()
}

function toggleVisible() {
  const next = !petVisible.value
  syncError.value = ''
  sendHostMessage('set-pet-visible', { enabled: next })
}

function selectPet(id) {
  const next = normalizePetId(id)
  if (selectedPet.value === next) return

  syncError.value = ''
  sendHostMessage('select-builtin-pet', { petId: next })
}

function sendHostMessage(type, payload = {}) {
  const webview = window.chrome?.webview
  if (!webview) {
    syncError.value = '需要在桌面应用中读取 desktop-settings.json'
    return null
  }

  const requestId = `pet-${Date.now()}-${++requestSeq}`
  pendingRequests.add(requestId)
  syncPending.value = true
  webview.postMessage({ type, requestId, ...payload })
  return requestId
}

function requestHostSettings() {
  sendHostMessage('get-pet-settings')
}

function applyHostSettings(payload) {
  const requestId = payload?.requestId
  if (requestId && pendingRequests.size > 0 && !pendingRequests.has(requestId)) {
    return
  }

  if (requestId) {
    pendingRequests.delete(requestId)
  }
  syncPending.value = pendingRequests.size > 0

  if (payload?.error) {
    syncError.value = payload.error
    return
  }

  syncError.value = ''
  petVisible.value = Boolean(payload?.enabled)
  selectedPet.value = normalizePetId(payload?.selectedPetId)
}

onMounted(() => {
  requestHostSettings()
})

defineExpose({
  handleMessage(payload) {
    if (payload?.type === 'pet-settings') {
      applyHostSettings(payload)
    }
  },
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
            v-for="(tab, index) in tabs"
            :key="tab.id"
            type="button"
            class="tab-btn"
            :class="{ active: activeTab === tab.id }"
            role="tab"
            :aria-selected="activeTab === tab.id ? 'true' : 'false'"
            :tabindex="activeTab === tab.id ? 0 : -1"
            @click="selectTab(tab.id)"
            @keydown="onTabKey($event, index)"
          >{{ tab.label }}</button>
        </div>

        <button
          type="button"
          class="pill-toggle"
          :disabled="syncPending"
          :aria-pressed="petVisible ? 'true' : 'false'"
          title="切换桌面宠物可见性"
          @click="toggleVisible"
        >
          <svg class="pt-ico" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12z"/><circle cx="12" cy="12" r="3"/></svg>
          <span class="pt-label">{{ petVisible ? '已显示' : '显示宠物' }}</span>
        </button>
      </div>

      <section v-show="activeTab === 'builtin'" class="tab-panel" role="tabpanel">
        <p class="tab-lead">从内置宠物包中选择默认桌面伙伴，资源来自 TranscriptVue 的 assets/pets。</p>

        <div class="pet-grid">
          <button
            v-for="pet in pets"
            :key="pet.id"
            type="button"
            class="pet-card"
            :disabled="syncPending"
            :data-selected="selectedPet === pet.id ? 'true' : 'false'"
            title="点击设为默认"
            @click="selectPet(pet.id)"
          >
            <span class="pet-avatar" aria-hidden="true">
              <span v-if="pet.previewSrc" class="pet-sprite" :style="previewStyle(pet)"></span>
              <span v-else class="pet-initials">{{ initials(pet.name) }}</span>
            </span>
            <span class="pet-body">
              <span class="pet-name-row">
                <span class="pet-name">{{ pet.name }}</span>
                <span v-if="selectedPet === pet.id" class="pet-badge">默认</span>
              </span>
              <span class="pet-desc">{{ pet.desc }}</span>
              <span class="pet-meta">
                <span>{{ pet.id }}</span>
                <span v-if="pet.author">by {{ pet.author }}</span>
              </span>
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
          <span class="pf-count" :data-error="syncError ? 'true' : 'false'">
            {{ footerStatus }}
          </span>
        </footer>
      </section>

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

      <section v-show="activeTab === 'community'" class="tab-panel" role="tabpanel">
        <p class="tab-lead">来自社区分享的宠物，稍后可以在这里浏览、试用或投稿作品。</p>

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
  --danger: #c24150;
  --font-display: 'Segoe UI Variable Display', 'Segoe UI', -apple-system, BlinkMacSystemFont, 'PingFang SC', 'Microsoft YaHei', sans-serif;
  --shadow-sm: 0 1px 2px rgba(23, 26, 31, 0.06);

  color: var(--text);
  font: 14px/1.5 inherit;
}
.pet-view * { box-sizing: border-box; }
.pet-view button { cursor: pointer; font: inherit; color: inherit; }

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

.panel-head { margin-bottom: 18px; }
.panel-title {
  margin: 0;
  font-family: var(--font-display);
  font-size: 20px;
  font-weight: 650;
  line-height: 1.3;
  letter-spacing: 0;
  color: var(--text);
}
.panel-desc {
  margin: 4px 0 0;
  color: var(--muted);
  font-size: 13px;
  line-height: 1.5;
}

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
  letter-spacing: 0;
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
.pill-toggle:disabled {
  cursor: default;
  opacity: 0.64;
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

.tab-lead {
  color: var(--muted);
  font-size: 13px;
  line-height: 1.55;
  margin-bottom: 18px;
  max-width: 68ch;
}

.pet-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(238px, 1fr));
  gap: 12px;
}
.pet-card {
  display: flex;
  align-items: flex-start;
  gap: 14px;
  min-height: 116px;
  padding: 14px;
  border: 1px solid var(--border);
  border-radius: 8px;
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
.pet-card:disabled {
  cursor: default;
  opacity: 0.68;
}
.pet-card:disabled:hover {
  border-color: var(--border);
  background: var(--panel);
  box-shadow: none;
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
  width: 58px;
  height: 58px;
  overflow: hidden;
  border-radius: 8px;
  border: 1px solid var(--border);
  background: var(--panel-muted);
  display: grid;
  place-items: center;
  color: var(--muted);
}
.pet-sprite {
  width: 100%;
  height: 100%;
  background-repeat: no-repeat;
  image-rendering: auto;
}
.pet-initials {
  color: var(--muted);
  font-size: 13px;
  font-weight: 700;
}

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
  letter-spacing: 0;
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
.pet-meta {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  color: var(--muted-soft);
  font-size: 11.5px;
  line-height: 1.4;
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
  letter-spacing: 0;
  opacity: 0;
  transform: translateX(-2px);
  transition: opacity 0.14s, transform 0.14s;
}
.pet-card:hover .pet-cta,
.pet-card:focus-visible .pet-cta {
  opacity: 1;
  transform: none;
}

.pet-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  margin-top: 20px;
  padding: 12px 16px;
  border: 1px solid var(--border);
  border-radius: 8px;
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
  letter-spacing: 0;
}
.pf-value {
  color: var(--text);
  font-weight: 600;
  letter-spacing: 0;
}
.pf-count {
  min-width: 0;
  color: var(--muted-soft);
  text-align: right;
}
.pf-count[data-error="true"] {
  color: var(--danger);
}

.empty-state {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 6px;
  padding: 60px 24px 68px;
  text-align: center;
  border: 1px dashed var(--border-strong);
  border-radius: 8px;
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
  letter-spacing: 0;
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
  letter-spacing: 0;
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

.settings-content::-webkit-scrollbar { width: 9px; }
.settings-content::-webkit-scrollbar-thumb {
  background: #d7dae1;
  border-radius: 9px;
  border: 2px solid var(--panel-soft);
}

@media (max-width: 760px) {
  .panel-inner { padding: 22px 18px 32px; }
  .tab-bar {
    align-items: stretch;
    flex-direction: column;
  }
  .tab-strip { width: max-content; max-width: 100%; }
  .pet-footer {
    align-items: flex-start;
    flex-direction: column;
  }
  .pf-count { text-align: left; }
}

@media (prefers-reduced-motion: reduce) {
  .pet-view * { transition-duration: 0.001ms !important; }
}
</style>
