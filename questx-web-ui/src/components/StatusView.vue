<template>
  <div class="status-view">
    <div v-if="loading" class="loading-container">
      <div class="spinner"></div>
      <p>Loading status...</p>
    </div>

    <div v-else-if="status" class="status-grid">
      <!-- Network Status -->
      <div class="status-card card">
        <h3><Wifi :size="18" :stroke-width="1.6" aria-hidden="true" /> Network</h3>
        <div class="status-items">
          <div class="status-item">
            <span class="label">Robot Connection:</span>
            <span :class="['value', status.networkConnected ? 'success' : 'error']">
              {{ status.networkConnected ? 'Connected' : 'Disconnected' }}
            </span>
          </div>
          <div class="status-item" :class="{ 'debug-override-active': isDebugIPActive }">
            <span class="label">Robot IP Address:</span>
            <div class="value-with-badge">
              <span class="value mono">{{ status.robotIpAddress || 'N/A' }}</span>
              <span v-if="isDebugIPActive" class="debug-override-badge">DEBUG OVERRIDE ENABLED</span>
            </div>
          </div>
          <div class="status-item">
            <span class="label">Headset IP Address:</span>
            <span class="value mono">{{ status.ipAddress }}</span>
          </div>
          <div class="status-item">
            <span class="label">Team Number:</span>
            <span class="value">{{ status.teamNumber }}</span>
          </div>
        </div>
      </div>

      <!-- Position & Tracking -->
      <div class="status-card card">
        <h3><MapPin :size="18" :stroke-width="1.6" aria-hidden="true" /> Local VIO Position</h3>
        <div class="status-items">
          <div class="status-item">
            <span class="label">X:</span>
            <span class="value mono">{{ status.position.x.toFixed(3) }} m</span>
          </div>
          <div class="status-item">
            <span class="label">Y:</span>
            <span class="value mono">{{ status.position.y.toFixed(3) }} m</span>
          </div>
          <div class="status-item">
            <span class="label">Z:</span>
            <span class="value mono">{{ status.position.z.toFixed(3) }} m</span>
          </div>
          <div class="status-item">
            <span class="label">Yaw:</span>
            <span class="value mono">{{ status.eulerAngles.pitch.toFixed(1) }}°</span>
          </div>
          <div class="status-item">
            <span class="label">Pitch:</span>
            <span class="value mono">{{ status.eulerAngles.yaw.toFixed(1) }}°</span>
          </div>
          <div class="status-item">
            <span class="label">Roll:</span>
            <span class="value mono">{{ status.eulerAngles.roll.toFixed(1) }}°</span>
          </div>
        </div>
      </div>

      <!-- Tracking Status -->
      <div class="status-card card">
        <h3><Crosshair :size="18" :stroke-width="1.6" aria-hidden="true" /> Tracking</h3>
        <div class="status-items">
          <div class="status-item">
            <span class="label">Status:</span>
            <span :class="['value', status.isTracking ? 'success' : 'error']">
              {{ status.isTracking ? 'Tracking' : 'Lost' }}
            </span>
          </div>
          <div class="status-item">
            <span class="label">Tracking Lost Events:</span>
            <span class="value">{{ status.trackingLostEvents }}</span>
          </div>
          <div class="status-item">
            <span class="label">Tracking Epoch:</span>
            <span class="value">{{ status.trackingEpoch }}</span>
          </div>
          <div class="status-item">
            <span class="label">VIO Measurements Published:</span>
            <span class="value">{{ status.vioPublishCount.toLocaleString() }}</span>
          </div>
        </div>
      </div>

      <!-- Latest field-relative measurement; robot-side fusion owns acceptance. -->
      <div class="status-card card">
        <h3><ScanLine :size="18" :stroke-width="1.6" aria-hidden="true" /> AprilTag Measurements</h3>
        <div class="status-items">
          <div class="status-item">
            <span class="label">Detector:</span>
            <span :class="['value', status.aprilTag.detectorActive ? 'success' : 'error']">
              {{ status.aprilTag.detectorActive ? 'Active' : 'Disabled' }}
            </span>
          </div>
          <div class="status-item">
            <span class="label">Last Outcome:</span>
            <span class="value">{{ status.aprilTag.lastOutcome }}</span>
          </div>
          <div v-if="status.aprilTag.lastReason" class="status-item">
            <span class="label">Reason:</span>
            <span class="value">{{ status.aprilTag.lastReason }}</span>
          </div>
          <div class="status-item">
            <span class="label">Last Measurement Age:</span>
            <span class="value mono">
              {{ status.aprilTag.measurementAgeMs >= 0 ? `${status.aprilTag.measurementAgeMs.toFixed(0)} ms` : 'N/A' }}
            </span>
          </div>
          <div class="status-item">
            <span class="label">Tag IDs:</span>
            <span class="value mono">{{ status.aprilTag.tagIds.length ? status.aprilTag.tagIds.join(', ') : 'None' }}</span>
          </div>
          <div class="status-item">
            <span class="label">Corner Inliers:</span>
            <span class="value mono">{{ status.aprilTag.inlierCount }}/{{ status.aprilTag.cornerCount }}</span>
          </div>
          <div class="status-item">
            <span class="label">Inlier Ratio:</span>
            <span class="value mono">{{ (status.aprilTag.inlierRatio * 100).toFixed(1) }}%</span>
          </div>
          <div class="status-item">
            <span class="label">Average Distance:</span>
            <span class="value mono">{{ status.aprilTag.averageTagDistanceMeters.toFixed(2) }} m</span>
          </div>
          <div class="status-item">
            <span class="label">Suggested XY Std Dev:</span>
            <span class="value mono">{{ status.aprilTag.linearStdDevMeters.toFixed(3) }} m</span>
          </div>
          <div class="status-item">
            <span class="label">Published:</span>
            <span class="value">{{ status.aprilTag.publishCount.toLocaleString() }}</span>
          </div>
          <div class="status-item">
            <span class="label">Camera Failures:</span>
            <span :class="['value', status.aprilTag.cameraFailureCount ? 'error' : 'success']">
              {{ status.aprilTag.cameraFailureCount.toLocaleString() }}
            </span>
          </div>
        </div>
      </div>

      <!-- Remote stream health makes camera/transport dropouts distinguishable. -->
      <div class="status-card card">
        <h3><Radio :size="18" :stroke-width="1.6" aria-hidden="true" /> Passthrough Stream</h3>
        <div class="status-items">
          <div class="status-item">
            <span class="label">MJPEG Clients:</span>
            <span class="value">{{ status.passthroughStream.connectedClients }}</span>
          </div>
          <div class="status-item">
            <span class="label">Last Frame Age:</span>
            <span class="value mono">
              {{ status.passthroughStream.lastFrameAgeMs >= 0 ? `${status.passthroughStream.lastFrameAgeMs.toFixed(0)} ms` : 'No frame yet' }}
            </span>
          </div>
          <div class="status-item">
            <span class="label">Capture Failures:</span>
            <span class="value">{{ status.passthroughStream.captureFailureCount.toLocaleString() }}</span>
          </div>
          <div class="status-item">
            <span class="label">Consecutive Failures:</span>
            <span :class="['value', status.passthroughStream.consecutiveCaptureFailures ? 'error' : 'success']">
              {{ status.passthroughStream.consecutiveCaptureFailures }}
            </span>
          </div>
        </div>
      </div>

      <!-- Battery -->
      <div class="status-card card">
        <h3><BatteryMedium :size="18" :stroke-width="1.6" aria-hidden="true" /> Battery</h3>
        <div class="status-items">
          <div class="status-item">
            <span class="label">Level:</span>
            <div class="battery-bar">
              <div 
                class="battery-fill" 
                :style="{ width: status.batteryPercent + '%' }"
                :class="{ 
                  'battery-low': status.batteryPercent < 20,
                  'battery-charging': status.batteryCharging 
                }"
              >
                {{ status.batteryPercent }}%
              </div>
            </div>
          </div>
        </div>
      </div>

      <!-- Performance -->
      <div class="status-card card">
        <h3><Gauge :size="18" :stroke-width="1.6" aria-hidden="true" /> Performance</h3>
        <div class="status-items">
          <div class="status-item">
            <span class="label">FPS:</span>
            <span class="value">{{ status.fps }}</span>
          </div>
          <div class="status-item">
            <span class="label">Frame Count:</span>
            <span class="value mono">{{ status.frameCount.toLocaleString() }}</span>
          </div>
          <div class="status-item">
            <span class="label">Connected Clients:</span>
            <span class="value">{{ status.connectedClients }}</span>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted, watch } from 'vue'
