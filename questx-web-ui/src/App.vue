<template>
  <div :class="['app', { offline: !isConnected }]">
    <nav class="rail" aria-label="Pages">
      <div class="rail-logo"><img src="/bird-logo.svg" alt="QuestX" /></div>
      <button
        v-for="(page, index) in PAGES"
        :key="page.id"
        :class="['rail-btn', { active: activePage === page.id }]"
        :aria-label="page.label"
        :aria-current="activePage === page.id ? 'page' : undefined"
        @click="activePage = page.id"
      >
        <component :is="page.icon" :size="17" :stroke-width="1.8" aria-hidden="true" />
        <span class="tip">{{ page.label }}<kbd>{{ index + 1 }}</kbd></span>
      </button>
    </nav>

    <div class="main">
      <header class="topbar">
        <span v-if="status" class="pill" :title="status.batteryCharging ? 'Charging' : 'Battery'">
          <BatteryCharging v-if="status.batteryCharging" :size="16" :stroke-width="1.8" aria-hidden="true" />
          <Battery v-else :size="16" :stroke-width="1.8" aria-hidden="true" />
          {{ status.batteryPercent }}%
        </span>
        <ConfirmButton class="icon-btn" confirm-text="Restart?" title="Restart QuestX" aria-label="Restart QuestX" @confirm="restartQuestX">
          <Power :size="15" :stroke-width="2" aria-hidden="true" />
        </ConfirmButton>
      </header>

      <div v-if="banners.length" class="banners">
        <div v-for="banner in banners" :key="banner.id" :class="['banner', banner.tone]" role="status">
          <span>{{ banner.text }}</span>
          <button v-if="banner.action" class="btn" @click="banner.action.run">{{ banner.action.label }}</button>
        </div>
      </div>

      <main class="pages">
        <div v-if="!configStore.config" class="empty-pane">
          <b>{{ configStore.error ? 'Could not load the configuration' : 'Connecting to QuestX' }}</b>
          {{ configStore.error ?? 'Waiting for the headset to answer.' }}
        </div>
        <!-- Pages stay mounted so they keep their state; v-show sits on a wrapper because
             a component's own root can change (StatusView swaps its placeholder). -->
        <template v-else>
          <section v-show="activePage === 'camera'"><CameraPage :active="activePage === 'camera'" @navigate="activePage = $event" /></section>
          <section v-show="activePage === '3d'"><ThreeDView :active="activePage === '3d'" /></section>
          <section v-show="activePage === 'status'"><StatusView /></section>
          <section v-show="activePage === 'field'"><FieldLayoutsPage /></section>
          <section v-show="activePage === 'logs'"><LogsView :active="activePage === 'logs'" /></section>
          <section v-show="activePage === 'settings'"><SettingsPage /></section>
        </template>
      </main>
    </div>

    <div class="toasts" aria-live="polite">
      <div v-for="t in toasts" :key="t.id" :class="['toast', t.tone]">{{ t.text }}</div>
    </div>

    <CommandPalette v-model:open="paletteOpen" :commands="commands" />
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { Activity, Battery, BatteryCharging, Box, Map as MapIcon, Power, ScrollText, Settings, Video } from '@lucide/vue'
import { useConfigStore } from './stores/config'
import { configApi } from './api/config'
import { useConnectionState } from './composables/useConnectionState'
import { refreshHeadsetStatus, useHeadsetStatus } from './composables/useHeadsetStatus'
import { refreshRunningLayout, useRunningLayout } from './composables/useRunningLayout'
import { useFieldLayouts } from './composables/useFieldLayouts'
import { restartQuestX } from './composables/useRestart'
import { errorText, toast, useToasts } from './composables/useToast'
import CameraPage from './components/CameraPage.vue'
import ThreeDView from './components/ThreeDView.vue'
import StatusView from './components/StatusView.vue'
import FieldLayoutsPage from './components/FieldLayoutsPage.vue'
import LogsView from './components/LogsView.vue'
import SettingsPage from './components/SettingsPage.vue'
import ConfirmButton from './components/ConfirmButton.vue'
import CommandPalette from './components/CommandPalette.vue'
import type { Command, PageId } from './types'

const PAGES: { id: PageId; label: string; icon: typeof Video }[] = [
  { id: 'camera', label: 'Camera & AprilTag', icon: Video },
  { id: '3d', label: '3D view', icon: Box },
  { id: 'status', label: 'Status', icon: Activity },
  { id: 'field', label: 'Field layouts', icon: MapIcon },
  { id: 'logs', label: 'Logs', icon: ScrollText },
  { id: 'settings', label: 'Settings', icon: Settings },
]

const configStore = useConfigStore()
const { status } = useHeadsetStatus()
const { runningLayoutFile } = useRunningLayout()
const { layouts } = useFieldLayouts()
const { toasts } = useToasts()
const { isConnected, isDisconnected, secondsUntilRetry, checkConnection, getReconnectDelay } = useConnectionState()

const activePage = ref<PageId>('camera')
const paletteOpen = ref(false)

// ---- Banners: persistent state that needs attention, most urgent first ----

