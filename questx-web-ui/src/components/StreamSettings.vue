<template>
  <!-- Stream settings apply as soon as they change; only detector settings are staged. -->
  <div>
    <div class="row">
      <label for="stream-enabled">Passthrough stream</label>
      <div class="control">
        <label class="switch">
          <input id="stream-enabled" type="checkbox" :checked="streamEnabled" @change="setStreamEnabled" />
          {{ streamEnabled ? 'Enabled' : 'Disabled' }}
        </label>
      </div>
    </div>

    <div class="row">
      <label for="stream-orientation">Orientation</label>
      <div class="control">
        <select id="stream-orientation" :value="orientation" @change="setOrientation">
          <option v-for="option in ORIENTATION_OPTIONS" :key="option.value" :value="option.value">{{ option.label }}</option>
        </select>
      </div>
    </div>
    <div v-if="mountMismatch" class="callout-warn">
      <TriangleAlert :size="15" :stroke-width="1.8" aria-hidden="true" />
      <span>
        The Robot to Quest roll has the headset {{ mountMismatch }}.
        Match it on the <button class="link" @click="emit('navigate', '3d')">3D page</button>.
      </span>
    </div>

    <div v-if="lockedByDetector" class="callout-note">
      <LockKeyhole :size="15" :stroke-width="1.8" aria-hidden="true" />
      <span>
        The AprilTag detector owns the camera, so the stream runs at the detector's
        <b>{{ effectiveResolution }}</b>. Change it on the Detector tab, or turn the detector off.
      </span>
    </div>

    <div class="row">
      <label for="stream-resolution">Resolution</label>
      <div class="control">
        <select id="stream-resolution" :value="resolutionKey" :disabled="modeLocked" @change="setResolution">
          <option v-for="key in resolutionKeys" :key="key" :value="key">{{ key }}</option>
        </select>
      </div>
    </div>
    <div class="row">
      <label for="stream-framerate">Framerate</label>
      <div class="control">
        <select id="stream-framerate" :value="framerate" :disabled="modeLocked" @change="setFramerate">
          <option v-for="fps in framerates" :key="fps" :value="fps">{{ fps }} fps</option>
        </select>
      </div>
    </div>
    <div class="row">
      <label for="stream-quality">JPEG quality</label>
      <div class="control">
        <input id="stream-quality" v-model.number="quality" type="range" min="1" max="100" :disabled="!streamEnabled" @change="saveQuality" />
        <input v-model.number="quality" class="number" type="number" min="1" max="100" aria-label="JPEG quality" :disabled="!streamEnabled" @change="saveQuality" />
      </div>
    </div>

    <div class="sect">High quality</div>
    <div class="row">
      <label for="stream-hq">High quality stream</label>
      <div class="control">
        <label class="switch">
          <input id="stream-hq" type="checkbox" :checked="highQuality" @change="setHighQuality" />
          {{ highQuality ? 'On' : 'Off' }}
        </label>
      </div>
    </div>
    <p class="hint">Unlocks resolutions above 640x480 and framerates above 30 fps.</p>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { LockKeyhole, TriangleAlert } from '@lucide/vue'
import { useConfigStore } from '../stores/config'
import { useHeadsetStatus } from '../composables/useHeadsetStatus'
import { errorText, toast } from '../composables/useToast'
import { videoApi } from '../api/video'
import { mountedOrientation } from '../three/mountMath'
import { CameraOrientation, type PageId, type StreamModeModel, type VideoModeModel } from '../types'

const emit = defineEmits<{ navigate: [page: PageId] }>()

const ORIENTATION_OPTIONS = [
  { label: 'Normal', value: CameraOrientation.Normal },
  { label: 'Upside-down', value: CameraOrientation.UpsideDown },
  { label: 'Left (90° CCW)', value: CameraOrientation.Left },
  { label: 'Right (90° CW)', value: CameraOrientation.Right },
]
const MOUNTED_DESCRIPTIONS: Record<CameraOrientation, string> = {
  [CameraOrientation.Normal]: 'upright',
  [CameraOrientation.UpsideDown]: 'upside-down',
  [CameraOrientation.Left]: 'rolled left',
  [CameraOrientation.Right]: 'rolled right',
}
// Without high quality, the stream is capped at 640x480 and 30 fps.
const STANDARD_MAX_PIXELS = 640 * 480
const STANDARD_MAX_FPS = 30

