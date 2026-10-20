<template>
  <div class="three-d-view">
    <div class="viewport-col">
      <div class="toolbar">
        <div class="seg" role="tablist" aria-label="3D view">
          <button
            v-for="option in modeOptions"
            :key="option.value"
            :class="{ active: mode === option.value }"
            role="tab"
            :aria-selected="mode === option.value"
            @click="mode = option.value"
          >
            {{ option.label }}
          </button>
        </div>
        <div v-if="mode === 'field'" class="seg small" aria-label="Camera">
          <button
            v-for="preset in presetOptions"
            :key="preset.value"
            :class="{ active: fieldPreset === preset.value }"
            @click="fieldPreset = preset.value"
          >
            {{ preset.label }}
          </button>
        </div>
        <button class="btn" @click="viewer?.resetCamera()">Reset camera</button>
      </div>

      <div class="viewport-wrap">
        <div ref="viewport" class="viewport"></div>
        <div v-if="overlayMessage" class="viewport-message">{{ overlayMessage }}</div>
        <div class="legend">
          <template v-if="mode === 'field'">
            <span><i :class="['swatch', resolvedAlliance]"></i>VIO aligned to last tag solve</span>
            <span><i class="swatch solve"></i>Raw tag solve</span>
            <span><i class="swatch used"></i>Used</span>
            <span><i class="swatch seen"></i>Seen now</span>
          </template>
          <template v-else>
            <span><i class="swatch used"></i>Used</span>
            <span><i class="swatch ignored"></i>Ignored</span>
            <span><i class="swatch unknown"></i>Not in layout</span>
          </template>
        </div>
        <div class="credit">
          <a href="https://sketchfab.com/3d-models/quest-3s-with-controllers-448519e4ef0b40619592000db3729aaa" target="_blank" rel="noopener">Quest 3S model</a>
          by OG_MATTER,
          <a href="http://creativecommons.org/licenses/by/4.0/" target="_blank" rel="noopener">CC BY 4.0</a>
        </div>
      </div>
    </div>

    <aside class="panel side">
      <template v-if="mode === 'field'">
        <div class="sect">Robot field pose</div>
        <dl v-if="estimate" class="readout">
          <dt>X</dt><dd>{{ estimate.x.toFixed(3) }} m</dd>
          <dt>Y</dt><dd>{{ estimate.y.toFixed(3) }} m</dd>
          <dt>Yaw</dt><dd>{{ degrees(yawOfPose(estimate)).toFixed(1) }}°</dd>
        </dl>
        <p v-else class="empty-line">Waiting for a tag solve{{ scene?.vio && !scene.vio.tracking ? ' (VIO not tracking)' : '' }}</p>

        <div class="sect">VIO drift at last solve</div>
        <dl v-if="correction" class="readout">
          <dt>Position</dt><dd>{{ (correction.meters * 100).toFixed(1) }} cm</dd>
          <dt>Yaw</dt><dd>{{ degrees(correction.yawRadians).toFixed(2) }}°</dd>
          <dt>Over</dt><dd>{{ correction.intervalSeconds.toFixed(2) }} s</dd>
        </dl>
        <p v-else class="empty-line">Needs two solves in the same tracking epoch</p>

        <div class="sect">Last tag solve</div>
        <dl v-if="measurement" class="readout">
          <dt>Age</dt><dd>{{ formatAge(measurement.ageMs) }}</dd>
          <dt>Tags</dt><dd>{{ measurement.tagIds.join(', ') }}</dd>
          <dt>Distance</dt><dd>{{ measurement.averageTagDistanceMeters.toFixed(2) }} m</dd>
          <dt>Ambiguity</dt><dd>{{ measurement.ambiguity.toFixed(3) }}</dd>
          <dt>Reprojection</dt><dd>{{ measurement.reprojectionErrorPixels?.toFixed(2) ?? 'n/a' }} px</dd>
          <dt>Forward / left std dev</dt><dd>{{ measurement.stdDevs.xMeters.toFixed(3) }} / {{ measurement.stdDevs.yMeters.toFixed(3) }} m</dd>
          <dt>Yaw std dev</dt><dd>{{ degrees(measurement.stdDevs.yawRadians).toFixed(1) }}°</dd>
        </dl>
        <p v-else class="empty-line">None yet</p>

        <div class="sect">Robot</div>
        <div class="row">
          <label for="robot-alliance">Alliance</label>
          <div class="control">
            <select id="robot-alliance" v-model="allianceSetting">
              <option value="auto">Driver Station</option>
              <option value="red">Red</option>
              <option value="blue">Blue</option>
            </select>
          </div>
        </div>
        <div class="row">
          <label for="robot-length">Length</label>
          <div class="control"><input id="robot-length" v-model.number="robotSize.length" type="number" min="0.2" max="2" step="0.01" /><span class="muted">m</span></div>
        </div>
        <div class="row">
          <label for="robot-width">Width</label>
          <div class="control"><input id="robot-width" v-model.number="robotSize.width" type="number" min="0.2" max="2" step="0.01" /><span class="muted">m</span></div>
        </div>
        <button class="btn block clear-trail" @click="viewer?.clearTrail()">Clear trail</button>
      </template>

      <template v-else-if="mode === 'headset'">
        <div class="sect">Detected tags</div>
        <table v-if="detectedTags.length" class="tags">
          <thead>
            <tr><th>ID</th><th>Status</th><th>Dist</th><th>Amb</th></tr>
          </thead>
          <tbody>
            <tr v-for="tag in detectedTags" :key="tag.id">
              <td class="mono">{{ tag.id }}</td>
              <td><span :class="['chip', STATUS_CHIP[tag.status]]">{{ statusText[tag.status] }}</span></td>
              <td class="mono">{{ tag.cameraToTag ? distance(tag.cameraToTag).toFixed(2) : 'n/a' }}</td>
              <td :class="['mono', { 'high-ambiguity': tag.ambiguity > 0.2 }]">{{ tag.ambiguity.toFixed(2) }}</td>
            </tr>
          </tbody>
        </table>
        <p v-else class="empty-line">No tags in view</p>

        <template v-if="scene?.detector">
          <div class="sect">Field solve</div>
          <dl class="readout">
            <dt>Outcome</dt><dd class="text">{{ scene.detector.lastOutcome || 'n/a' }}</dd>
            <template v-if="scene.detector.lastReason">
              <dt>Reason</dt><dd class="text">{{ scene.detector.lastReason }}</dd>
            </template>
          </dl>
        </template>
      </template>

      <template v-else>
        <div class="sect">Robot to Quest</div>
        <div class="mount-grid">
          <label v-for="field in translationFields" :key="field.key">
            {{ field.label }}
            <span class="unit">
              <input type="number" step="0.005" min="-2" max="2" :class="{ edited: pending[field.key] !== savedMount[field.key] }"
                :value="pending[field.key]" @input="setPending(field.key, $event)" />
              <span>m</span>
            </span>
          </label>
          <label v-for="field in angleFields" :key="field.key">
            {{ field.label }}
            <span class="unit">
              <input type="number" step="1" min="-180" max="180" :class="{ edited: pending[field.key] !== savedMount[field.key] }"
                :value="pending[field.key]" @input="setPending(field.key, $event)" />
              <span>°</span>
            </span>
          </label>
        </div>
        <div v-if="orientationHint" class="callout-warn mount-warn">
          <TriangleAlert :size="15" :stroke-width="1.8" aria-hidden="true" />
          <div>
            Camera orientation is <b>{{ orientationNames[cameraOrientation] }}</b>, but this mount has the headset
            {{ orientationHint.mounted === null ? 'facing up or down' : mountedDescriptions[orientationHint.mounted] }}.
            Orientation only rotates the stream. Poses use this roll.
            <button class="btn" @click="applySuggestedRoll">Set roll to {{ orientationHint.roll }}°</button>
          </div>
        </div>
        <p v-if="!pendingValid" class="error-text">Translations must be within ±2 m and angles within ±180°.</p>
        <div class="mount-actions">
          <button class="btn" :disabled="!dirty || saving" @click="revertMount">Revert</button>
          <button class="btn primary" :disabled="!dirty || !pendingValid || saving" @click="saveMount">{{ saving ? 'Saving…' : 'Save' }}</button>
        </div>
        <p v-if="dirty" class="empty-line unsaved">Previewing unsaved values</p>
      </template>
    </aside>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref, shallowRef, watch } from 'vue'
