<template>
  <div id="app">
    <!-- Disconnected Overlay -->
    <transition name="fade">
      <div v-if="isDisconnected" class="disconnect-overlay">
        <div class="disconnect-card card">
          <RadioOff class="disconnect-icon" :size="42" :stroke-width="1.5" aria-hidden="true" />
          <h2>Connection Lost</h2>
          <p>Lost connection to QuestX</p>
          <div class="reconnect-info">
            <div v-if="secondsUntilRetry > 0" class="retry-countdown">
              <span class="spinner-small"></span>
              Retrying in {{ secondsUntilRetry }} second{{ secondsUntilRetry !== 1 ? 's' : '' }}...
            </div>
            <div v-else class="retry-countdown">
              <span class="spinner-small"></span>
              Reconnecting...
            </div>
          </div>
          <button @click="manualRetry" class="retry-button">
            Retry Now
          </button>
        </div>
      </div>
    </transition>

    <!-- Main Application -->
    <div class="app-container">
      <!-- Header -->
      <header class="app-header">
        <div class="header-content">
          <div class="header-left">
            <div class="logo-container">
              <img src="/bird-logo.svg" alt="Bird logo" class="logo" />
            </div>
            <div class="header-info">
              <span v-if="configStore.lastUpdated" class="last-updated">
                <Clock3 class="info-icon" :size="14" :stroke-width="1.75" aria-hidden="true" />
                Updated {{ formatTime(configStore.lastUpdated) }}
              </span>
              <span :class="['connection-status', connectionStatus]">
                <span class="status-dot"></span>
                {{ connectionStatusText }}
              </span>
            </div>
          </div>
          
          <div class="header-right">
            <button class="secondary icon-button" @click="refreshData" title="Refresh">
              <RefreshCw class="button-icon" :size="16" :stroke-width="1.75" aria-hidden="true" />
              <span class="button-text">Refresh</span>
            </button>
            <button class="secondary icon-button" @click="showInfo" title="Server Info">
              <Info class="button-icon" :size="16" :stroke-width="1.75" aria-hidden="true" />
              <span class="button-text">Info</span>
            </button>
            <button class="danger icon-button" @click="handleRestart" title="Restart App">
              <Power class="button-icon" :size="16" :stroke-width="1.75" aria-hidden="true" />
              <span class="button-text">Restart App</span>
            </button>
            <button class="icon-button theme-toggle" @click="toggleTheme" :title="isDarkMode ? 'Switch to Light Mode' : 'Switch to Dark Mode'">
              <Sun v-if="!isDarkMode" class="theme-icon" :size="19" :stroke-width="1.75" aria-hidden="true" />
              <Moon v-else class="theme-icon" :size="19" :stroke-width="1.75" aria-hidden="true" />
            </button>
          </div>
        </div>
      </header>

      <!-- Content -->
      <main class="app-content">
        <ConfigForm />
      </main>

    </div>

    <!-- Info Modal -->
    <div v-if="showInfoModal" class="modal-overlay" @click="showInfoModal = false">
      <div class="modal-content card" @click.stop>
        <h2>Server Information</h2>
        
        <div v-if="serverInfo" class="info-grid">
          <h3>Application</h3>
          <div class="info-item">
            <span class="info-label">App Name:</span>
            <span class="info-value">{{ serverInfo.appName }}</span>
          </div>
          <div class="info-item">
            <span class="info-label">Version:</span>
            <span class="info-value">{{ serverInfo.version }}</span>
          </div>
          <div class="info-item">
            <span class="info-label">Build Date:</span>
            <span class="info-value">{{ serverInfo.buildDate }}</span>
          </div>
          <div class="info-item">
            <span class="info-label">Unity Version:</span>
            <span class="info-value">{{ serverInfo.unityVersion }}</span>
          </div>
          
          <h3>Device</h3>
          <div class="info-item">
            <span class="info-label">Device Model:</span>
            <span class="info-value">{{ serverInfo.deviceModel }}</span>
          </div>
          <div class="info-item">
            <span class="info-label">Operating System:</span>
            <span class="info-value">{{ serverInfo.operatingSystem }}</span>
          </div>
          
          <h3>Server</h3>
          <div class="info-item">
            <span class="info-label">Port:</span>
            <span class="info-value">{{ serverInfo.serverPort }}</span>
          </div>
        </div>
        
        <button @click="showInfoModal = false" class="mt-2">Close</button>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted } from 'vue'
