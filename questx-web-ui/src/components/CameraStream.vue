<template>
  <div class="camera-stream">
    <div
      ref="container"
      :class="['stream', { portrait: isPortrait, off: !streamEnabled }]"
      :style="{ aspectRatio: streamEnabled ? aspectRatio : '4 / 3' }"
    >
      <template v-if="streamEnabled && active">
        <!-- With the detector on, the preview is the detector's own frame with its tag
             outlines (always on). Without it there is nothing to outline, so show video. -->
        <AprilTagOverlay
          v-if="detectorEnabled"
          :key="streamKey"
          @dimensions="previewSize = $event"
          @stats="stats = $event"
        />
        <img v-else :src="streamUrl" alt="Passthrough camera stream" class="mjpeg" @error="streamFailed = true" />
        <span v-if="!detectorEnabled && streamFailed" class="stream-message">Camera stream unavailable. Turn the stream off and on to reconnect.</span>
        <div v-if="detectorEnabled && stats" class="hud mono">
          <!-- The preview runs at the stream's frame rate; detection runs on every camera frame. -->
          <template v-if="detectionFps > 0">detect {{ detectionFps.toFixed(0) }} Hz · </template><template v-if="stats.fps > 0">preview {{ stats.fps.toFixed(1) }} fps · </template>{{ Math.round(stats.ageMs) }} ms
        </div>
        <button class="fs-btn" :title="isFullscreen ? 'Exit fullscreen' : 'Fullscreen'" :aria-label="isFullscreen ? 'Exit fullscreen' : 'Fullscreen'" @click="toggleFullscreen">
          <Minimize2 v-if="isFullscreen" :size="15" :stroke-width="2" aria-hidden="true" />
          <Maximize2 v-else :size="15" :stroke-width="2" aria-hidden="true" />
        </button>
      </template>
      <div v-else-if="!streamEnabled" class="stream-off">
        <CameraOff :size="40" :stroke-width="1.5" aria-hidden="true" />
        <b>Passthrough stream is off</b>
        <button class="btn primary" @click="configStore.updateEnablePassthroughStream(true)">Turn stream on</button>
      </div>
    </div>

    <div class="readout">
      <span><span class="k">x:</span>{{ fixed(status?.position.x, 3, ' m') }}</span>
      <span><span class="k">y:</span>{{ fixed(status?.position.y, 3, ' m') }}</span>
      <span><span class="k">z:</span>{{ fixed(status?.position.z, 3, ' m') }}</span>
      <!-- Same field as the old Status tab's "Yaw" row; StatusProvider fills pitch from euler z. -->
      <span><span class="k">yaw:</span>{{ fixed(status?.eulerAngles.pitch, 1, '°') }}</span>
    </div>
    <div class="readout secondary">
      <span><span class="k">tags:</span>{{ tagIds }}</span>
      <span><span class="k">age:</span>{{ measurementAge }}</span>
      <span><span class="k">inliers:</span>{{ inliers }}</span>
      <span><span class="k">σxy:</span>{{ tagCount ? `${status!.aprilTag.linearStdDevMeters.toFixed(3)} m` : 'n/a' }}</span>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { CameraOff, Maximize2, Minimize2 } from '@lucide/vue'
import { useConfigStore } from '../stores/config'
import { useHeadsetStatus } from '../composables/useHeadsetStatus'
import { videoApi } from '../api/video'
import { CameraOrientation } from '../types'
import AprilTagOverlay from './AprilTagOverlay.vue'

const props = defineProps<{ active: boolean }>()

const configStore = useConfigStore()
const { status } = useHeadsetStatus()

const container = ref<HTMLElement | null>(null)
const isFullscreen = ref(false)
const streamFailed = ref(false)
const previewSize = ref<{ width: number; height: number } | null>(null)
const stats = ref<{ fps: number; ageMs: number; tags: number } | null>(null)

const streamEnabled = computed(() => configStore.config?.enablePassthroughStream ?? false)
const detectorEnabled = computed(() => configStore.config?.enableAprilTagDetector ?? false)
const detectionFps = computed(() => status.value?.aprilTag.processedFps ?? 0)
const orientation = computed(() => configStore.config?.cameraOrientation ?? CameraOrientation.Normal)

// A new stream mode or orientation restarts the MJPEG request and the preview.
const streamKey = computed(() => {
  const mode = configStore.config?.streamMode
  return `${mode?.width}x${mode?.height}@${mode?.framerate}q${mode?.quality}r${orientation.value}`
})
const streamUrl = computed(() => `${videoApi.baseUrl}/video?k=${encodeURIComponent(streamKey.value)}`)
watch([streamKey, detectorEnabled], () => {
  streamFailed.value = false
  previewSize.value = null
  stats.value = null
})

