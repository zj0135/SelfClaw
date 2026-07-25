<script setup>
import { onMounted, ref } from 'vue'
import { Eye, EyeOff, Check, ImagePlus, Globe, Sparkles } from 'lucide-vue-next'
import { useHostBridge, isSuperseded } from '../../composables/hostBridge.js'

const { requestLatest } = useHostBridge()

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
  syncPetSettings('set-pet-visible', { enabled: !petVisible.value })
}

function selectPet(id) {
  const next = normalizePetId(id)
  if (selectedPet.value === next) return

  syncPetSettings('select-builtin-pet', { petId: next })
}

// 三种操作（读取 / 显隐 / 选宠）都以 pet-settings 回包收尾，故共用一段请求逻辑。
// 用 pet 这个固定 key 走 requestLatest：连续操作只认最新一次回包。
async function syncPetSettings(type, payload = {}) {
  syncError.value = ''
  syncPending.value = true
  try {
    const result = await requestLatest('pet', type, payload)
    if (result?.error) {
      syncError.value = result.error
    } else {
      petVisible.value = Boolean(result?.enabled)
      selectedPet.value = normalizePetId(result?.selectedPetId)
    }
  } catch (error) {
    // 被更新的请求取代：让那次请求继续持有 syncPending，这里静默退出。
    if (isSuperseded(error)) return
    syncError.value = error?.message || '与桌面应用同步失败'
  }
  syncPending.value = false
}

onMounted(() => {
  syncPetSettings('get-pet-settings')
})
</script>

<template>
  <main class="pet-view sc-root sc-stage">
    <div class="panel-inner">
      <header class="pt-hero sc-rise" style="--i: 0">
        <div>
          <div class="pt-kicker">DESKTOP COMPANION</div>
          <h1 class="pt-title">宠物</h1>
          <p class="pt-sub">驻留桌面的像素伙伴，选择一位默认出场。</p>
        </div>

        <button
          type="button"
          class="pet-toggle"
          :disabled="syncPending"
          :aria-pressed="petVisible ? 'true' : 'false'"
          title="切换桌面宠物可见性"
          @click="toggleVisible"
        >
          <Eye v-if="petVisible" :size="14" :stroke-width="2" class="pt-ico" aria-hidden="true" />
          <EyeOff v-else :size="14" :stroke-width="2" class="pt-ico" aria-hidden="true" />
          <span class="pt-label">{{ petVisible ? '已显示' : '显示宠物' }}</span>
        </button>
      </header>

      <div class="tab-bar sc-rise" style="--i: 1">
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
        <span class="tab-hint">{{ String(pets.length).padStart(2, '0') }} UNITS</span>
      </div>

      <section v-show="activeTab === 'builtin'" class="tab-panel" role="tabpanel">
        <div class="pet-grid">
          <button
            v-for="(pet, pi) in pets"
            :key="pet.id"
            type="button"
            class="pet-card sc-rise"
            :style="{ '--i': pi + 2 }"
            :disabled="syncPending"
            :data-selected="selectedPet === pet.id ? 'true' : 'false'"
            title="点击设为默认"
            @click="selectPet(pet.id)"
          >
            <span class="pet-stage" aria-hidden="true">
              <span v-if="pet.previewSrc" class="pet-sprite" :style="previewStyle(pet)"></span>
              <span v-else class="pet-initials">{{ initials(pet.name) }}</span>
            </span>
            <span class="pet-body">
              <span class="pet-name-row">
                <span class="pet-name">{{ pet.name }}</span>
                <span v-if="selectedPet === pet.id" class="pet-badge">
                  <Check :size="10" :stroke-width="3" aria-hidden="true" />
                  默认
                </span>
              </span>
              <span class="pet-desc">{{ pet.desc }}</span>
              <span class="pet-meta">
                <span>{{ pet.id }}</span>
                <span v-if="pet.author">by {{ pet.author }}</span>
              </span>
            </span>
          </button>
        </div>

      </section>

      <section v-show="activeTab === 'custom'" class="tab-panel" role="tabpanel">
        <p class="tab-lead">你亲手定制的宠物会在这里出现。可以从形象、动作到出场频率完全按需调整。</p>

        <div class="empty-state sc-rise" style="--i: 2">
          <ImagePlus :size="34" :stroke-width="1.5" class="es-ico" aria-hidden="true" />
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

        <div class="empty-state sc-rise" style="--i: 2">
          <Globe :size="34" :stroke-width="1.5" class="es-ico" aria-hidden="true" />
          <h3>社区市场即将上线</h3>
          <p>正在打通同步与安全审核流程，稍后会在这里展示可安装的社区宠物。</p>
          <div class="es-actions">
            <button type="button" class="btn-secondary">
              <Sparkles :size="13" :stroke-width="2" aria-hidden="true" />
              了解投稿方式
            </button>
          </div>
        </div>
      </section>
    </div>
  </main>
</template>