import { useConfigStore } from './stores/config'
import { useConnectionState } from './composables/useConnectionState'
import { configApi } from './api/config'
import ConfigForm from './components/ConfigForm.vue'
import type { ServerInfo } from './types'
import { Clock3, Info, Moon, Power, RadioOff, RefreshCw, Sun } from '@lucide/vue'

const configStore = useConfigStore()
const { 
  connectionStatus, 
  isDisconnected, 
  secondsUntilRetry,
  checkConnection, 
  getReconnectDelay 
} = useConnectionState()

const showInfoModal = ref(false)
const serverInfo = ref<ServerInfo | null>(null)
const isDarkMode = ref(false)

// Initialize theme from localStorage or system preference
onMounted(async () => {
  // Check localStorage first
  const savedTheme = localStorage.getItem('questx-theme')
  if (savedTheme) {
    isDarkMode.value = savedTheme === 'dark'
  } else {
    // The dashboard's editorial theme is dark-first; the toggle still provides a light option.
    isDarkMode.value = true
  }
  applyTheme()
  
  // Initial check
  await scheduleNextCheck()
})

function applyTheme() {
  if (isDarkMode.value) {
    document.documentElement.setAttribute('data-theme', 'dark')
  } else {
    document.documentElement.removeAttribute('data-theme')
  }
}

function toggleTheme() {
  isDarkMode.value = !isDarkMode.value
  localStorage.setItem('questx-theme', isDarkMode.value ? 'dark' : 'light')
  applyTheme()
}

const connectionStatusText = computed(() => {
  switch (connectionStatus.value) {
    case 'connected': return 'Connected'
    case 'disconnected': return 'Disconnected'
    case 'connecting': return 'Connecting...'
  }
})

function formatTime(timestamp: number): string {
  const date = new Date(timestamp)
  return date.toLocaleTimeString()
}

// Monitor connection status with exponential backoff
let statusCheckInterval: number | null = null

async function scheduleNextCheck() {
  const connected = await checkConnection(false) // Automatic retry
  
  // Schedule next check based on connection state
  const delay = connected ? 3000 : getReconnectDelay()
  
  if (statusCheckInterval !== null) {
    clearTimeout(statusCheckInterval)
  }
  
  statusCheckInterval = setTimeout(scheduleNextCheck, delay) as unknown as number
}

async function manualRetry() {
  // Cancel the scheduled automatic retry
  if (statusCheckInterval !== null) {
    clearTimeout(statusCheckInterval)
  }
  
  // Try to connect immediately with manual flag (doesn't increment backoff)
  const connected = await checkConnection(true) // Manual retry
  
  // Schedule next automatic check based on result
  const delay = connected ? 3000 : getReconnectDelay()
  statusCheckInterval = setTimeout(scheduleNextCheck, delay) as unknown as number
}

onMounted(async () => {
  // Initial check
  await scheduleNextCheck()
})

onUnmounted(() => {
  if (statusCheckInterval !== null) {
    clearTimeout(statusCheckInterval)
  }
})

async function refreshData() {
  await configStore.loadConfig()
}

async function showInfo() {
  try {
    serverInfo.value = await configApi.getServerInfo()
    showInfoModal.value = true
  } catch (error) {
    console.error('Failed to load server info:', error)
  }
}

async function handleRestart() {
  if (!confirm('Are you sure you want to restart the QuestX application? This will disconnect and restart the app on the Quest headset.')) {
    return
  }
  
  try {
    await configApi.restartApp()
    // Show message that restart was triggered
    alert('Restart command sent. The app will restart shortly.')
  } catch (error) {
    console.error('Failed to restart app:', error)
    alert('Failed to send restart command: ' + (error instanceof Error ? error.message : 'Unknown error'))
  }
}
</script>

<style scoped>
/* Disconnect Overlay */
.disconnect-overlay {
  position: fixed;
  top: 0;
  left: 0;
  right: 0;
  bottom: 0;
  background: rgba(10, 10, 10, 0.72);
  backdrop-filter: blur(6px);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 9999;
  padding: 2rem;
}

