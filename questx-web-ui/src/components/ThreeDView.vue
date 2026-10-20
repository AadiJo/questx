<template>
  <div class="three-d-view">
    <div class="view-toolbar">
      <div class="segmented" role="tablist" aria-label="3D view">
        <button
          v-for="option in modeOptions"
          :key="option.value"
          :class="['segment', { active: mode === option.value }]"
          role="tab"
          :aria-selected="mode === option.value"
          @click="mode = option.value"
        >
          <component :is="option.icon" :size="15" :stroke-width="1.75" aria-hidden="true" />
          {{ option.label }}
        </button>
      </div>
      <div v-if="mode === 'field'" class="segmented small" aria-label="Camera">
        <button
          v-for="preset in presetOptions"
          :key="preset.value"
          :class="['segment', { active: fieldPreset === preset.value }]"
          @click="fieldPreset = preset.value"
        >
          {{ preset.label }}
        </button>
      </div>
      <button class="secondary reset-camera" @click="viewer?.resetCamera()">
        <RotateCcw :size="15" :stroke-width="1.75" aria-hidden="true" />
        Reset Camera
      </button>
      <span :class="['live-status', liveStatus.tone]">
        <span class="status-dot"></span>
        {{ liveStatus.text }}
      </span>
    </div>

    <div class="view-body">
      <div class="viewport-wrap">
        <div ref="viewport" class="viewport"></div>
        <div v-if="overlayMessage" class="viewport-message">{{ overlayMessage }}</div>
        <div class="viewport-legend">
          <template v-if="mode === 'field'">
            <span><i :class="['swatch', 'bumper', resolvedAlliance]"></i>VIO aligned to last tag solve</span>
            <span><i class="swatch bumper solve"></i>Raw tag solve</span>
            <span><i class="swatch used"></i>Used</span>
            <span><i class="swatch seen"></i>Seen now</span>
          </template>
          <template v-else>
            <span><i class="swatch used"></i>Used</span>
            <span><i class="swatch ignored"></i>Ignored</span>
            <span><i class="swatch unknown"></i>Not in layout</span>
          </template>
        </div>
        <div class="viewport-credit">
          <a href="https://sketchfab.com/3d-models/quest-3s-with-controllers-448519e4ef0b40619592000db3729aaa" target="_blank" rel="noopener">Quest 3S model</a>
          by OG_MATTER,
          <a href="http://creativecommons.org/licenses/by/4.0/" target="_blank" rel="noopener">CC BY 4.0</a>
        </div>
      </div>

      <aside class="side-panel">
        <!-- Field view readouts -->
        <template v-if="mode === 'field'">
          <section class="panel-section">
            <h4>Robot Field Pose</h4>
            <p class="panel-note">VIO carried into the field by the last tag solve. Display only; robot code does its own fusion.</p>
            <dl v-if="estimate" class="readout">
              <dt>X</dt><dd class="mono">{{ estimate.x.toFixed(3) }} m</dd>
              <dt>Y</dt><dd class="mono">{{ estimate.y.toFixed(3) }} m</dd>
              <dt>Yaw</dt><dd class="mono">{{ degrees(yawOfPose(estimate)).toFixed(1) }}°</dd>
            </dl>
            <p v-else class="panel-empty">Waiting for a tag solve{{ scene?.vio && !scene.vio.tracking ? ' (VIO not tracking)' : '' }}</p>
          </section>

          <section class="panel-section">
            <h4>VIO Drift at Last Solve</h4>
            <dl v-if="correction" class="readout">
              <dt>Position</dt><dd class="mono">{{ (correction.meters * 100).toFixed(1) }} cm</dd>
              <dt>Yaw</dt><dd class="mono">{{ degrees(correction.yawRadians).toFixed(2) }}°</dd>
              <dt>Over</dt><dd class="mono">{{ correction.intervalSeconds.toFixed(2) }} s</dd>
            </dl>
            <p v-else class="panel-empty">Needs two solves in the same tracking epoch</p>
          </section>

          <section class="panel-section">
            <h4>Last Tag Solve</h4>
            <dl v-if="measurement" class="readout">
              <dt>Age</dt><dd class="mono">{{ formatAge(measurement.ageMs) }}</dd>
              <dt>Tags</dt><dd class="mono">{{ measurement.tagIds.join(', ') }}</dd>
              <dt>Distance</dt><dd class="mono">{{ measurement.averageTagDistanceMeters.toFixed(2) }} m</dd>
              <dt>Ambiguity</dt><dd class="mono">{{ measurement.ambiguity.toFixed(3) }}</dd>
              <dt>Reprojection</dt><dd class="mono">{{ measurement.reprojectionErrorPixels?.toFixed(2) ?? '—' }} px</dd>
              <dt>XY Std Dev</dt><dd class="mono">{{ measurement.stdDevs.xMeters.toFixed(3) }} m</dd>
              <dt>Yaw Std Dev</dt><dd class="mono">{{ degrees(measurement.stdDevs.yawRadians).toFixed(1) }}°</dd>
            </dl>
            <p v-else class="panel-empty">None yet</p>
          </section>

          <section class="panel-section">
            <h4>Robot</h4>
            <div class="size-inputs">
              <label>Alliance
                <select v-model="allianceSetting">
                  <option value="auto">Driver Station</option>
                  <option value="red">Red</option>
                  <option value="blue">Blue</option>
                </select>
              </label>
              <p v-if="allianceSetting === 'auto' && !scene?.alliance" class="panel-note">
                No alliance from the Driver Station yet; showing blue.
              </p>
              <label>Length <input type="number" min="0.2" max="2" step="0.01" v-model.number="robotSize.length" /> m</label>
              <label>Width <input type="number" min="0.2" max="2" step="0.01" v-model.number="robotSize.width" /> m</label>
            </div>
            <button class="secondary" @click="viewer?.clearTrail()">Clear Trail</button>
          </section>
        </template>

        <!-- Headset view readouts -->
        <template v-else-if="mode === 'headset'">
          <section class="panel-section">
            <h4>Detected Tags</h4>
            <p class="panel-note">Each tag solved on its own in the camera frame, so ignored and rejected tags show too. Drawn level with gravity around the headset.</p>
            <table v-if="detectedTags.length" class="tag-table">
              <thead>
                <tr><th>ID</th><th>Status</th><th>Dist</th><th>Amb</th></tr>
              </thead>
              <tbody>
                <tr v-for="tag in detectedTags" :key="tag.id">
                  <td class="mono">{{ tag.id }}</td>
                  <td><span :class="['status-chip', tag.status]">{{ statusText[tag.status] }}</span></td>
                  <td class="mono">{{ tag.cameraToTag ? distance(tag.cameraToTag).toFixed(2) : '—' }}</td>
                  <td :class="['mono', { warn: tag.ambiguity > 0.2 }]">{{ tag.ambiguity.toFixed(2) }}</td>
                </tr>
              </tbody>
            </table>
            <p v-else class="panel-empty">No tags in view</p>
          </section>
          <section v-if="scene?.detector" class="panel-section">
            <h4>Field Solve</h4>
            <dl class="readout">
              <dt>Outcome</dt><dd>{{ scene.detector.lastOutcome }}</dd>
              <template v-if="scene.detector.lastReason">
                <dt>Reason</dt><dd>{{ scene.detector.lastReason }}</dd>
              </template>
            </dl>
          </section>
        </template>

        <!-- Robot view: robot-to-Quest editor -->
        <template v-else>
          <section class="panel-section">
            <h4>Robot to Quest</h4>
            <p class="panel-note">
              Where the headset's center eye sits on the robot, in robot axes (X forward, Y left, Z up).
              Every published pose is the robot's pose, so remove <code>ROBOT_TO_QUEST</code> from robot code.
            </p>
            <div class="mount-grid">
              <label v-for="field in translationFields" :key="field.key">
                {{ field.label }}
                <span class="unit-input">
                  <input type="number" step="0.005" min="-2" max="2" :value="pending[field.key]"
                    @input="setPending(field.key, $event)" />
                  <span>m</span>
                </span>
              </label>
              <label v-for="field in angleFields" :key="field.key">
                {{ field.label }}
                <span class="unit-input">
                  <input type="number" step="1" min="-180" max="180" :value="pending[field.key]"
                    @input="setPending(field.key, $event)" />
                  <span>°</span>
                </span>
              </label>
            </div>
            <div v-if="orientationHint" class="orientation-hint">
              <TriangleAlert :size="16" :stroke-width="1.75" aria-hidden="true" />
              <span>
                Camera orientation is <strong>{{ orientationNames[cameraOrientation] }}</strong>, but this mount has the headset
                {{ orientationHint.mounted === null ? 'facing up or down' : mountedDescriptions[orientationHint.mounted] }}.
                Orientation only rotates the stream; poses use this roll.
              </span>
              <button class="secondary" @click="applySuggestedRoll">Set roll to {{ orientationHint.roll }}°</button>
            </div>
            <p v-if="!pendingValid" class="error-text">Translations must be within ±2 m and angles within ±180°.</p>
            <div class="mount-actions">
              <button :disabled="!dirty || !pendingValid || saving" @click="saveMount">
                {{ saving ? 'Saving…' : 'Save' }}
              </button>
              <button class="secondary" :disabled="!dirty || saving" @click="revertMount">Revert</button>
              <span v-if="dirty" class="unsaved">Previewing unsaved values</span>
            </div>
          </section>
        </template>
      </aside>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref, shallowRef, watch } from 'vue'