import { TriangleAlert } from '@lucide/vue'
import { useConfigStore } from '../stores/config'
import { sceneApi } from '../api/scene'
import { CameraOrientation, type PoseJson, type RobotToQuest, type SceneLayout, type SceneResponse, type SceneTagStatus } from '../types'
import { ORIENTATION_ROLL_DEGREES, isValidRobotToQuest, mountedOrientation, yawOf } from '../three/mountMath'
import { SceneViewer, type FieldCameraPreset, type ViewMode } from '../three/SceneViewer'
import { BUMPER_COLORS } from '../three/robotModel'

const props = defineProps<{ active: boolean }>()

const configStore = useConfigStore()
const viewport = ref<HTMLElement | null>(null)
const viewer = shallowRef<SceneViewer | null>(null)
const scene = shallowRef<SceneResponse | null>(null)
const layout = shallowRef<SceneLayout | null>(null)

const mode = ref<ViewMode>('field')
const fieldPreset = ref<FieldCameraPreset>('perspective')
const modeOptions = [
  { value: 'field' as const, label: 'Field' },
  { value: 'headset' as const, label: 'Headset' },
  { value: 'robot' as const, label: 'Robot' },
]
const presetOptions = [
  { value: 'perspective' as const, label: 'Perspective' },
  { value: 'top' as const, label: 'Top' },
  { value: 'follow' as const, label: 'Follow' },
]