const configStore = useConfigStore()
const { status } = useHeadsetStatus()

const streamEnabled = computed(() => configStore.config?.enablePassthroughStream ?? false)
const highQuality = computed(() => configStore.config?.enableHighQualityStream ?? false)
const orientation = computed(() => configStore.config?.cameraOrientation ?? CameraOrientation.Normal)
const streamMode = computed(() => configStore.config?.streamMode ?? null)

// The orientation only rotates the picture; poses use the saved mount roll. Flag a mount
// whose roll disagrees, as the 3D page's Robot view does.
const mountMismatch = computed(() => {
  const mount = configStore.config?.robotToQuest
  if (!mount) return null
  const mounted = mountedOrientation(mount)
  if (mounted === orientation.value) return null
  return mounted === null ? 'facing up or down' : MOUNTED_DESCRIPTIONS[mounted]
})

const lockedByDetector = computed(() => status.value?.passthroughResolutionLockedByAprilTag ?? false)
const effectiveResolution = computed(() => {
  const r = status.value?.effectivePassthroughResolution
  return r ? `${r.width}x${r.height}` : 'resolution'
})
const modeLocked = computed(() => !streamEnabled.value || lockedByDetector.value)

// ---- Mode options ----

const videoModes = ref<VideoModeModel[]>([])

async function loadVideoModes() {
  if (!streamEnabled.value) return
  try {
    videoModes.value = await videoApi.getVideoModes()
  } catch (error) {
    console.error('Failed to load stream modes:', error)
  }
}

const resolutionKey = computed(() => {
  if (lockedByDetector.value) return effectiveResolution.value
  return streamMode.value ? `${streamMode.value.width}x${streamMode.value.height}` : ''
})

const resolutionKeys = computed(() => {
  if (lockedByDetector.value) return [effectiveResolution.value]
  const keys = videoModes.value
    .filter(m => highQuality.value || m.width * m.height <= STANDARD_MAX_PIXELS)
    .map(m => `${m.width}x${m.height}`)
  const unique = [...new Set(keys)]
  // Keep the saved mode visible even if the camera list does not offer it.
  if (resolutionKey.value && !unique.includes(resolutionKey.value)) unique.unshift(resolutionKey.value)
  return unique
})

const framerate = computed(() => streamMode.value?.framerate ?? 0)

const framerates = computed(() => {
  if (lockedByDetector.value) return [framerate.value]
  const rates = [...new Set(videoModes.value.map(m => m.framerate))]
    .filter(fps => highQuality.value || fps <= STANDARD_MAX_FPS)
    .sort((a, b) => a - b)
  if (framerate.value && !rates.includes(framerate.value)) rates.unshift(framerate.value)
  return rates
})

// ---- Writes ----

const quality = ref(streamMode.value?.quality ?? 75)
watch(() => streamMode.value?.quality, value => {
  if (value !== undefined) quality.value = value
})

async function saveMode(change: Partial<StreamModeModel>) {
  if (!streamMode.value) return
  const ok = await configStore.updateStreamMode({ ...streamMode.value, ...change })
  if (!ok) toast(errorText('Could not change the stream', configStore.error), 'error')
}

function setResolution(event: Event) {
  const [width, height] = (event.target as HTMLSelectElement).value.split('x').map(Number)
  saveMode({ width, height })
}

function setFramerate(event: Event) {
  saveMode({ framerate: Number((event.target as HTMLSelectElement).value) })
}

function saveQuality() {
  const value = Math.min(100, Math.max(1, Math.round(quality.value) || 1))
  quality.value = value
  if (value !== streamMode.value?.quality) saveMode({ quality: value })
}

async function setStreamEnabled(event: Event) {
  await configStore.updateEnablePassthroughStream((event.target as HTMLInputElement).checked)
}

async function setHighQuality(event: Event) {
  await configStore.updateEnableHighQualityStream((event.target as HTMLInputElement).checked)
}

async function setOrientation(event: Event) {
  const target = event.target as HTMLSelectElement
  if (!(await configStore.updateCameraOrientation(Number(target.value) as CameraOrientation))) {
    target.value = String(orientation.value)
    toast(errorText('Could not change the orientation', configStore.error), 'error')
  }
}

watch([streamEnabled, highQuality], loadVideoModes)
onMounted(loadVideoModes)
</script>