import { Crosshair, Map as MapIcon, RotateCcw, ScanEye, TriangleAlert } from '@lucide/vue'
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
const connected = ref(true)

const mode = ref<ViewMode>('field')
const fieldPreset = ref<FieldCameraPreset>('perspective')
const modeOptions = [
  { value: 'field' as const, label: 'Field', icon: MapIcon },
  { value: 'headset' as const, label: 'Headset', icon: ScanEye },
  { value: 'robot' as const, label: 'Robot', icon: Crosshair },
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
const statusText: Record<SceneTagStatus, string> = { used: 'Used', ignored: 'Ignored', unknown: 'Unknown' }

const liveStatus = computed(() => {
  if (!connected.value) return { text: 'Disconnected', tone: 'error' }
  if (scene.value?.detector && !scene.value.detector.active) return { text: 'Detector Off', tone: 'warning' }
  if (!scene.value?.vio?.tracking) return { text: 'Not Tracking', tone: 'warning' }
  return { text: 'Live', tone: 'success' }
})

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
    connected.value = true
    if (!layout.value) layout.value = await sceneApi.getLayout()
    redraw()
  } catch {
    connected.value = false
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
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.view-toolbar {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  flex-wrap: wrap;
}

.segmented {
  display: inline-flex;
  border: 1px solid var(--border-strong);
  border-radius: 4px;
  overflow: hidden;
}

.segment {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
  padding: 0.5rem 0.9rem;
  background: transparent;
  border: none;
  border-right: 1px solid var(--border-color);
  border-radius: 0;
  color: var(--text-secondary);
  font-size: 0.8125rem;
  font-weight: 600;
  cursor: pointer;
}

.segment:last-child {
  border-right: none;
}

.segment:hover {
  background: var(--bg-tertiary);
  color: var(--text-primary);
}

.segment.active {
  background: var(--primary-color);
  color: var(--bg-color);
}

.segmented.small .segment {
  padding: 0.4rem 0.7rem;
  font-size: 0.75rem;
}

.reset-camera {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
}

.live-status {
  margin-left: auto;
  display: inline-flex;
  align-items: center;
  gap: 0.45rem;
  font-size: 0.75rem;
  font-weight: 650;
  letter-spacing: 0.06em;
  text-transform: uppercase;
  color: var(--text-secondary);
}

.status-dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: currentColor;
}

