<template>
  <div class="field-page">
    <section>
      <div class="sect">Layouts</div>
      <div v-if="!loaded" class="empty-line">Loading layouts…</div>
      <div v-for="layout in layouts" :key="layout.fileName" :class="['layout', { running: layout.fileName === runningLayoutFile }]">
        <template v-if="renaming === layout.fileName">
          <input v-model="renameTo" class="mono" type="text" maxlength="64" aria-label="New name" @keydown.enter="performRename(layout)" @keydown.esc="renaming = null" />
          <button class="btn" @click="renaming = null">Cancel</button>
          <button class="btn primary" :disabled="busy" @click="performRename(layout)">Rename</button>
        </template>
        <template v-else>
          <div class="info">
            <div class="name">{{ layout.displayName }}</div>
            <div class="meta">
              <span :class="['chip', layout.source === 'custom' ? 'blue' : '']">{{ layout.source }}</span>
              <span v-if="layout.fileName === runningLayoutFile" class="chip blue">in use</span>
              <span v-else-if="layout.fileName === savedLayoutFile" class="chip amber">loads on restart</span>
              {{ layout.tagCount }} tags · <span class="mono">{{ layout.fileName }}</span>
            </div>
          </div>
          <template v-if="layout.source === 'custom'">
            <button class="btn" @click="beginEdit(layout)">Edit</button>
            <button class="btn" @click="beginRename(layout)">Rename</button>
            <ConfirmButton class="btn danger" confirm-text="Delete?" @confirm="remove(layout)">Delete</ConfirmButton>
          </template>
        </template>
      </div>
    </section>

    <section class="panel editor">
      <div class="sect">{{ editing ? `Edit ${editing.fileName}` : 'New custom layout' }}</div>
      <div class="row">
        <label for="layout-name">Name</label>
        <div class="control">
          <input id="layout-name" v-model="formName" class="mono" type="text" maxlength="64" placeholder="team-1234-practice" :disabled="!!editing" />
        </div>
      </div>
      <p class="hint">Letters, numbers, '.', '_' and '-'. The server adds ".json".</p>
      <textarea v-model="formContent" rows="18" spellcheck="false" aria-label="Layout JSON" placeholder='{"tags": [...], "field": {"length": 16.54, "width": 8.07}}' />
      <p v-if="formError" class="error-text">{{ formError }}</p>
      <div class="actions">
        <button v-if="editing" class="btn" @click="resetForm">Cancel edit</button>
        <button class="btn primary" :disabled="busy" @click="save">{{ editing ? 'Save changes' : 'Create layout' }}</button>
      </div>
    </section>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { configApi } from '../api/config'
import { useConfigStore } from '../stores/config'
import { refreshFieldLayouts, useFieldLayouts } from '../composables/useFieldLayouts'
import { useRunningLayout } from '../composables/useRunningLayout'
import { errorText, toast } from '../composables/useToast'
import type { AprilTagFieldLayoutEntry } from '../types'
import ConfirmButton from './ConfirmButton.vue'

const configStore = useConfigStore()
const { layouts, loaded } = useFieldLayouts()
const { runningLayoutFile } = useRunningLayout()
const savedLayoutFile = computed(() => configStore.config?.aprilTagDetectorMode.fieldLayoutFile ?? null)

const busy = ref(false)
const editing = ref<AprilTagFieldLayoutEntry | null>(null)
const formName = ref('')
const formContent = ref('')
const formError = ref<string | null>(null)
const renaming = ref<string | null>(null)
const renameTo = ref('')

const baseName = (fileName: string) => fileName.replace(/\.json$/, '')

function resetForm() {
  editing.value = null
  formName.value = ''
  formContent.value = ''
  formError.value = null
}

async function beginEdit(layout: AprilTagFieldLayoutEntry) {
  formError.value = null
  try {
    formContent.value = await configApi.getAprilTagFieldLayoutContent(layout.fileName)
    editing.value = layout
    formName.value = baseName(layout.fileName)
  } catch (error) {
    toast(errorText(`Could not open ${layout.fileName}`, error), 'error')
  }
}

async function save() {
  formError.value = null
  if (!formName.value.trim()) {
    formError.value = 'Name is required.'
    return
  }
  if (!formContent.value.trim()) {
    formError.value = 'JSON content is required.'
    return
  }
  busy.value = true
  try {
    const result = await configApi.saveCustomAprilTagFieldLayout(formName.value, formContent.value)
    toast(`Saved ${result.fileName} (${result.tagCount} tags)`)
    // A new layout clears the form for the next one; an edit stays open for iteration.
    if (!editing.value) resetForm()
    await refreshFieldLayouts()
  } catch (error) {
    formError.value = error instanceof Error ? error.message : String(error)
  } finally {
    busy.value = false
  }
}

function beginRename(layout: AprilTagFieldLayoutEntry) {
  renaming.value = layout.fileName
  renameTo.value = baseName(layout.fileName)
}

async function performRename(layout: AprilTagFieldLayoutEntry) {
  if (!renameTo.value.trim()) return
  busy.value = true
  try {
    const result = await configApi.renameCustomAprilTagFieldLayout(layout.fileName, renameTo.value)
    toast(`Renamed to ${result.newFileName}`)
    renaming.value = null
    if (editing.value?.fileName === layout.fileName) resetForm()
    await Promise.all([refreshFieldLayouts(), configStore.loadConfig(false)])
  } catch (error) {
    toast(errorText('Rename failed', error), 'error')
  } finally {
    busy.value = false
  }
}

async function remove(layout: AprilTagFieldLayoutEntry) {
  try {
    const result = await configApi.deleteCustomAprilTagFieldLayout(layout.fileName)
    toast(result.fellBackTo
      ? `Deleted ${result.deletedFileName}. Falling back to ${result.fellBackTo}`
      : `Deleted ${result.deletedFileName}`)
    if (editing.value?.fileName === layout.fileName) resetForm()
    await Promise.all([refreshFieldLayouts(), configStore.loadConfig(false)])
  } catch (error) {
    toast(errorText('Delete failed', error), 'error')
  }
}

onMounted(refreshFieldLayouts)
</script>

<style scoped>
.field-page {
  display: grid;
  grid-template-columns: minmax(320px, 460px) minmax(0, 1fr);
  gap: 24px;
  align-items: start;
}

.layout {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 6px;
  padding: 10px 12px;
  border: 1px solid var(--line);
  border-radius: 10px;
  background: var(--panel);
}

.layout.running {
  border-color: rgba(94, 106, 210, 0.5);
  box-shadow: 0 0 0 3px rgba(94, 106, 210, 0.1);
}

.info {
  flex: 1;
  min-width: 0;
}

.name {
  font-weight: 600;
}

.meta {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
  color: var(--text-2);
  font-size: 12px;
}

.layout .btn {
  height: 26px;
  padding: 0 9px;
  font-size: 12px;
}

.editor {
  padding: 14px 18px 18px;
}

.editor textarea {
  min-height: 360px;
  margin-top: 6px;
}

.actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
  margin-top: 12px;
}

@media (max-width: 1100px) {
  .field-page {
    grid-template-columns: minmax(0, 1fr);
  }
}
</style>
