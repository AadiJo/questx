<template>
  <div v-if="!status" class="empty-pane"><b>Waiting for status</b>The headset has not reported yet.</div>
  <div v-else class="status-grid">
    <section class="panel card">
      <h3>Network</h3>
      <dl class="readout">
        <dt>Robot connection</dt>
        <dd :class="['text', status.networkConnected ? 'good' : 'bad']">{{ status.networkConnected ? 'Connected' : 'Disconnected' }}</dd>
        <dt>Robot IP</dt>
        <dd>{{ status.robotIpAddress || 'n/a' }}<span v-if="debugIpActive" class="chip amber override">override</span></dd>
        <dt>Headset IP</dt>
        <dd>{{ status.ipAddress }}</dd>
        <dt>Team number</dt>
        <dd>{{ status.teamNumber }}</dd>
        <dt>Web clients</dt>
        <dd>{{ status.connectedClients }}</dd>
      </dl>
    </section>

    <section class="panel card">
      <h3>Local VIO pose</h3>
      <div class="big">
        <b>{{ signed(status.position.x, 2) }}, {{ signed(status.position.y, 2) }}</b>
        <span>x, y (m)</span>
      </div>
      <!-- Labels match the previous dashboard: StatusProvider fills pitch from euler z
           and yaw from euler y, and the old UI showed them swapped like this. -->
      <dl class="readout">
        <dt>z</dt><dd>{{ signed(status.position.z, 3) }} m</dd>
        <dt>Yaw</dt><dd>{{ signed(status.eulerAngles.pitch, 1) }}°</dd>
        <dt>Pitch</dt><dd>{{ signed(status.eulerAngles.yaw, 1) }}°</dd>
        <dt>Roll</dt><dd>{{ signed(status.eulerAngles.roll, 1) }}°</dd>
      </dl>
    </section>

    <section class="panel card">
      <h3>Tracking</h3>
      <dl class="readout">
        <dt>Status</dt>
        <dd :class="['text', status.isTracking ? 'good' : 'bad']">{{ status.isTracking ? 'Tracking' : 'Lost' }}</dd>
        <dt>Tracking lost events</dt><dd>{{ status.trackingLostEvents }}</dd>
        <dt>Tracking epoch</dt><dd>{{ status.trackingEpoch }}</dd>
        <dt>VIO published</dt><dd>{{ status.vioPublishCount.toLocaleString() }}</dd>
      </dl>
    </section>

    <section class="panel card">
      <h3>AprilTag measurements</h3>
      <dl class="readout">
        <dt>Detector</dt>
        <dd :class="['text', tag.detectorActive ? 'good' : 'bad']">{{ tag.detectorActive ? 'Active' : 'Disabled' }}</dd>
        <template v-if="tag.detector">
          <dt>Detect time</dt>
          <dd :title="`Last frame ${(tag.lastDetectMs ?? 0).toFixed(1)} ms`">{{ tag.detector }} · {{ (tag.averageDetectMs ?? 0).toFixed(1) }} ms avg</dd>
          <dt>Detection rate</dt>
          <dd :title="`Frames detected of camera frames delivered; ${(tag.busyFrameCount ?? 0).toLocaleString()} dropped while detection was busy`">{{ (tag.processedFps ?? 0).toFixed(0) }} / {{ (tag.cameraFps ?? 0).toFixed(0) }} fps</dd>
          <dt>Publish rate</dt><dd>{{ (tag.measurementFps ?? 0).toFixed(0) }}/s</dd>
          <template v-if="tag.captureBackend === 'Native'">
            <dt>Camera</dt>
            <dd title="QuestX's own camera client: grayscale frames, no GPU readback. Delivery is exposure to frame arrival.">Native · {{ (tag.averageDeliveryMs ?? 0).toFixed(1) }} ms delivery</dd>
          </template>
          <template v-else>
            <dt>Readback</dt><dd>{{ (tag.averageReadbackMs ?? 0).toFixed(1) }} ms</dd>
          </template>
          <dt>Solve</dt><dd>{{ (tag.averageSolveMs ?? 0).toFixed(1) }} ms</dd>
          <template v-if="tag.averagePreviewMs">
            <dt>Preview encode</dt><dd>{{ tag.averagePreviewMs.toFixed(1) }} ms</dd>
          </template>
          <dt>Latency</dt>
          <dd title="Camera exposure to measurement publish">{{ (tag.averageLatencyMs ?? 0).toFixed(0) }} ms</dd>
        </template>
        <dt>Last outcome</dt><dd class="text">{{ tag.lastOutcome || 'n/a' }}</dd>
        <template v-if="tag.lastReason">
          <dt>Reason</dt><dd class="text">{{ tag.lastReason }}</dd>
        </template>
        <dt>Measurement age</dt><dd>{{ tag.measurementAgeMs >= 0 ? `${tag.measurementAgeMs.toFixed(0)} ms` : 'n/a' }}</dd>
        <dt>Tag IDs</dt><dd>{{ tag.tagIds.length ? tag.tagIds.join(', ') : 'none' }}</dd>
        <dt>Corner inliers</dt><dd>{{ tag.inlierCount }}/{{ tag.cornerCount }}</dd>
        <dt>Inlier ratio</dt><dd>{{ (tag.inlierRatio * 100).toFixed(1) }}%</dd>
        <dt>Average distance</dt><dd>{{ tag.averageTagDistanceMeters.toFixed(2) }} m</dd>
        <dt>XY std dev (larger axis)</dt><dd>{{ tag.linearStdDevMeters.toFixed(3) }} m</dd>
        <dt>Published</dt><dd>{{ tag.publishCount.toLocaleString() }}</dd>
        <dt>Camera failures</dt>
        <dd :class="tag.cameraFailureCount ? 'bad' : 'good'">{{ tag.cameraFailureCount.toLocaleString() }}</dd>
      </dl>
    </section>

    <section class="panel card">
      <h3>Passthrough stream</h3>
      <dl class="readout">
        <dt>MJPEG clients</dt><dd>{{ stream.connectedClients }}</dd>
        <dt>Last frame age</dt><dd>{{ stream.lastFrameAgeMs >= 0 ? `${stream.lastFrameAgeMs.toFixed(0)} ms` : 'No frame yet' }}</dd>
        <dt>Capture failures</dt><dd>{{ stream.captureFailureCount.toLocaleString() }}</dd>
        <dt>Consecutive failures</dt>
        <dd :class="stream.consecutiveCaptureFailures ? 'bad' : 'good'">{{ stream.consecutiveCaptureFailures }}</dd>
      </dl>
    </section>

    <section class="panel card">
      <h3>Headset</h3>
      <div class="big">
        <b>{{ status.batteryPercent }}%</b>
        <span>battery, {{ status.batteryCharging ? 'charging' : status.batteryStatus.toLowerCase() || 'discharging' }}</span>
      </div>
      <div :class="['bar', { low: status.batteryPercent < 20 }]"><i :style="{ width: `${status.batteryPercent}%` }" /></div>
      <dl class="readout">
        <dt>App FPS</dt><dd>{{ status.fps }}</dd>
        <dt>Frame count</dt><dd>{{ status.frameCount.toLocaleString() }}</dd>
      </dl>
    </section>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useConfigStore } from '../stores/config'