.live-status.success { color: var(--success-color); }
.live-status.warning { color: var(--warning-color); }
.live-status.error { color: var(--danger-color); }

.view-body {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 320px;
  gap: 1rem;
  align-items: start;
}

.viewport-wrap {
  position: relative;
  height: clamp(460px, 64vh, 680px);
  border: 1px solid var(--border-color);
  border-radius: 4px;
  overflow: hidden;
  background: #1b1b1b;
}

.viewport {
  position: absolute;
  inset: 0;
}

.viewport :deep(canvas) {
  display: block;
  width: 100%;
  height: 100%;
  touch-action: none;
}

.viewport-message {
  position: absolute;
  top: 1rem;
  left: 50%;
  transform: translateX(-50%);
  padding: 0.5rem 0.9rem;
  background: rgba(18, 18, 18, 0.88);
  border: 1px solid #3a3a3a;
  border-radius: 4px;
  font-size: 0.8125rem;
  color: #d6d6d6;
  pointer-events: none;
}

.viewport-legend {
  position: absolute;
  left: 0.75rem;
  bottom: 0.75rem;
  display: flex;
  flex-wrap: wrap;
  gap: 0.35rem 0.9rem;
  padding: 0.4rem 0.7rem;
  background: rgba(18, 18, 18, 0.88);
  border: 1px solid #3a3a3a;
  border-radius: 4px;
  font-size: 0.75rem;
  color: #d6d6d6;
  pointer-events: none;
}