.disconnect-card {
  max-width: 500px;
  padding: 3rem;
  text-align: center;
  background: var(--card-bg);
  border: 1px solid var(--danger-color);
  box-shadow: none;
}

.disconnect-icon {
  color: var(--danger-color);
  margin-bottom: 1.5rem;
  animation: disconnectPulse 2s ease-in-out infinite;
}

@keyframes disconnectPulse {
  0%, 100% {
    opacity: 1;
    transform: scale(1);
  }
  50% {
    opacity: 0.6;
    transform: scale(1.1);
  }
}

.disconnect-card h2 {
  color: var(--text-primary);
  font-size: 2rem;
  margin-bottom: 1rem;
  font-weight: 520;
}

.disconnect-card > p {
  color: var(--text-secondary);
  font-size: 1rem;
  margin-bottom: 2rem;
}

.reconnect-info {
  margin: 2rem 0;
  padding: 1.5rem;
  background: var(--bg-tertiary);
  border-radius: 4px;
  border: 1px solid var(--border-color);
}

.retry-countdown {
  font-size: 1rem; /* Base body text: 16px */
  color: var(--text-primary);
  font-weight: 600;
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 0.75rem;
}

.spinner-small {
  width: 16px;
  height: 16px;
  border: 1px solid var(--border-strong);
  border-top-color: transparent;
  border-radius: 50%;
  animation: spin 0.8s linear infinite;
}

@keyframes spin {
  to { transform: rotate(360deg); }
}

.retry-button {
  padding: 0.75rem 1.5rem;
  background: transparent;
  color: var(--danger-color);
  border: 1px solid var(--danger-color);
  border-radius: 4px;
  font-weight: 650;
  font-size: 0.78rem;
  cursor: pointer;
  transition: all 0.2s ease;
  box-shadow: none;
  margin-top: 1rem;
}

.retry-button:hover {
  background: var(--danger-color);
  border-color: var(--danger-color);
  color: #fff;
  transform: none;
  box-shadow: none;
}

.retry-button:active {
  transform: translateY(0);
}

.fade-enter-active, .fade-leave-active {
  transition: opacity 0.3s ease;
}

.fade-enter-from, .fade-leave-to {
  opacity: 0;
}

.app-container {
  min-height: 100vh;
  display: flex;
  flex-direction: column;
  background: var(--bg-color);
}

.app-header {
  background: var(--header-bg);
  border-bottom: 1px solid var(--border-color);
  padding: 1rem 2rem;
  position: sticky;
  top: 0;
  z-index: 100;
  box-shadow: none;
  backdrop-filter: blur(16px);
}

.header-content {
  max-width: 1400px;
  margin: 0 auto;
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 1.5rem;
}

.header-left {
  display: flex;
  align-items: center;
  gap: 2rem;
}

.logo-container {
  display: flex;
  align-items: center;
  gap: 1rem;
}

.logo {
  height: 64px;
  width: auto;
  filter: none;
}

.header-info {
  display: flex;
  flex-direction: column;
  gap: 0.35rem;
}

.last-updated {
  font-size: 0.7rem;
  color: var(--text-secondary);
  display: flex;
  align-items: center;
  gap: 0.4rem;
  font-weight: 600;
  letter-spacing: 0.08em;
  text-transform: uppercase;
}

.info-icon {
  font-size: 0.875rem; /* Small text: 14px */
}

.connection-status {
  font-size: 0.72rem;
  display: flex;
  align-items: center;
  gap: 0.5rem;
  font-weight: 600;
  padding: 0.25rem 0.75rem;
  border-radius: 4px;
  background: transparent;
  letter-spacing: 0.07em;
  text-transform: uppercase;
}

.connection-status .status-dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  display: inline-block;
}

.connection-status.connected {
  color: var(--success-color);
  background: transparent;
  border: 0;
  box-shadow: none;
}

.connection-status.connected .status-dot {
  background-color: var(--success-color);
}

.connection-status.disconnected {
  color: var(--danger-color);
  background: transparent;
  border: 1px solid var(--danger-color);
  box-shadow: none;
}

.connection-status.disconnected .status-dot {
  background-color: var(--danger-color);
}

.connection-status.connecting {
  color: var(--warning-color);
  background: transparent;
  border: 1px solid var(--warning-color);
  box-shadow: none;
}