const ROBOT_SIZE_KEY = 'questx.3d.robotSize'
const robotSize = reactive(loadRobotSize())
watch(robotSize, value => localStorage.setItem(ROBOT_SIZE_KEY, JSON.stringify(value)))

function loadRobotSize() {
  try {
    const stored = JSON.parse(localStorage.getItem(ROBOT_SIZE_KEY) ?? '')
    if (Number.isFinite(stored.length) && Number.isFinite(stored.width)) return stored
  } catch { /* use the default */ }
  return { length: 0.85, width: 0.85 }
}

// --- Robot-to-Quest editing -------------------------------------------------------------

type MountKey = keyof RobotToQuest
const translationFields: { key: MountKey; label: string }[] = [
  { key: 'x', label: 'Forward (X)' },
  { key: 'y', label: 'Left (Y)' },
  { key: 'z', label: 'Up (Z)' },
]
const angleFields: { key: MountKey; label: string }[] = [
  { key: 'roll', label: 'Roll' },
  { key: 'pitch', label: 'Pitch' },
  { key: 'yaw', label: 'Yaw' },
]
const zeroMount: RobotToQuest = { x: 0, y: 0, z: 0, roll: 0, pitch: 0, yaw: 0 }
const savedMount = computed<RobotToQuest>(() => configStore.config?.robotToQuest ?? zeroMount)
const pending = reactive<RobotToQuest>({ ...savedMount.value })
const dirty = ref(false)
const saving = ref(false)
const pendingValid = computed(() => isValidRobotToQuest(pending))

// Config polling must not overwrite an edit in progress.
watch(savedMount, value => {
  if (!dirty.value) Object.assign(pending, value)
})

function setPending(key: MountKey, event: Event) {
  const value = (event.target as HTMLInputElement).valueAsNumber
  pending[key] = Number.isFinite(value) ? value : NaN
  dirty.value = (Object.keys(zeroMount) as MountKey[]).some(k => pending[k] !== savedMount.value[k])
}

async function saveMount() {
  saving.value = true
  try {
    if (await configStore.updateRobotToQuest({ ...pending })) dirty.value = false
  } finally {
    saving.value = false
  }
}

function revertMount() {
  Object.assign(pending, savedMount.value)
  dirty.value = false
}

const orientationNames: Record<CameraOrientation, string> = {
  [CameraOrientation.Normal]: 'Normal',
  [CameraOrientation.UpsideDown]: 'Upside-down',
  [CameraOrientation.Left]: 'Left',
  [CameraOrientation.Right]: 'Right',
}
const mountedDescriptions: Record<CameraOrientation, string> = {
  [CameraOrientation.Normal]: 'upright',
  [CameraOrientation.UpsideDown]: 'upside-down',
  [CameraOrientation.Left]: 'rolled left',
  [CameraOrientation.Right]: 'rolled right',
}
const cameraOrientation = computed(() => configStore.config?.cameraOrientation ?? CameraOrientation.Normal)