<style scoped>
@import './settings-console.css';

.pet-view {
  height: 100%;
  overflow-y: auto;
  color: var(--sc-text);
  font-family: var(--sc-sans);
  font-size: 13px;
  line-height: 1.5;
}
.pet-view * { box-sizing: border-box; }
.pet-view button { cursor: pointer; font: inherit; color: inherit; }

.pet-view::-webkit-scrollbar { width: 9px; }
.pet-view::-webkit-scrollbar-thumb {
  background: var(--sc-raise);
  background-clip: padding-box;
  border: 2px solid transparent;
  border-radius: 99px;
}

.panel-inner {
  padding: 48px 40px 72px;
  max-width: 1120px;
}

/* ── hero ─────────────────────────────────────────────────── */
.pt-hero {
  display: flex;
  align-items: flex-end;
  justify-content: space-between;
  gap: 24px;
  margin-bottom: 26px;
  padding-bottom: 24px;
  border-bottom: 1px solid var(--sc-line);
}

.pt-kicker {
  margin-bottom: 12px;
  color: var(--sc-faint);
  font-family: var(--sc-mono);
  font-size: 10px;
  font-weight: 600;
  letter-spacing: 0.24em;
}

.pt-title {
  margin: 0;
  font-family: var(--sc-display);
  font-size: 44px;
  font-weight: 660;
  letter-spacing: 0.01em;
  line-height: 1.05;
}

.pt-sub {
  margin: 10px 0 0;
  color: var(--sc-mute);
  font-size: 13px;
}

.pet-toggle {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 8px;
  min-height: 38px;
  padding: 8px 16px;
  border: 1px solid var(--sc-line-2);
  border-radius: 9px;
  background: var(--sc-panel);
  color: var(--sc-text);
  font-size: 13px;
  font-weight: 600;
  line-height: 1.2;
  transition:
    border-color 0.16s,
    background 0.16s,
    color 0.16s,
    transform 0.12s var(--sc-ease-spring);
}
.pet-toggle:hover {
  border-color: var(--sc-faint);
  background: var(--sc-hover);
  transform: translateY(-1px);
}
.pet-toggle:active:not(:disabled) {
  transform: translateY(0);
}
.pet-toggle:disabled {
  cursor: default;
  opacity: 0.55;
}
.pet-toggle .pt-ico {
  color: var(--sc-mute);
  transition: color 0.16s;
}
.pet-toggle[aria-pressed="true"] {
  border-color: color-mix(in srgb, var(--sc-acid) 45%, transparent);
  background: var(--sc-acid-soft);
  color: var(--sc-acid);
}
.pet-toggle[aria-pressed="true"] .pt-ico { color: var(--sc-acid); }

/* ── tabs ─────────────────────────────────────────────────── */
.tab-bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  margin-bottom: 22px;
}
.tab-strip {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  padding: 4px;
  border: 1px solid var(--sc-line);
  border-radius: 11px;
  background: var(--sc-panel);
}
.tab-btn {
  padding: 8px 18px;
  border: 1px solid transparent;
  border-radius: 8px;
  background: transparent;
  color: var(--sc-mute);
  font-size: 13px;
  font-weight: 540;
  transition:
    background 0.16s,
    border-color 0.16s,
    color 0.16s;
}
.tab-btn:hover { color: var(--sc-text); }
.tab-btn.active {
  border-color: var(--sc-line-2);
  background: var(--sc-raise);
  color: var(--sc-text);
  box-shadow: 0 4px 16px rgba(23, 26, 31, 0.06);
}
.tab-btn:focus-visible {
  outline: 2px solid var(--sc-acid);
  outline-offset: 2px;
}
.tab-hint {
  color: var(--sc-faint);
  font-family: var(--sc-mono);
  font-size: 10px;
  font-weight: 500;
  letter-spacing: 0.2em;
}

.tab-lead {
  max-width: 68ch;
  margin-bottom: 18px;
  color: var(--sc-mute);
  font-size: 12.5px;
  line-height: 1.6;
}