import { configApi } from '../api/config'
import { useConfigStore } from '../stores/config'
import { useConnectionState } from '../composables/useConnectionState'
import type { HeadsetStatus } from '../types'
import { BatteryMedium, Crosshair, Gauge, MapPin, Radio, ScanLine, Wifi } from '@lucide/vue'

const configStore = useConfigStore()
const { isConnected } = useConnectionState()
const status = ref<HeadsetStatus | null>(null)
const loading = ref(true)
let intervalId: number | null = null

// Check if debug IP override is active
const isDebugIPActive = computed(() => {
  return Boolean(configStore.config?.debugIpOverride)
})

async function loadStatus() {
  // Skip loading if disconnected
  if (!isConnected.value) {
    return
  }
  
  try {
    status.value = await configApi.getHeadsetStatus()
    loading.value = false
  } catch (err) {
    // Silently fail - don't show error, keep old data visible
    // Connection state overlay will handle showing disconnect status
    loading.value = false
  }
}

onMounted(async () => {
  await loadStatus()
  // Update every second, but only when connected
  intervalId = setInterval(loadStatus, 1000) as unknown as number
})

onUnmounted(() => {
  if (intervalId !== null) {
    clearInterval(intervalId)
  }
})

// Watch connection state and reload when reconnected
watch(isConnected, async (connected, wasConnected) => {
  if (connected && !wasConnected) {
    console.log('[StatusView] Connection restored, reloading status')
    await loadStatus()
  }
})
</script>