.connection-status.connecting .status-dot {
  background-color: var(--warning-color);
  animation: pulse 2s ease-in-out infinite;
}

@keyframes pulse {
  0%, 100% { 
    opacity: 1;
    transform: scale(1);
  }
  50% { 
    opacity: 0.3;
    transform: scale(0.8);
  }
}

.header-right {
  display: flex;
  gap: 0.75rem;
}

.icon-button {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.6rem 1.2rem;
  font-size: 0.875rem; /* Small text: 14px */
}

.icon-button.theme-toggle {
  padding: 0.6rem;
  min-width: auto;
  background: transparent;
  border: 1px solid var(--border-strong);
  color: var(--text-primary);
}

.icon-button.theme-toggle:hover {
  background: var(--bg-tertiary);
  border-color: var(--primary-color);
  color: var(--primary-color);
}

.theme-icon {
  width: 20px;
  height: 20px;
  stroke: currentColor;
}

.button-icon {
  font-size: 1rem; /* Base icon size: 16px */
}

.app-content {
  flex: 1;
  padding: 2rem;
  max-width: 1400px;
  width: 100%;
  margin: 0 auto;
}

.modal-overlay {
  position: fixed;
  top: 0;
  left: 0;
  right: 0;
  bottom: 0;
  background-color: rgba(0, 0, 0, 0.78);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 1000;
  padding: 1rem;
  backdrop-filter: blur(4px);
  animation: fadeIn 0.2s ease;
}

@keyframes fadeIn {
  from {
    opacity: 0;
  }
  to {
    opacity: 1;
  }
}

.modal-content {
  max-width: 700px;
  width: 100%;
  max-height: 90vh;
  overflow-y: auto;
  animation: slideUp 0.3s ease;
}

@keyframes slideUp {
  from {
    transform: translateY(30px);
    opacity: 0;
  }
  to {
    transform: translateY(0);
    opacity: 1;
  }
}

.modal-content h2 {
  color: var(--text-primary);
  margin-bottom: 1rem;
  font-size: 1.5rem; /* Consistent h2 size: 24px */
}

.info-grid {
  display: flex;
  flex-direction: column;
  gap: 1rem;
  margin-top: 1.5rem;
}

.info-grid h3 {
  margin-top: 1.5rem;
  margin-bottom: 0.75rem;
  color: var(--text-primary);
  font-size: 1.125rem; /* Consistent h3 size: 18px */
  border-bottom: 1px solid var(--border-color);
  padding-bottom: 0.5rem;
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.info-grid h3::before {
  content: '';
  width: 1.25rem;
  height: 1px;
  background: var(--primary-color);
}

.info-grid h3:first-of-type {
  margin-top: 0;
}

.info-item {
  display: grid;
  grid-template-columns: 160px 1fr;
  gap: 1rem;
  padding: 0.75rem;
  background: var(--bg-tertiary);
  border-radius: 4px;
  border-left: 1px solid var(--primary-color);
  transition: all 0.2s ease;
}

.info-item:hover {
  background: var(--bg-secondary);
}

.info-label {
  font-weight: 600;
  color: var(--text-secondary);
  font-size: 0.875rem; /* Small text: 14px */
}

.info-value {
  color: var(--text-primary);
  word-break: break-all;
  font-size: 0.875rem; /* Small text: 14px */
}

@media (max-width: 1024px) {
  .header-content {
    max-width: 100%;
  }
  
  .app-content {
    max-width: 100%;
  }
  
}

@media (max-width: 768px) {
  .header-content {
    flex-direction: column;
    align-items: stretch;
  }
  
  .header-left {
    flex-direction: column;
    align-items: flex-start;
    gap: 1rem;
  }
  
  .logo-container {
    width: 100%;
  }
  
  .header-right {
    justify-content: space-between;
    flex-wrap: wrap;
  }
  
  .icon-button .button-text {
    display: none;
  }
  
  .icon-button {
    padding: 0.6rem;
    flex: 1;
    justify-content: center;
  }
  
  .icon-button.theme-toggle {
    flex: 0;
    min-width: 44px;
  }
  
  .app-content {
    padding: 1rem;
  }
  
  .info-item {
    grid-template-columns: 1fr;
    gap: 0.5rem;
  }
}
</style>
