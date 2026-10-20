<template>
  <canvas ref="canvas" class="apriltag-preview" role="img" :aria-label="status" />
  <span v-if="!hasFrame" class="overlay-status">{{ status }}</span>
</template>

<script setup lang="ts">
import { ref, watch, onMounted, onUnmounted } from 'vue'
import { videoApi } from '../api/video'
import type { AprilTagFrameMetadata } from '../types'

// stats feeds the parent's HUD: preview rate over the last frames, the frame's age, and
// how many tags it shows. It is null while no frame is on screen.
const emit = defineEmits<{
  dimensions: [size: { width: number; height: number }]
  stats: [stats: { fps: number; ageMs: number; tags: number } | null]
}>()
const canvas = ref<HTMLCanvasElement | null>(null)
const visible = ref(!document.hidden)
const status = ref('Waiting for synchronized camera frames…')
const hasFrame = ref(false)
const frameTimes: number[] = []
let image: ImageBitmap | null = null
let metadata: AprilTagFrameMetadata | null = null
let resizeObserver: ResizeObserver | null = null

function clearFrame() {
  if (hasFrame.value) emit('stats', null)
  hasFrame.value = false
  image?.close()
  image = null
  metadata = null
  const target = canvas.value
  if (target) target.getContext('2d')?.clearRect(0, 0, target.width, target.height)
}

function drawFrame() {
  const target = canvas.value
  if (!target || !image || !metadata) return
  const { width, height } = target.getBoundingClientRect()
  if (width <= 0 || height <= 0) return
  const dpr = window.devicePixelRatio || 1
  target.width = Math.max(1, Math.round(width * dpr))
  target.height = Math.max(1, Math.round(height * dpr))
  const context = target.getContext('2d')
  if (!context) throw new Error('Canvas is unavailable')
  const scale = Math.min(width / metadata.width, height / metadata.height)
  const left = (width - metadata.width * scale) / 2
  const top = (height - metadata.height * scale) / 2
  context.setTransform(dpr, 0, 0, dpr, 0, 0)
  context.drawImage(image, left, top, metadata.width * scale, metadata.height * scale)
  context.lineJoin = 'round'
  context.font = '700 16px sans-serif'
  context.textAlign = 'center'
  context.textBaseline = 'middle'
  for (const tag of metadata.tags) {
    context.beginPath()
    tag.corners.forEach((point, index) => {
      const x = left + point.x * scale
      const y = top + point.y * scale
      if (index === 0) context.moveTo(x, y)
      else context.lineTo(x, y)
    })
    context.closePath()
    context.strokeStyle = '#8a96ff'
    context.lineWidth = 2.5
    context.stroke()
    const x = left + tag.corners.reduce((sum, p) => sum + p.x, 0) / 4 * scale
    const y = top + tag.corners.reduce((sum, p) => sum + p.y, 0) / 4 * scale
    context.strokeStyle = '#111'
    context.fillStyle = 'white'
    context.lineWidth = 3
    context.strokeText(String(tag.id), x, y)
    context.fillText(String(tag.id), x, y)
  }
}