// The stream orientation is display only, so a rolled headset needs the same roll here.
// Flag a mount whose roll disagrees with how the user said the stream is rotated.
const orientationHint = computed(() => {
  if (!pendingValid.value) return null
  const mounted = mountedOrientation(pending)
  if (mounted === cameraOrientation.value) return null
  return { mounted, roll: ORIENTATION_ROLL_DEGREES[cameraOrientation.value] }
})

function applySuggestedRoll() {
  if (!orientationHint.value) return
  pending.roll = orientationHint.value.roll
  dirty.value = (Object.keys(zeroMount) as MountKey[]).some(k => pending[k] !== savedMount.value[k])
}

// The robot view previews the pending edit; other views draw what the headset applies.
const drawnMount = computed<RobotToQuest>(() =>
  mode.value === 'robot' && pendingValid.value ? { ...pending } : savedMount.value)

// --- Readouts ---------------------------------------------------------------------------

const estimate = computed(() => scene.value?.field.estimate ?? null)
const correction = computed(() => scene.value?.field.lastCorrection ?? null)
const measurement = computed(() => scene.value?.field.measurement ?? null)
const detectedTags = computed(() => [...(scene.value?.detections?.tags ?? [])].sort((a, b) => a.id - b.id))
const statusText: Record<SceneTagStatus, string> = { used: 'Used', ignored: 'Ignored', unknown: 'Not in layout' }
const STATUS_CHIP: Record<SceneTagStatus, string> = { used: 'blue', ignored: 'grey', unknown: 'amber' }


const overlayMessage = computed(() => {
  if (!scene.value) return 'Connecting to headset…'
  if (mode.value === 'field') {
    if (layout.value && !layout.value.loaded) return 'No field layout loaded'
    if (!estimate.value && !measurement.value) return 'No field pose yet. Point the headset at an AprilTag.'
  }
  if (mode.value === 'headset' && scene.value.detector && !scene.value.detector.active) {
    return 'AprilTag detector is disabled'
  }
  return ''
})

const degrees = (radians: number) => radians * 180 / Math.PI
const yawOfPose = (pose: PoseJson) => yawOf({ w: pose.qw, x: pose.qx, y: pose.qy, z: pose.qz })
const distance = (pose: PoseJson) => Math.hypot(pose.x, pose.y, pose.z)
const formatAge = (ms: number) => ms < 1000 ? `${ms.toFixed(0)} ms` : `${(ms / 1000).toFixed(1)} s`

// --- Viewer and polling -----------------------------------------------------------------

// Bumpers follow the Driver Station alliance unless overridden; the number is the team number.
const ALLIANCE_KEY = 'questx.3d.alliance'
type AllianceSetting = 'auto' | 'red' | 'blue'
const allianceSetting = ref<AllianceSetting>(
  (['auto', 'red', 'blue'] as const).find(v => v === localStorage.getItem(ALLIANCE_KEY)) ?? 'auto',
)
watch(allianceSetting, value => localStorage.setItem(ALLIANCE_KEY, value))
const resolvedAlliance = computed<'red' | 'blue'>(() =>
  allianceSetting.value === 'auto' ? scene.value?.alliance ?? 'blue' : allianceSetting.value)
const teamNumber = computed(() => {
  const team = configStore.config?.teamNumber ?? 0
  return team > 0 ? team : null
})

function redraw() {
  viewer.value?.update({
    scene: scene.value,
    layout: layout.value,
    mount: drawnMount.value,
    robotSize,
    robotStyle: { bumperColor: BUMPER_COLORS[resolvedAlliance.value], teamNumber: teamNumber.value },
  })
}

watch(mode, value => {
  viewer.value?.setMode(value)
  redraw()
})
watch(fieldPreset, value => viewer.value?.setFieldPreset(value))
watch([drawnMount, robotSize, resolvedAlliance, teamNumber], redraw, { deep: true })

let polling = false
let pollTimer: number | undefined

async function poll() {
  if (!polling) return
  try {
    scene.value = await sceneApi.getScene()
    if (!layout.value) layout.value = await sceneApi.getLayout()
    redraw()
  } catch {
    // The connection banner reports outages; keep drawing the last scene.
  }
  if (polling) pollTimer = window.setTimeout(poll, 66)
}

function startPolling() {
  if (polling) return
  polling = true
  poll()
}