<style scoped>
.status-view {
  width: 100%;
  max-width: 1400px;
  margin: 0 auto;
}

.loading-container {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  padding: 4rem 2rem;
  gap: 1.5rem;
}

.loading-container p {
  color: var(--primary-color);
  font-size: 1rem; /* Base body text: 16px */
  font-weight: 500;
}

.error-message {
  padding: 2rem;
  background: var(--card-bg);
  border: 1px solid var(--danger-color);
  border-radius: 4px;
  color: var(--danger-color);
  text-align: center;
  font-weight: 600;
  box-shadow: none;
}

.status-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(320px, 1fr));
  gap: 1.5rem;
}

.status-card {
  padding: 1.5rem;
  background: var(--card-bg);
  border: 1px solid var(--border-color);
  border-radius: 4px;
  transition: all 0.2s ease;
  position: relative;
  overflow: hidden;
}

.status-card:hover {
  border-color: var(--primary-color);
  box-shadow: none;
}

.status-card h3 {
  margin-bottom: 1rem;
  color: var(--text-primary);
  font-size: 1.3rem;
  font-weight: 520;
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.status-items {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

.status-item {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 0.75rem;
  background: var(--bg-tertiary);
  border-radius: 4px;
  border-left: 1px solid transparent;
  transition: all 0.2s ease;
}

.status-item:hover {
  border-left-color: var(--primary-color);
}

.status-item .label {
  font-weight: 500;
  color: var(--text-secondary);
  font-size: 0.875rem; /* Small text: 14px */
}

.status-item .value {
  font-weight: 600;
  color: var(--text-primary);
  font-size: 0.875rem; /* Small text: 14px */
}

.status-item .value.mono {
  font-size: 0.875rem; /* Small text: 14px */
  font-weight: 500;
}

.status-item .value.success {
  color: var(--success-color);
  text-shadow: none;
}

.status-item .value.error {
  color: var(--danger-color);
  text-shadow: none;
}

.status-item.debug-override-active {
  background: var(--bg-tertiary);
  border: 1px solid var(--warning-color);
  border-left: 1px solid var(--warning-color);
  box-shadow: none;
}

.status-item.debug-override-active:hover {
  background: var(--bg-tertiary);
  border-left-color: var(--warning-color);
}

.value-with-badge {
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  gap: 0.5rem;
}

.debug-override-badge {
  font-size: 0.75rem; /* Extra small: 12px */
  padding: 0.25rem 0.65rem;
  background: transparent;
  color: var(--danger-color);
  border-radius: 4px;
  border: 1px solid var(--danger-color);
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.5px;
  animation: none;
  box-shadow: none;
  white-space: nowrap;
}

@keyframes pulseGlow {
  0%, 100% { 
    opacity: 1;
    box-shadow: 0 2px 6px rgba(220, 53, 69, 0.4);
  }
  50% { 
    opacity: 0.8;
    box-shadow: 0 4px 12px rgba(220, 53, 69, 0.6);
  }
}


.battery-bar {
  flex: 1;
  height: 28px;
  background-color: var(--bg-tertiary);
  border-radius: 4px;
  overflow: hidden;
  margin-left: 1rem;
  border: 1px solid var(--border-color);
}

.battery-fill {
  height: 100%;
  background: var(--teal);
  display: flex;
  align-items: center;
  justify-content: center;
  font-weight: 700;
  font-size: 0.875rem; /* Small text: 14px */
  color: white;
  text-shadow: none;
  transition: width 0.5s ease;
  position: relative;
}

.battery-fill.battery-low {
  background: var(--danger-color);
  animation: none;
}

.battery-fill.battery-charging {
  background: var(--amber);
  animation: none;
}

@keyframes batteryPulse {
  0%, 100% { 
    opacity: 1;
    box-shadow: 0 0 0 rgba(220, 53, 69, 0);
  }
  50% { 
    opacity: 0.8;
    box-shadow: 0 0 20px rgba(220, 53, 69, 0.5);
  }
}

@keyframes chargingShine {
  0% {
    background-position: -200% 0;
  }
  100% {
    background-position: 200% 0;
  }
}

@media (max-width: 768px) {
  .status-grid {
    grid-template-columns: 1fr;
  }
}
</style>