interface Banner {
  id: string
  tone: 'red' | 'blue' | 'amber'
  text: string
  action?: { label: string; run: () => void }
}

const banners = computed<Banner[]>(() => {
  const list: Banner[] = []
  if (isDisconnected.value) {
    list.push({
      id: 'offline',
      tone: 'red',
      text: secondsUntilRetry.value > 0
        ? `Lost connection to QuestX. Retrying in ${secondsUntilRetry.value} s`
        : 'Lost connection to QuestX. Reconnecting…',
      action: { label: 'Retry now', run: manualRetry },
    })
    return list
  }
  const config = configStore.config
  if (status.value && !status.value.isTracking) {
    list.push({ id: 'tracking', tone: 'red', text: 'VIO tracking lost' })
  }
  const savedLayout = config?.aprilTagDetectorMode.fieldLayoutFile
  if (savedLayout && runningLayoutFile.value && savedLayout !== runningLayoutFile.value) {
    list.push({
      id: 'layout',
      tone: 'blue',
      text: `Field layout saved: ${layouts.value.find(l => l.fileName === savedLayout)?.displayName ?? savedLayout}. Restart to load it`,
      action: { label: 'Restart QuestX', run: restartQuestX },
    })
  }
  if (config?.debugIpOverride) {
    list.push({
      id: 'debug-ip',
      tone: 'amber',
      text: `Debug IP override active. Robot at ${config.debugIpOverride}, team number ignored`,
      action: { label: 'Clear override', run: clearDebugIp },
    })
  }
  return list
})

async function clearDebugIp() {
  if (await configStore.updateDebugIpOverride('')) toast('Debug IP override cleared')
}

// ---- Command palette and shortcuts ----

const commands = computed<Command[]>(() => {
  const config = configStore.config
  const pages: Command[] = PAGES.map((page, index) => ({
    group: 'Go to',
    label: page.label,
    shortcut: String(index + 1),
    run: () => { activePage.value = page.id },
  }))
  if (!config) return pages
  return [
    ...pages,
    {
      group: 'Actions',
      label: config.enableAprilTagDetector ? 'Turn AprilTag detector off' : 'Turn AprilTag detector on',
      run: () => configStore.updateEnableAprilTagDetector(!config.enableAprilTagDetector),
    },
    {
      group: 'Actions',
      label: config.enablePassthroughStream ? 'Turn passthrough stream off' : 'Turn passthrough stream on',
      run: () => configStore.updateEnablePassthroughStream(!config.enablePassthroughStream),
    },
    {
      group: 'Actions',
      label: config.enableHighQualityStream ? 'Turn high quality stream off' : 'Turn high quality stream on',
      run: () => configStore.updateEnableHighQualityStream(!config.enableHighQualityStream),
    },
    { group: 'Actions', label: 'Restart QuestX', run: restartQuestX },
    {
      group: 'Actions',
      label: 'Download config.db',
      run: () => configApi.downloadDatabase().catch(error => toast(errorText('Download failed', error), 'error')),
    },
  ]
})

function onKeydown(event: KeyboardEvent) {
  if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'k') {
    event.preventDefault()
    paletteOpen.value = !paletteOpen.value
    return
  }
  const target = event.target as HTMLElement
  const typing = target.isContentEditable || ['INPUT', 'SELECT', 'TEXTAREA'].includes(target.tagName)
  if (typing || paletteOpen.value || event.metaKey || event.ctrlKey || event.altKey) return
  const page = PAGES[Number(event.key) - 1]
  if (page) activePage.value = page.id
}

// ---- Polling ----

// Connection check with backoff; config refresh rides on the same loop.
let connectionTimer: ReturnType<typeof setTimeout> | undefined
let statusTimer: ReturnType<typeof setInterval> | undefined

async function scheduleNextCheck(manual = false) {
  clearTimeout(connectionTimer)
  const connected = await checkConnection(manual)
  if (connected) await configStore.loadConfig(false)
  connectionTimer = setTimeout(scheduleNextCheck, connected ? 3000 : getReconnectDelay())
}

function manualRetry() {
  scheduleNextCheck(true)
}

watch(isConnected, connected => {
  if (connected) {
    refreshHeadsetStatus()
    refreshRunningLayout()
  }
})

onMounted(() => {
  scheduleNextCheck()
  statusTimer = setInterval(() => {
    if (isConnected.value) refreshHeadsetStatus()
  }, 1000)
  window.addEventListener('keydown', onKeydown)
})

onUnmounted(() => {
  clearTimeout(connectionTimer)
  clearInterval(statusTimer)
  window.removeEventListener('keydown', onKeydown)
})
</script>

<style scoped>
.app {
  display: flex;
  min-height: 100vh;
}

.rail {
  position: sticky;
  top: 0;
  z-index: 20;
  display: flex;
  flex: 0 0 56px;
  flex-direction: column;
  align-items: center;
  width: 56px;
  height: 100vh;
  padding-bottom: 10px;
  border-right: 1px solid var(--line);
  background: var(--rail);
}

.rail-logo {
  display: flex;
  align-items: center;
  justify-content: center;
  height: 56px;
  margin-bottom: 4px;
}