.viewport-credit {
  position: absolute;
  right: 0.75rem;
  bottom: 0.75rem;
  font-size: 0.6875rem;
  color: #8a8a8a;
}

.viewport-credit a {
  color: inherit;
}

.viewport-legend span {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
}

.swatch {
  display: inline-block;
  width: 10px;
  height: 10px;
  border-radius: 2px;
}

.swatch.bumper.red { background: #d21414; }
.swatch.bumper.blue { background: #1438d2; }
.swatch.bumper.solve { background: #f2c200; }
.swatch.used { background: #3fae6b; }
.swatch.seen { background: #e3a33b; }
.swatch.ignored { background: #8a8a8a; }
.swatch.unknown { background: #d0453f; }

.side-panel {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.panel-section {
  padding: 1rem 1.1rem;
  background: var(--card-bg);
  border: 1px solid var(--border-color);
  border-radius: 4px;
}

.panel-section h4 {
  font-size: 1.05rem;
  margin-bottom: 0.5rem;
}

.panel-note {
  font-size: 0.78rem;
  color: var(--text-secondary);
  margin-bottom: 0.75rem;
}

.panel-empty {
  font-size: 0.8125rem;
  color: var(--text-muted);
}

.readout {
  display: grid;
  grid-template-columns: auto 1fr;
  gap: 0.35rem 1rem;
  font-size: 0.8125rem;
}

.readout dt {
  color: var(--text-secondary);
}

.readout dd {
  text-align: right;
  font-weight: 550;
  overflow-wrap: anywhere;
}

.size-inputs {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
  margin-bottom: 0.75rem;
  font-size: 0.8125rem;
}

.size-inputs label {
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.size-inputs input,
.size-inputs select {
  width: 8.5rem;
  margin-left: auto;
  padding: 0.35rem 0.5rem;
}

.tag-table {
  width: 100%;
  border-collapse: collapse;
  font-size: 0.8125rem;
}

.tag-table th {
  text-align: left;
  font-weight: 600;
  color: var(--text-secondary);
  padding: 0.3rem 0.25rem;
  border-bottom: 1px solid var(--border-color);
}

.tag-table td {
  padding: 0.35rem 0.25rem;
  border-bottom: 1px solid var(--border-color);
}

.tag-table td.warn {
  color: var(--warning-color);
}

.status-chip {
  font-size: 0.7rem;
  font-weight: 650;
  padding: 0.1rem 0.4rem;
  border-radius: 3px;
  border: 1px solid currentColor;
}

.status-chip.used { color: #3fae6b; }
.status-chip.ignored { color: #8a8a8a; }
.status-chip.unknown { color: #d0453f; }

.mount-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 0.6rem 0.75rem;
  font-size: 0.78rem;
  color: var(--text-secondary);
}

.mount-grid label {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
}

.unit-input {
  display: flex;
  align-items: center;
  gap: 0.35rem;
}

.unit-input input {
  padding: 0.4rem 0.5rem;
}

.unit-input span {
  min-width: 1rem;
  color: var(--text-muted);
}

.orientation-hint {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-start;
  gap: 0.5rem;
  margin-top: 0.9rem;
  padding: 0.7rem;
  border: 1px solid var(--warning-color);
  border-radius: 4px;
  font-size: 0.78rem;
  color: var(--text-primary);
}

.orientation-hint > svg {
  color: var(--warning-color);
  flex-shrink: 0;
  margin-top: 0.1rem;
}

.orientation-hint span {
  flex: 1;
  min-width: 12rem;
}

.error-text {
  margin-top: 0.75rem;
  font-size: 0.78rem;
  color: var(--danger-color);
}

.mount-actions {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  margin-top: 1rem;
  flex-wrap: wrap;
}

.unsaved {
  font-size: 0.75rem;
  color: var(--warning-color);
}

code {
  font-size: 0.85em;
  padding: 0.05rem 0.25rem;
  background: var(--code-bg);
  border-radius: 3px;
}

@media (max-width: 960px) {
  .view-body {
    grid-template-columns: 1fr;
  }

  .viewport-wrap {
    height: 420px;
  }
}
</style>
