<template>
  <div>
    <div class="row">
      <label for="detector-enabled">AprilTag detector</label>
      <div class="control">
        <label class="switch">
          <input id="detector-enabled" type="checkbox" :checked="enabled" @change="setEnabled" />
          {{ enabled ? 'Enabled' : 'Disabled' }}
        </label>
      </div>
    </div>

    <template v-if="enabled && draft">
      <div class="sect">Camera</div>
      <div :class="['row', { dirty: isDirty('resolution') }]">
        <label for="detector-resolution">Resolution</label>
        <div class="control">
          <select id="detector-resolution" :value="resolutionKey" :disabled="resolutions.length === 0" @change="setResolution">
            <option v-for="key in resolutions" :key="key" :value="key">{{ key }}</option>
          </select>
        </div>
      </div>
      <div :class="['row', { dirty: isDirty('framerate') }]">
        <label for="detector-rate">Detection rate</label>
        <div class="control">
          <select id="detector-rate" :value="draft.framerate" :disabled="framerates.length === 0" @change="setFramerate">
            <option v-for="fps in framerates" :key="fps" :value="fps">{{ fps }} fps</option>
          </select>
        </div>
      </div>
      <div :class="['row', { dirty: isDirty('detector') }]">
        <label for="detector-backend">Tag detector</label>
        <div class="control">
          <select id="detector-backend" :value="draft.detector ?? 0" @change="setBackend">
            <option v-for="option in BACKENDS" :key="option.value" :value="option.value">{{ option.label }}</option>
          </select>
        </div>
      </div>

      <div class="sect">Field</div>
      <div :class="['row', { dirty: isDirty('fieldLayoutFile') }]">
        <label for="detector-layout">Field layout</label>
        <div class="control">
          <select id="detector-layout" :value="draft.fieldLayoutFile" @change="setLayout">
            <option v-if="!layoutListed" :value="draft.fieldLayoutFile">{{ draft.fieldLayoutFile }}</option>
            <option v-for="layout in layouts" :key="layout.fileName" :value="layout.fileName">
              {{ layout.displayName }} ({{ layout.tagCount }} tags{{ layout.source === 'custom' ? ', custom' : '' }})
            </option>
          </select>
          <button class="link" @click="emit('navigate', 'field')">Manage</button>
        </div>
      </div>
    </template>

    <div v-else class="empty-pane">
      <b>Detector is off</b>
      QuestX publishes VIO pose only.
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useConfigStore } from '../stores/config'
import { injectDetectorDraft, type DraftField } from '../composables/useDetectorDraft'
import { refreshFieldLayouts, useFieldLayouts } from '../composables/useFieldLayouts'
import { videoApi } from '../api/video'
import type { PageId, VideoModeModel } from '../types'

const emit = defineEmits<{ navigate: [page: PageId] }>()

// Mirrors Config.TagDetectorBackend on the server.
const BACKENDS = [
  { value: 0, label: 'Napa' },
  { value: 1, label: 'AprilTag (libapriltag)' },
]

const configStore = useConfigStore()
const { draft: draftRef, dirtyFields } = injectDetectorDraft()
const { layouts } = useFieldLayouts()

const draft = computed(() => draftRef.value)
const enabled = computed(() => configStore.config?.enableAprilTagDetector ?? false)
const isDirty = (field: DraftField) => dirtyFields.value.includes(field)

// Resolution x framerate combinations the detector camera supports.
const videoModes = ref<VideoModeModel[]>([])
const resolutionKey = computed(() => draft.value ? `${draft.value.width}x${draft.value.height}` : '')
const resolutions = computed(() => [...new Set(videoModes.value.map(m => `${m.width}x${m.height}`))])
const framerates = computed(() => [...new Set(videoModes.value
  .filter(m => `${m.width}x${m.height}` === resolutionKey.value)
  .map(m => m.framerate))].sort((a, b) => a - b))
const layoutListed = computed(() => layouts.value.some(l => l.fileName === draft.value?.fieldLayoutFile))

function setResolution(event: Event) {
  const d = draft.value
  if (!d) return
  const [width, height] = (event.target as HTMLSelectElement).value.split('x').map(Number)
  d.width = width
  d.height = height
  // Snap the rate to the closest one the new resolution supports.
  const valid = videoModes.value.filter(m => m.width === width && m.height === height).map(m => m.framerate)
  if (valid.length && !valid.includes(d.framerate)) {
    d.framerate = valid.reduce((best, fps) => Math.abs(fps - d.framerate) < Math.abs(best - d.framerate) ? fps : best)
  }
}

function setFramerate(event: Event) {
  if (draft.value) draft.value.framerate = Number((event.target as HTMLSelectElement).value)
}

function setBackend(event: Event) {
  if (draft.value) draft.value.detector = Number((event.target as HTMLSelectElement).value)
}

function setLayout(event: Event) {
  if (draft.value) draft.value.fieldLayoutFile = (event.target as HTMLSelectElement).value
}

async function setEnabled(event: Event) {
  await configStore.updateEnableAprilTagDetector((event.target as HTMLInputElement).checked)
}

onMounted(async () => {
  refreshFieldLayouts()
  try {
    videoModes.value = await videoApi.getAprilTagVideoModes()
  } catch (error) {
    console.error('Failed to load AprilTag video modes:', error)
  }
})
</script>
