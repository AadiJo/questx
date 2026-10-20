<template>
  <div v-if="config" class="settings">
    <section class="panel">
      <div class="sect">Robot connection</div>
      <div class="row">
        <label for="team-number">Team number</label>
        <div class="control">
          <input id="team-number" v-model="teamInput" type="number" min="1" max="25599" @keyup.enter="saveTeam" />
          <button class="btn primary" :disabled="!teamChanged" @click="saveTeam">Update</button>
        </div>
      </div>
      <p class="hint">{{ config.debugIpOverride ? 'Override active. Update the team number to clear it.' : `Robot address from team number: ${teamAddress}` }}</p>

      <div class="row">
        <label for="debug-ip">Debug IP override</label>
        <div class="control">
          <input id="debug-ip" v-model="debugIpInput" class="mono" type="text" placeholder="e.g. 10.0.0.2" @keyup.enter="saveDebugIp" />
          <button class="btn primary" :disabled="!debugIpChanged" @click="saveDebugIp">Set</button>
        </div>
      </div>
      <p class="hint">For bench testing. Leave empty to use the team number.</p>

      <div class="row">
        <label for="pose-timeout">Pose reset timeout</label>
        <div class="control">
          <input id="pose-timeout" v-model="timeoutInput" type="number" min="1" @keyup.enter="saveTimeout" />
          <span class="muted">ms</span>
          <button class="btn primary" :disabled="!timeoutChanged" @click="saveTimeout">Set</button>
        </div>
      </div>
      <div class="callout-warn">
        Values above 120 ms can cause unexpected behavior. Missed resets usually come from loop overruns in robot code. Fix those first.
      </div>

      <div class="sect">Headset</div>
      <div class="row">
        <label for="start-on-boot">Start on boot</label>
        <div class="control">
          <label class="switch">
            <input id="start-on-boot" type="checkbox" :checked="config.enableAutoStartOnBoot" @change="setAutoStart" />
            Start QuestX when the headset boots
          </label>
        </div>
      </div>
      <div class="row">
        <label for="debug-logging">Debug logging</label>
        <div class="control">
          <label class="switch">
            <input id="debug-logging" type="checkbox" :checked="config.enableDebugLogging" @change="setDebugLogging" />
            Verbose logs
          </label>
        </div>
      </div>
      <div class="row">
        <span class="label">Application</span>
        <div class="control">
          <ConfirmButton class="btn danger" confirm-text="Click again to restart" @confirm="restartQuestX">Restart QuestX</ConfirmButton>
        </div>
      </div>
    </section>

    <section class="panel">
      <div class="sect">Device</div>
      <dl class="readout info">
        <dt>App</dt><dd>{{ info?.appName ?? '…' }}</dd>
        <dt>Version</dt><dd>{{ info?.version ?? '…' }}</dd>
        <dt>Build date</dt><dd>{{ info?.buildDate ?? '…' }}</dd>
        <dt>Unity</dt><dd>{{ info?.unityVersion ?? '…' }}</dd>
        <dt>Device model</dt><dd>{{ info?.deviceModel ?? '…' }}</dd>
        <dt>Operating system</dt><dd>{{ info?.operatingSystem ?? '…' }}</dd>
        <dt>Headset IP</dt><dd>{{ status?.ipAddress ?? '…' }}</dd>
        <dt>Web server port</dt><dd>{{ info?.serverPort ?? '…' }}</dd>
      </dl>

      <div class="sect">Configuration</div>
      <div class="row">
        <span class="label">Config database</span>
        <div class="control">
          <button class="btn" @click="downloadDatabase">Download</button>
          <label class="btn">
            Upload
            <input type="file" accept=".db" hidden @change="uploadDatabase" />
          </label>
        </div>
      </div>
      <div class="row">
        <span class="label">All settings</span>
        <div class="control">
          <ConfirmButton class="btn danger" confirm-text="Click again to reset" @confirm="resetSettings">Reset to defaults</ConfirmButton>
        </div>
      </div>
    </section>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useConfigStore } from '../stores/config'