.rail-logo img {
  display: block;
  width: 42px;
  padding: 6px 4px;
  border: 1px solid var(--line);
  border-radius: 9px;
  background: #15161a;
}

.rail-btn {
  position: relative;
  display: flex;
  align-items: center;
  justify-content: center;
  width: 36px;
  height: 36px;
  margin: 2px 0;
  border: 0;
  border-radius: 8px;
  background: transparent;
  color: var(--text-2);
}

.rail-btn:hover {
  background: rgba(255, 255, 255, 0.05);
  color: var(--text);
}

.rail-btn.active {
  background: rgba(255, 255, 255, 0.08);
  color: var(--blue-soft);
  box-shadow: inset 0 0 0 1px rgba(255, 255, 255, 0.06);
}

.tip {
  position: absolute;
  top: 50%;
  left: 46px;
  display: none;
  align-items: center;
  gap: 8px;
  padding: 4px 6px 4px 9px;
  transform: translateY(-50%);
  border: 1px solid var(--line-strong);
  border-radius: 7px;
  background: #1a1b1f;
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.45);
  color: var(--text);
  font-size: 12px;
  font-weight: 500;
  white-space: nowrap;
}

.rail-btn:hover .tip {
  display: inline-flex;
}

.tip kbd {
  padding: 0 5px;
  border: 1px solid var(--line-strong);
  border-radius: 4px;
  color: var(--muted);
  font-family: var(--font-sans);
  font-size: 10.5px;
  line-height: 16px;
}

.main {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-width: 0;
}

.topbar {
  position: sticky;
  top: 0;
  z-index: 10;
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 8px;
  height: 48px;
  padding: 0 28px;
  border-bottom: 1px solid var(--line);
  background: rgba(8, 9, 10, 0.8);
  backdrop-filter: blur(12px);
}

.pill {
  display: inline-flex;
  align-items: center;
  gap: 7px;
  height: 30px;
  padding: 0 10px;
  border: 1px solid var(--line);
  border-radius: 7px;
  background: rgba(255, 255, 255, 0.02);
  color: var(--text-2);
  font-size: 12.5px;
  font-weight: 500;
  font-variant-numeric: tabular-nums;
}

.icon-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 30px;
  height: 30px;
  padding: 0 7px;
  border: 1px solid var(--line);
  border-radius: 7px;
  background: transparent;
  color: var(--text-2);
  font-size: 12.5px;
  font-weight: 500;
}

.icon-btn:hover {
  border-color: var(--red-border);
  background: rgba(229, 72, 77, 0.12);
  color: var(--red-text);
}

.icon-btn.confirming {
  border-color: var(--red);
  background: var(--red);
  color: #fff;
}

.banners {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 16px 28px 0;
}

.banner {
  display: flex;
  align-items: center;
  gap: 12px;
  min-height: 40px;
  padding: 6px 8px 6px 14px;
  border: 1px solid;
  border-radius: 8px;
  font-size: 13px;
  font-weight: 500;
}

.banner::before {
  content: '';
  flex: 0 0 7px;
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: currentColor;
}

.banner .btn {
  margin-left: auto;
  border-color: rgba(255, 255, 255, 0.12);
  background: rgba(255, 255, 255, 0.06);
}

.banner.red {
  border-color: rgba(229, 72, 77, 0.28);
  background: var(--red-tint);
  color: #ff9b9e;
}

.banner.blue {
  border-color: var(--blue-border);
  background: rgba(94, 106, 210, 0.09);
  color: #c3c9ff;
}

.banner.amber {
  border-color: var(--amber-border);
  background: rgba(227, 184, 101, 0.07);
  color: #f0cf8e;
}

.pages {
  flex: 1;
  padding: 22px 28px 32px;
}

.offline .pages {
  opacity: 0.4;
  pointer-events: none;
}

.toasts {
  position: fixed;
  right: 20px;
  bottom: 20px;
  z-index: 70;
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  gap: 8px;
}

.toast {
  position: relative;
  max-width: 420px;
  padding: 9px 14px 9px 30px;
  border: 1px solid var(--line-strong);
  border-radius: 9px;
  background: #1a1b1f;
  box-shadow: 0 12px 32px rgba(0, 0, 0, 0.5);
  font-weight: 500;
}

.toast::before {
  content: '';
  position: absolute;
  top: 50%;
  left: 13px;
  width: 7px;
  height: 7px;
  margin-top: -3.5px;
  border-radius: 50%;
  background: var(--blue-soft);
}

.toast.error::before {
  background: var(--red);
}

@media (max-width: 640px) {
  .app {
    flex-direction: column;
  }

  .rail {
    flex: 0 0 52px;
    flex-direction: row;
    justify-content: space-around;
    width: 100%;
    height: 52px;
    padding: 0 6px;
    border-right: 0;
    border-bottom: 1px solid var(--line);
  }

  .rail-logo {
    height: 52px;
    margin: 0;
  }

  .tip {
    display: none !important;
  }

  .topbar {
    top: 52px;
    padding: 0 14px;
  }

  .pages,
  .banners {
    padding-right: 12px;
    padding-left: 12px;
  }
}
</style>
