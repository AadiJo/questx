<template>
  <div class="camera-page">
    <CameraStream :active="active" />

    <section class="panel tune">
      <div class="seg tabs" role="tablist">
        <button
          v-for="tab in TABS"
          :key="tab.id"
          role="tab"
          :aria-selected="activeTab === tab.id"
          :class="{ active: activeTab === tab.id, dirty: tabDirty(tab.id) }"
          @click="activeTab = tab.id"
        >
          {{ tab.label }}
        </button>
      </div>

      <div class="pane">
        <StreamSettings v-show="activeTab === 'stream'" @navigate="emit('navigate', $event)" />
        <DetectorSettings v-show="activeTab === 'detector'" @navigate="emit('navigate', $event)" />
        <FilterSettings v-show="activeTab === 'filters'" />
      </div>

      <!-- Detector and filter edits are staged; stream edits apply immediately. -->
      <footer v-if="showFooter" class="foot">
        <span class="count">{{ dirtyCount ? `${dirtyCount} unsaved change${dirtyCount > 1 ? 's' : ''}` : '' }}</span>
        <button class="btn" :disabled="!dirtyCount || applying" @click="cancel">Cancel</button>
        <button class="btn primary" :disabled="!dirtyCount || applying" @click="apply">Apply</button>
      </footer>
    </section>
  </div>
</template>

<script setup lang="ts">
import { computed, provide, ref } from 'vue'
import { useConfigStore } from '../stores/config'
import { DETECTOR_DRAFT, FILTER_FIELDS, useDetectorDraft } from '../composables/useDetectorDraft'
import type { PageId } from '../types'
import CameraStream from './CameraStream.vue'
import StreamSettings from './StreamSettings.vue'
import DetectorSettings from './DetectorSettings.vue'
import FilterSettings from './FilterSettings.vue'

defineProps<{ active: boolean }>()
const emit = defineEmits<{ navigate: [page: PageId] }>()

type TabId = 'stream' | 'detector' | 'filters'
const TABS: { id: TabId; label: string }[] = [
  { id: 'stream', label: 'Stream' },
  { id: 'detector', label: 'Detector' },
  { id: 'filters', label: 'Filtering' },
]

const configStore = useConfigStore()
const detectorDraft = useDetectorDraft()
provide(DETECTOR_DRAFT, detectorDraft)
const { dirtyFields, applying, apply, cancel } = detectorDraft

const activeTab = ref<TabId>('stream')
const dirtyCount = computed(() => dirtyFields.value.length)

function tabDirty(tab: TabId) {
  if (tab === 'stream') return false
  return dirtyFields.value.some(field => FILTER_FIELDS.includes(field) === (tab === 'filters'))
}

const showFooter = computed(() =>
  dirtyCount.value > 0 || (activeTab.value !== 'stream' && (configStore.config?.enableAprilTagDetector ?? false)))
</script>

<style scoped>
.camera-page {
  display: grid;
  grid-template-columns: minmax(0, 1.45fr) minmax(360px, 1fr);
  gap: 20px;
  align-items: start;
}

.tune {
  overflow: hidden;
}

.tabs {
  display: flex;
  margin: 14px 16px 0;
}

.tabs > button {
  flex: 1;
}

.tabs > button.dirty::after {
  content: '\25CF';
  margin-top: -6px;
  color: var(--blue-soft);
  font-size: 8px;
}

.pane {
  padding: 8px 18px 6px;
}

.foot {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 12px 18px;
  border-top: 1px solid var(--line);
  background: rgba(255, 255, 255, 0.015);
}

.count {
  flex: 1;
  color: var(--text-2);
  font-size: 12.5px;
}

@media (max-width: 1100px) {
  .camera-page {
    grid-template-columns: minmax(0, 1fr);
  }
}
</style>