import { configApi } from '../api/config'
import { useConnectionState } from '../composables/useConnectionState'
import { useHeadsetStatus } from '../composables/useHeadsetStatus'
import { restartQuestX } from '../composables/useRestart'
import { errorText, toast } from '../composables/useToast'
import type { ServerInfo } from '../types'
import ConfirmButton from './ConfirmButton.vue'

const configStore = useConfigStore()
const { status } = useHeadsetStatus()
const { isConnected } = useConnectionState()
const config = computed(() => configStore.config)

// Text inputs start from the saved value and keep the user's typing until it is saved;
// config polling does not overwrite an edit in progress.
function useEditable<T>(saved: () => T | undefined) {
  const input = ref<T | null>(null)
  const value = computed({
    get: () => input.value ?? saved() ?? null,
    set: next => { input.value = next },
  })
  const changed = computed(() => input.value !== null && input.value !== saved())
  return { value, changed, reset: () => { input.value = null } }
}

const team = useEditable(() => config.value?.teamNumber)
const debugIp = useEditable(() => config.value?.debugIpOverride)
const timeout = useEditable(() => config.value?.allowedPoseResetTimeoutMs)
const teamInput = team.value
const teamChanged = team.changed
const debugIpInput = debugIp.value
const debugIpChanged = debugIp.changed
const timeoutInput = timeout.value
const timeoutChanged = timeout.changed

// FRC convention: team 1234 lives at 10.12.34.2.
const teamAddress = computed(() => {
  const n = config.value?.teamNumber ?? 0
  return `10.${Math.floor(n / 100)}.${n % 100}.2`
})

async function report(action: Promise<boolean>, success: string, failure: string) {
  if (await action) toast(success)
  else toast(errorText(failure, configStore.error), 'error')
}

async function saveTeam() {
  const value = Number(teamInput.value)
  if (!teamChanged.value || !Number.isInteger(value) || value < 1 || value > 25599) return
  await report(configStore.updateTeamNumber(value), 'Team number updated', 'Could not update the team number')
  team.reset()
}

async function saveDebugIp() {
  if (!debugIpChanged.value) return
  const value = String(debugIpInput.value ?? '').trim()
  await report(configStore.updateDebugIpOverride(value), value ? 'Debug IP override set' : 'Debug IP override cleared', 'Could not set the override')
  debugIp.reset()
}

async function saveTimeout() {
  const value = Number(timeoutInput.value)
  if (!timeoutChanged.value || !Number.isInteger(value) || value < 1) return
  await report(configStore.updateAllowedPoseResetTimeoutMs(value), 'Pose reset timeout saved', 'Could not save the timeout')
  timeout.reset()
}

async function setAutoStart(event: Event) {
  await configStore.updateEnableAutoStartOnBoot((event.target as HTMLInputElement).checked)
}

async function setDebugLogging(event: Event) {
  await configStore.updateEnableDebugLogging((event.target as HTMLInputElement).checked)
}

async function resetSettings() {
  await report(configStore.resetToDefaults(), 'Settings reset to defaults', 'Reset failed')
}

async function downloadDatabase() {
  try {
    await configApi.downloadDatabase()
  } catch (error) {
    toast(errorText('Download failed', error), 'error')
  }
}

async function uploadDatabase(event: Event) {
  const target = event.target as HTMLInputElement
  const file = target.files?.[0]
  target.value = ''
  if (!file) return
  try {
    const result = await configApi.uploadDatabase(file)
    toast(result.message)
  } catch (error) {
    toast(errorText('Upload failed', error), 'error')
  }
}

// ---- Device info from /api/info ----

const info = ref<ServerInfo | null>(null)
async function loadInfo() {
  try {
    info.value = await configApi.getServerInfo()
  } catch {
    // Shown as "…" until the next reconnect.
  }
}
watch(isConnected, connected => { if (connected) loadInfo() })
onMounted(loadInfo)
</script>

<style scoped>
.settings {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(420px, 1fr));
  gap: 20px;
  align-items: start;
}

.settings .panel {
  padding: 14px 20px 16px;
}

.info dd {
  font-size: 12.5px;
}

@media (max-width: 900px) {
  .settings {
    grid-template-columns: minmax(0, 1fr);
  }
}
</style>