function stopPolling() {
  polling = false
  window.clearTimeout(pollTimer)
}

watch(() => props.active, active => (active ? startPolling() : stopPolling()))

onMounted(() => {
  if (!viewport.value) return
  try {
    viewer.value = new SceneViewer(viewport.value)
  } catch (error) {
    console.error('WebGL unavailable:', error)
  }
  if (props.active) startPolling()
})

onUnmounted(() => {
  stopPolling()
  viewer.value?.dispose()
  viewer.value = null
})
</script>
<style scoped>
.three-d-view {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 340px;
  gap: 20px;
  align-items: start;
}

.toolbar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 10px;
  margin-bottom: 10px;
}

.viewport-wrap {
  position: relative;
  overflow: hidden;
  height: calc(100vh - 160px);
  min-height: 440px;
  border: 1px solid var(--line);
  border-radius: var(--radius-lg);
  background: #0c0d0f;
}

.viewport {
  position: absolute;
  inset: 0;
}

/* SceneViewer sizes the drawing buffer only; CSS stretches the canvas to the viewport. */
.viewport :deep(canvas) {
  display: block;
  width: 100%;
  height: 100%;
  touch-action: none;
}

.viewport-message {
  position: absolute;
  top: 50%;
  left: 50%;
  transform: translate(-50%, -50%);
  padding: 8px 14px;
  border: 1px solid var(--line);
  border-radius: 8px;
  background: rgba(8, 9, 10, 0.7);
  color: var(--text-2);
  pointer-events: none;
}

.legend {
  position: absolute;
  bottom: 10px;
  left: 10px;
  display: flex;
  flex-wrap: wrap;
  gap: 14px;
  padding: 5px 10px;
  border: 1px solid var(--line);
  border-radius: 7px;
  background: rgba(8, 9, 10, 0.6);
  backdrop-filter: blur(8px);
  color: var(--text-2);
  font-size: 12px;
}

.swatch {
  display: inline-block;
  width: 11px;
  height: 11px;
  margin-right: 6px;
  border-radius: 3px;
  vertical-align: -1px;
}

/* Same colors as SceneViewer's palette and robotModel's BUMPER_COLORS. */
.swatch.red { background: #d21414; }
.swatch.blue { background: #1438d2; }
.swatch.solve { background: #f2c200; }
.swatch.used { background: #6371f0; }
.swatch.seen { background: #e3a33b; }
.swatch.ignored { background: #8a8a8a; }
.swatch.unknown { background: #d0453f; }

.credit {
  position: absolute;
  right: 10px;
  bottom: 8px;
  color: var(--muted);
  font-size: 10.5px;
}

.credit a {
  color: inherit;
}

.side {
  padding: 12px 18px 16px;
}

.side .row {
  grid-template-columns: 90px minmax(0, 1fr);
}

.clear-trail {
  margin-top: 8px;
}

.tags {
  width: 100%;
  border-collapse: collapse;
  font-size: 12.5px;
}

.tags th {
  padding: 4px 6px;
  border-bottom: 1px solid var(--line-strong);
  color: var(--text-2);
  font-weight: 500;
  text-align: left;
}

.tags td {
  padding: 5px 6px;
  border-bottom: 1px solid var(--line);
}

.tags td.high-ambiguity {
  color: var(--amber);
}

.mount-grid {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 10px;
  margin-bottom: 10px;
}

.mount-grid label {
  display: flex;
  flex-direction: column;
  gap: 4px;
  color: var(--text-2);
  font-size: 12px;
  font-weight: 500;
}

.unit {
  display: flex;
  align-items: center;
  gap: 5px;
}

.unit span {
  color: var(--muted);
}

.unit input.edited {
  border-color: rgba(94, 106, 210, 0.8);
}

.mount-warn {
  margin-left: 0;
}

.mount-warn .btn {
  display: flex;
  margin-top: 8px;
  height: 26px;
  font-size: 12px;
}

.mount-actions {
  display: flex;
  gap: 8px;
}

.mount-actions .btn {
  flex: 1;
}

.unsaved {
  margin-top: 8px;
}

@media (max-width: 1100px) {
  .three-d-view {
    grid-template-columns: minmax(0, 1fr);
  }

  .viewport-wrap {
    height: 60vh;
  }
}
</style>