import { useHeadsetStatus } from '../composables/useHeadsetStatus'

const configStore = useConfigStore()
const { status } = useHeadsetStatus()

const debugIpActive = computed(() => Boolean(configStore.config?.debugIpOverride))
const tag = computed(() => status.value!.aprilTag)
const stream = computed(() => status.value!.passthroughStream)

const signed = (value: number, digits: number) => `${value >= 0 ? '+' : ''}${value.toFixed(digits)}`
</script>

<style scoped>
/* Columns pack cards of different heights without gaps between rows. */
.status-grid {
  columns: 300px;
  column-gap: 16px;
}

.card {
  margin-bottom: 16px;
  padding: 14px 18px 16px;
  break-inside: avoid;
}

h3 {
  margin-bottom: 10px;
  padding-bottom: 10px;
  border-bottom: 1px solid var(--line);
  font-size: 13px;
  font-weight: 500;
}

.override {
  margin-left: 8px;
  font-family: var(--font-sans);
}

.big {
  padding: 4px 0 8px;
}

.big b {
  display: block;
  font-size: 28px;
  font-weight: 600;
  font-variant-numeric: tabular-nums;
  letter-spacing: -0.03em;
}

.big span {
  color: var(--text-2);
  font-size: 12px;
}

.bar {
  overflow: hidden;
  height: 6px;
  margin: 2px 0 12px;
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.07);
}

.bar i {
  display: block;
  height: 100%;
  border-radius: 999px;
  background: linear-gradient(90deg, #5e6ad2, #8b95f0);
}

.bar.low i {
  background: var(--red);
}
</style>
