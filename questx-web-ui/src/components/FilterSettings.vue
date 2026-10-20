<template>
  <div>
    <template v-if="enabled && draft">
      <div :class="['row', { dirty: isDirty('maxDistance') }]">
        <label for="filter-distance">Max tag distance</label>
        <div class="control">
          <input id="filter-distance" type="range" min="0.5" max="10" step="0.1" :value="draft.maxDistance" @input="setDistance" />
          <input class="number" type="number" min="0.5" max="10" step="0.1" :value="draft.maxDistance" aria-label="Max tag distance in meters" @change="setDistance" />
          <span class="muted">m</span>
        </div>
      </div>
      <div :class="['row', { dirty: isDirty('minimumNumberOfTags') }]">
        <label for="filter-min-tags">Minimum tags</label>
        <div class="control">
          <select id="filter-min-tags" :value="draft.minimumNumberOfTags" @change="setMinimumTags">
            <option v-for="n in MIN_TAGS_OPTIONS" :key="n" :value="n">{{ n }}</option>
          </select>
        </div>
      </div>
      <div :class="['row', { dirty: isDirty('ignoredIds') }]">
        <label for="filter-ignored">Ignored tag IDs</label>
        <div class="control">
          <input id="filter-ignored" v-model="ignoredText" class="mono" type="text" placeholder="e.g. 16, 17" @input="setIgnored" />
          <button class="btn" :disabled="draft.ignoredIds.length === 0" @click="clearIgnored">Clear</button>
        </div>
      </div>
    </template>

    <div v-else class="empty-pane">
      <b>Detector is off</b>
      Filtering applies to AprilTag measurements only.
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useConfigStore } from '../stores/config'
import { injectDetectorDraft, type DraftField } from '../composables/useDetectorDraft'

// Mirrors QuestXConstants.AprilTag.MINIMUM_TAGS_OPTIONS; the server rejects other values.
const MIN_TAGS_OPTIONS = [1, 2, 3, 4]
// FRC tag36h11 IDs in current use never exceed ~40; clamp to 50 for headroom.
const MAX_TAG_ID = 50

const configStore = useConfigStore()
const { draft: draftRef, dirtyFields } = injectDetectorDraft()

const draft = computed(() => draftRef.value)
const enabled = computed(() => configStore.config?.enableAprilTagDetector ?? false)
const isDirty = (field: DraftField) => dirtyFields.value.includes(field)

function setDistance(event: Event) {
  const value = Number((event.target as HTMLInputElement).value)
  if (draft.value && Number.isFinite(value)) draft.value.maxDistance = Math.min(10, Math.max(0.5, value))
}

function setMinimumTags(event: Event) {
  const value = Number((event.target as HTMLSelectElement).value)
  if (draft.value && MIN_TAGS_OPTIONS.includes(value)) draft.value.minimumNumberOfTags = value
}

// The text box keeps what the user typed ("16, " mid-edit); the draft holds the parsed,
// de-duplicated IDs clamped to [0, MAX_TAG_ID].
const ignoredText = ref(draft.value?.ignoredIds.join(', ') ?? '')
const parseIds = (text: string) => [...new Set(text.split(',')
  .map(part => parseInt(part.trim()))
  .filter(id => Number.isInteger(id) && id >= 0 && id <= MAX_TAG_ID))]

watch(() => draft.value?.ignoredIds, ids => {
  if (ids && JSON.stringify(ids) !== JSON.stringify(parseIds(ignoredText.value))) ignoredText.value = ids.join(', ')
}, { deep: true })

function setIgnored() {
  if (draft.value) draft.value.ignoredIds = parseIds(ignoredText.value)
}

function clearIgnored() {
  ignoredText.value = ''
  if (draft.value) draft.value.ignoredIds = []
}
</script>