// The preview reports its upright size; camera modes are sensor-oriented.
const displaySize = computed(() => {
  if (detectorEnabled.value && previewSize.value) return previewSize.value
  const mode = status.value?.effectivePassthroughResolution ?? configStore.config?.streamMode
  if (!mode || mode.width <= 0 || mode.height <= 0) return null
  const swaps = orientation.value === CameraOrientation.Left || orientation.value === CameraOrientation.Right
  return swaps ? { width: mode.height, height: mode.width } : { width: mode.width, height: mode.height }
})
const aspectRatio = computed(() => displaySize.value ? `${displaySize.value.width} / ${displaySize.value.height}` : '4 / 3')
// A full-width portrait stream would be far taller than the window; letterbox it instead.
const isPortrait = computed(() => !!displaySize.value && displaySize.value.height > displaySize.value.width)

// ---- Readouts from the shared status poll ----

const tagCount = computed(() => status.value?.aprilTag.tagIds.length ?? 0)
const tagIds = computed(() => tagCount.value ? status.value!.aprilTag.tagIds.join(', ') : 'none')
const measurementAge = computed(() => {
  const age = status.value?.aprilTag.measurementAgeMs ?? -1
  if (age < 0) return 'n/a'
  return age < 1000 ? `${Math.round(age)} ms` : `${(age / 1000).toFixed(1)} s`
})
const inliers = computed(() => {
  const tag = status.value?.aprilTag
  return tag ? `${tag.inlierCount}/${tag.cornerCount}` : 'n/a'
})

function fixed(value: number | undefined, digits: number, unit: string) {
  if (value === undefined || !Number.isFinite(value)) return 'n/a'
  return `${value >= 0 ? '+' : ''}${value.toFixed(digits)}${unit}`
}

function toggleFullscreen() {
  if (document.fullscreenElement) {
    document.exitFullscreen()
  } else {
    container.value?.requestFullscreen().catch(error => console.error('Failed to enter fullscreen:', error))
  }
}

function onFullscreenChange() {
  isFullscreen.value = document.fullscreenElement === container.value
}

watch(() => props.active, active => {
  if (!active && isFullscreen.value) document.exitFullscreen()
})

onMounted(() => document.addEventListener('fullscreenchange', onFullscreenChange))
onUnmounted(() => document.removeEventListener('fullscreenchange', onFullscreenChange))
</script>

<style scoped>
.stream {
  position: relative;
  overflow: hidden;
  width: 100%;
  border: 1px solid var(--line);
  border-radius: var(--radius-lg);
  background: #000;
}

.stream.portrait {
  width: auto;
  height: min(75vh, 900px);
  margin: 0 auto;
}

.stream:fullscreen {
  width: 100%;
  height: 100%;
  border: 0;
  border-radius: 0;
}

.mjpeg {
  display: block;
  width: 100%;
  height: 100%;
  object-fit: contain;
}

.stream-message {
  position: absolute;
  top: 50%;
  left: 50%;
  transform: translate(-50%, -50%);
  color: var(--text-2);
  text-align: center;
}

.hud {
  position: absolute;
  top: 10px;
  left: 10px;
  padding: 3px 8px;
  border: 1px solid var(--line);
  border-radius: 6px;
  background: rgba(8, 9, 10, 0.6);
  backdrop-filter: blur(8px);
  color: #e6e8ec;
  font-size: 12px;
}

.fs-btn {
  position: absolute;
  right: 10px;
  bottom: 10px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 30px;
  height: 30px;
  border: 1px solid var(--line-strong);
  border-radius: 7px;
  background: rgba(8, 9, 10, 0.6);
  backdrop-filter: blur(8px);
  color: var(--text-2);
}

.fs-btn:hover {
  color: #fff;
}

.stream-off {
  position: absolute;
  inset: 0;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 12px;
  background: #0b0c0e;
  color: var(--muted);
}

.stream-off b {
  color: var(--text);
  font-size: 15px;
  font-weight: 600;
}

.readout {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 6px 16px;
  margin-top: 10px;
  padding: 10px 14px;
  border: 1px solid var(--line);
  border-radius: 10px;
  background: var(--panel);
  font-size: 14px;
  font-variant-numeric: tabular-nums;
}

.readout.secondary {
  margin-top: 6px;
  color: var(--text-2);
  font-size: 13px;
}

.readout > span {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.readout .k {
  margin-right: 4px;
  color: var(--muted);
}

@media (max-width: 640px) {
  .readout {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}
</style>