// One request and one decode at a time: a slow browser asks for the latest frame
// when ready, rather than building a queue. Image and outlines are committed in
// the same synchronous canvas draw, never from independent updates.
watch(visible, (isVisible, _previous, onCleanup) => {
  clearFrame()
  status.value = 'Waiting for synchronized camera frames…'
  if (!isVisible) return

  let stopped = false
  let after = -1
  let controller: AbortController | null = null
  let pollTimer: ReturnType<typeof setTimeout> | undefined
  let expiryTimer: ReturnType<typeof setTimeout> | undefined
  let requestTimer: ReturnType<typeof setTimeout> | undefined

  function clearDisplayedFrame() {
    clearTimeout(expiryTimer)
    clearFrame()
  }

  async function poll() {
    const request = new AbortController()
    controller = request
    requestTimer = setTimeout(() => request.abort(), 4000)
    let retryMs = 0
    try {
      const result = await videoApi.getAprilTagFrame(after, request.signal)
      if (stopped || request.signal.aborted) return
      const frame = result.metadata
      if (!frame.enabled || !frame.streamEnabled) {
        clearDisplayedFrame()
        after = frame.frameId
        status.value = !frame.streamEnabled ? 'Camera stream disabled.'
          : 'AprilTag detector is off.'
        retryMs = 500
      } else if (!result.jpeg) {
        // A changed revision without an image invalidates the previous camera mode.
        // An unchanged heartbeat must not extend the old frame's expiry timer.
        if (after !== frame.frameId) clearDisplayedFrame()
        after = frame.frameId
        if (!image) status.value = 'Waiting for synchronized camera frames…'
        retryMs = 25
      } else if (frame.frameId !== after) {
        const decodingStarted = performance.now()
        const bitmap = await createImageBitmap(result.jpeg)
        if (stopped || request.signal.aborted) {
          bitmap.close()
          return
        }
        const remaining = frame.maxAgeMs - frame.ageMs - (performance.now() - decodingStarted)
        if (bitmap.width !== frame.width || bitmap.height !== frame.height) {
          bitmap.close()
          throw new Error('Preview image dimensions do not match its detections')
        }
        after = frame.frameId
        if (remaining <= 0) {
          bitmap.close()
          clearDisplayedFrame()
          status.value = 'Waiting for a fresh synchronized frame…'
          retryMs = 25
        } else {
          clearDisplayedFrame()
          image = bitmap
          metadata = frame
          drawFrame()
          hasFrame.value = true
          emit('dimensions', { width: frame.width, height: frame.height })
          const arrived = performance.now()
          frameTimes.push(arrived)
          while (frameTimes.length > 10 || (frameTimes.length > 1 && arrived - frameTimes[0] > 3000)) frameTimes.shift()
          const span = (arrived - frameTimes[0]) / 1000
          emit('stats', {
            fps: span > 0 ? (frameTimes.length - 1) / span : 0,
            ageMs: frame.ageMs,
            tags: frame.tags.length,
          })
          const count = frame.tags.length
          status.value = `Synchronized · ${count} ${count === 1 ? 'tag' : 'tags'} detected`
          expiryTimer = setTimeout(() => {
            clearFrame()
            status.value = 'Waiting for a fresh synchronized frame…'
          }, remaining)
        }
      } else {
        // A misbehaving server cannot cause a tight loop or revive an old frame.
        retryMs = 100
      }
    } catch {
      if (stopped) return
      clearDisplayedFrame()
      status.value = 'Synchronized preview unavailable — retrying…'
      retryMs = 500
    } finally {
      clearTimeout(requestTimer)
      if (!stopped) pollTimer = setTimeout(poll, retryMs)
    }
  }

  onCleanup(() => {
    stopped = true
    controller?.abort()
    clearTimeout(pollTimer)
    clearTimeout(requestTimer)
    clearDisplayedFrame()
  })
  poll()
}, { immediate: true })

function updateVisibility() { visible.value = !document.hidden }
onMounted(() => {
  document.addEventListener('visibilitychange', updateVisibility)
  resizeObserver = new ResizeObserver(drawFrame)
  if (canvas.value) resizeObserver.observe(canvas.value)
})
onUnmounted(() => {
  document.removeEventListener('visibilitychange', updateVisibility)
  resizeObserver?.disconnect()
})
</script>

<style scoped>
.apriltag-preview {
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
  pointer-events: none;
}

.overlay-status {
  position: absolute;
  top: 50%;
  left: 50%;
  max-width: calc(100% - 2rem);
  transform: translate(-50%, -50%);
  color: var(--text-2);
  font-size: 13px;
  text-align: center;
  pointer-events: none;
}
</style>