/* ── pet grid ─────────────────────────────────────────────── */
.pet-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(300px, 1fr));
  gap: 14px;
}
.pet-card {
  position: relative;
  display: flex;
  align-items: center;
  gap: 15px;
  min-height: 116px;
  padding: 15px;
  overflow: hidden;
  border: 1px solid var(--sc-line);
  border-radius: 15px;
  background: var(--sc-panel);
  text-align: left;
  font-size: 12px;
  transition:
    border-color 0.18s,
    background 0.18s,
    transform 0.18s var(--sc-ease-out),
    box-shadow 0.18s;
}
.pet-card:hover {
  border-color: var(--sc-line-2);
  background: var(--sc-panel);
  transform: translateY(-3px);
  box-shadow: 0 18px 44px rgba(23, 26, 31, 0.1);
}
.pet-card:disabled {
  cursor: default;
  opacity: 0.6;
}
.pet-card:disabled:hover {
  border-color: var(--sc-line);
  background: var(--sc-panel);
  transform: none;
  box-shadow: none;
}
.pet-card:focus-visible {
  outline: none;
  border-color: var(--sc-acid);
  box-shadow: 0 0 0 3px var(--sc-acid-soft);
}
.pet-card[data-selected="true"] {
  border-color: color-mix(in srgb, var(--sc-acid) 55%, transparent);
  background:
    radial-gradient(220px 130px at 15% 0%, rgba(59, 91, 253, 0.06), transparent 70%),
    var(--sc-panel);
}
.pet-card[data-selected="true"]::after {
  position: absolute;
  top: 10px;
  right: 10px;
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: var(--sc-acid);
  box-shadow: 0 0 10px rgba(59, 91, 253, 0.5);
  content: '';
  animation: sc-blink 2.4s ease-in-out infinite;
}

.pet-stage {
  position: relative;
  display: grid;
  place-items: center;
  width: 84px;
  height: 84px;
  flex: 0 0 auto;
  overflow: hidden;
  border: 1px solid var(--sc-line);
  border-radius: 12px;
  background:
    radial-gradient(60px 40px at 50% 78%, rgba(59, 91, 253, 0.08), transparent 70%),
    var(--sc-bg);
  color: var(--sc-mute);
}
.pet-sprite {
  width: 100%;
  height: 100%;
  background-repeat: no-repeat;
  image-rendering: auto;
  filter: drop-shadow(0 6px 14px rgba(23, 26, 31, 0.18));
  transition: transform 0.25s var(--sc-ease-spring);
}
.pet-card:hover .pet-sprite {
  transform: scale(1.07) translateY(-2px);
}
.pet-initials {
  color: var(--sc-mute);
  font-family: var(--sc-mono);
  font-size: 13px;
  font-weight: 700;
}

.pet-body {
  min-width: 0;
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 4px;
}
.pet-name-row {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
}
.pet-name {
  flex: 1 1 auto;
  min-width: 0;
  overflow: hidden;
  color: var(--sc-text);
  font-size: 14px;
  font-weight: 620;
  line-height: 1.35;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.pet-badge {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  flex: 0 0 auto;
  padding: 2px 8px;
  border: 1px solid color-mix(in srgb, var(--sc-acid) 45%, transparent);
  border-radius: 999px;
  background: var(--sc-acid-soft);
  color: var(--sc-acid);
  font-size: 10.5px;
  font-weight: 640;
  line-height: 1.55;
}
.pet-desc {
  display: -webkit-box;
  overflow: hidden;
  color: var(--sc-mute);
  font-size: 12px;
  line-height: 1.5;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
}
.pet-meta {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  color: var(--sc-faint);
  font-family: var(--sc-mono);
  font-size: 10px;
  letter-spacing: 0.03em;
  line-height: 1.4;
}

/* ── empty states ─────────────────────────────────────────── */
.empty-state {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 7px;
  padding: 68px 24px 76px;
  border: 1px dashed var(--sc-line-2);
  border-radius: 15px;
  background: var(--sc-panel);
  text-align: center;
}
.empty-state .es-ico {
  margin-bottom: 8px;
  color: var(--sc-faint);
}
.empty-state h3 {
  margin: 0;
  font-family: var(--sc-display);
  font-size: 16px;
  font-weight: 640;
  color: var(--sc-text);
  letter-spacing: 0.01em;
}
.empty-state p {
  margin: 0;
  max-width: 46ch;
  color: var(--sc-mute);
  font-size: 12.5px;
  line-height: 1.6;
}
.es-actions {
  display: flex;
  gap: 10px;
  margin-top: 14px;
}
.btn-primary {
  padding: 9px 16px;
  border: 1px solid var(--sc-acid);
  border-radius: 9px;
  background: var(--sc-acid);
  color: var(--sc-acid-ink);
  font-size: 12.5px;
  font-weight: 640;
  transition:
    transform 0.12s var(--sc-ease-spring),
    box-shadow 0.16s;
}
.btn-primary:hover {
  transform: translateY(-1px);
  box-shadow: 0 10px 26px rgba(59, 91, 253, 0.2);
}
.btn-secondary {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 9px 16px;
  border: 1px solid var(--sc-line-2);
  border-radius: 9px;
  background: var(--sc-panel);
  color: var(--sc-soft);
  font-size: 12.5px;
  font-weight: 600;
  transition:
    border-color 0.16s,
    background 0.16s,
    color 0.16s;
}
.btn-secondary:hover {
  border-color: var(--sc-faint);
  background: var(--sc-hover);
  color: var(--sc-text);
}

@media (max-width: 760px) {
  .panel-inner { padding: 32px 20px 56px; }
  .pt-hero {
    align-items: flex-start;
    flex-direction: column;
  }
  .tab-strip { width: max-content; max-width: 100%; }
}
</style>
