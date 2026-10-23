<template>
  <div class="logs-view">
    <div class="tools">
      <select v-model="filterLevel" class="level" aria-label="Log level">
        <option value="all">All levels</option>
        <option value="Log">Log</option>
        <option value="Warning">Warning</option>
        <option value="Error">Error</option>
      </select>
      <label class="switch"><input v-model="autoScroll" type="checkbox" /> Auto-scroll</label>
      <span class="muted">{{ filteredLogs.length }} lines</span>
      <span class="grow" />
      <button class="btn" @click="exportLogs"><Download :size="14" :stroke-width="2" aria-hidden="true" /> Export</button>
      <ConfirmButton class="btn danger" confirm-text="Clear all logs?" @confirm="clearLogs">Clear</ConfirmButton>
    </div>

    <p v-if="error" class="error-text">{{ error }}</p>

    <div ref="logsContainer" class="panel terminal">
      <div v-if="loading" class="muted">Loading logs…</div>
      <div v-else-if="filteredLogs.length === 0" class="muted">No logs to show</div>
      <div v-for="(log, index) in filteredLogs" :key="index" :class="['line', log.Type]">
        <span class="time">{{ formatTime(log.Timestamp) }}</span>
        <span :class="['level-tag', levelClass(log.Type)]">{{ getLogPrefix(log.Type) }}</span>
        <span class="message">{{ log.Message }}</span>
        <pre v-if="log.StackTrace && log.Type !== 'Log'" class="stack">{{ log.StackTrace }}</pre>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted, nextTick, watch } from 'vue'
import { configApi } from '../api/config'
import { useVisiblePolling } from '../composables/useVisiblePolling'
import { Download } from '@lucide/vue'
import ConfirmButton from './ConfirmButton.vue'

interface LogEntry {
  Message: string
  StackTrace: string
  Type: string
  Timestamp: number
}

const logs = ref<LogEntry[]>([])
const props = defineProps<{ active: boolean }>()
const loading = ref(true)
const error = ref<string | null>(null)
const filterLevel = ref('all')
const autoScroll = ref(true)
const logsContainer = ref<HTMLElement | null>(null)

const filteredLogs = computed(() => {
  if (filterLevel.value === 'all') return logs.value
  return logs.value.filter(log => log.Type === filterLevel.value)
})

const visible = useVisiblePolling(() => props.active, 2000, async isCurrent => {
  try {
    error.value = null
    const response = await configApi.getLogs(200)
    if (!isCurrent()) return
    logs.value = response.logs
    loading.value = false
    
    if (autoScroll.value) {
      await nextTick()
      if (isCurrent()) scrollToBottom()
    }
  } catch (err) {
    if (!isCurrent()) return
    error.value = err instanceof Error ? err.message : 'Failed to load logs'
    loading.value = false
  }
}, 'fixed')

async function clearLogs() {
  
  try {
    await configApi.clearLogs()
    logs.value = []
  } catch (err) {
    error.value = err instanceof Error ? err.message : 'Failed to clear logs'
  }
}

function exportLogs() {
  // Generate log file content
  const timestamp = new Date().toISOString().replace(/[:.]/g, '-').slice(0, -5)
  const filename = `questx-logs-${timestamp}.log`
  
  let content = `QuestX Unity Logs\n`
  content += `Exported: ${new Date().toLocaleString()}\n`
  content += `Total Logs: ${logs.value.length}\n`
  content += `Filter: ${filterLevel.value}\n`
  content += `${'='.repeat(80)}\n\n`
  
  filteredLogs.value.forEach((log, index) => {
    const time = new Date(log.Timestamp).toLocaleString()
    const logType = log.Type
    content += `[${index + 1}] ${time} [${logType}]\n`
    content += `${log.Message}\n`
    
    if (log.StackTrace && logType !== 'Log') {
      content += `\nStack Trace:\n${log.StackTrace}\n`
    }
    
    content += `${'-'.repeat(80)}\n\n`
  })
  
  // Create and download file
  const blob = new Blob([content], { type: 'text/plain' })
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = filename
  link.click()
  URL.revokeObjectURL(url)
}

function scrollToBottom() {
  if (logsContainer.value) {
    logsContainer.value.scrollTop = logsContainer.value.scrollHeight
  }
}

function handleScroll() {
  if (!logsContainer.value) return
  
  // Check if user has scrolled up from the bottom
  const { scrollTop, scrollHeight, clientHeight } = logsContainer.value
  const isAtBottom = scrollHeight - scrollTop - clientHeight < 10 // 10px threshold
  
  // Only disable auto-scroll if:
  // 1. Not at bottom
  // 2. Auto-scroll is enabled
  // 3. There's actual scrollable content (prevents false triggers on empty/loading state)
  // 4. User has scrolled down at least a bit (prevents initial load triggers)
  if (!isAtBottom && autoScroll.value && scrollHeight > clientHeight && scrollTop > 50) {
    autoScroll.value = false
  }
}

function formatTime(timestamp: number): string {
  const date = new Date(timestamp)
  const hours = String(date.getHours()).padStart(2, '0')
  const minutes = String(date.getMinutes()).padStart(2, '0')
  const seconds = String(date.getSeconds()).padStart(2, '0')
  const ms = String(date.getMilliseconds()).padStart(3, '0')
  return `${hours}:${minutes}:${seconds}.${ms}`
}

function getLogPrefix(type: string): string {
  switch (type) {
    case 'Error': return 'ERROR'
    case 'Warning': return 'WARN'
    case 'Assert': return 'ASSERT'
    case 'Exception': return 'EXCEPT'
    default: return 'INFO'
  }
}

function levelClass(type: string) {
  if (type === 'Warning') return 'warn'
  return type === 'Log' ? 'info' : 'error'
}

watch(filteredLogs, async () => {
  if (visible.value && autoScroll.value) {
    await nextTick()
    if (visible.value) scrollToBottom()
  }
})

onMounted(() => {
  // Attach scroll listener to detect manual scrolling
  if (logsContainer.value) {
    logsContainer.value.addEventListener('scroll', handleScroll)
  }
})

onUnmounted(() => {
  // Remove scroll listener
  if (logsContainer.value) {
    logsContainer.value.removeEventListener('scroll', handleScroll)
  }
})
</script>

<style scoped>
.tools {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 10px;
  margin-bottom: 12px;
}

.level {
  width: 140px;
}

.grow {
  flex: 1;
}

.terminal {
  height: calc(100vh - 170px);
  min-height: 320px;
  overflow: auto;
  padding: 10px 14px;
  background: #0b0c0d;
  font-family: var(--font-mono);
  font-size: 12.5px;
  line-height: 1.6;
}

.line {
  display: grid;
  grid-template-columns: 96px 60px minmax(0, 1fr);
  gap: 10px;
}

.time {
  color: var(--muted);
}

.level-tag {
  font-weight: 500;
}

.level-tag.info {
  color: var(--blue-soft);
}

.level-tag.warn {
  color: var(--amber);
}

.level-tag.error {
  color: var(--red-text);
}

.message {
  white-space: pre-wrap;
  word-break: break-word;
}

.line.Error .message,
.line.Exception .message {
  color: #f3a3b0;
}

.stack {
  grid-column: 3;
  margin: 2px 0 6px;
  color: var(--muted);
  font-size: 11.5px;
  white-space: pre-wrap;
}
</style>
