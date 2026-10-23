// Fake QuestX web server for developing the dashboard without a headset.
//
//   npm run mock                                        # serves the API on :5899
//   VITE_API_TARGET=http://127.0.0.1:5899 npm run dev   # dashboard against the mock
//
// It keeps config edits in memory and draws a synthetic grayscale camera frame with real
// tag36h11 tags 25 and 26 from the 2026 field, seen from a robot parked in front of
// them. Frames are PNG bytes; browsers sniff image data, so the JPEG content types the
// real server uses still decode.

import http from 'node:http'
import { readFile } from 'node:fs/promises'
import { deflateSync } from 'node:zlib'
import ts from 'typescript'

const PORT = Number(process.env.MOCK_PORT ?? 5899)
const ROOT = new URL('../', import.meta.url)
const LAYOUT_DIR = new URL('../unity/Assets/StreamingAssets/apriltag/fieldlayouts/', ROOT)

const tagSource = await readFile(new URL('src/three/aprilTag36h11.ts', ROOT), 'utf8')
const tagModule = ts.transpileModule(tagSource, { compilerOptions: { module: ts.ModuleKind.ESNext } }).outputText
const { tag36h11Cells, TAG_CELLS } = await import(`data:text/javascript;base64,${Buffer.from(tagModule).toString('base64')}`)

// ---- Mutable server state ----

const BUNDLED = ['2026-rebuilt-welded.json', '2025-reefscape-welded.json']
const customLayouts = new Map([['team-1234-shop.json', JSON.stringify({ tags: [], field: { length: 6, width: 4 } }, null, 2)]])
const startedAt = Date.now()

const defaults = () => ({
  teamNumber: 1234,
  debugIpOverride: '',
  allowedPoseResetTimeoutMs: 120,
  enableAutoStartOnBoot: true,
  enablePassthroughStream: true,
  enableHighQualityStream: false,
  enableDebugLogging: false,
  cameraOrientation: 0,
  previewScale: 4,
  robotToQuest: { x: 0.28, y: 0, z: 0.6, roll: 0, pitch: 0, yaw: 0 },
  streamMode: { width: 640, height: 480, framerate: 30, quality: 75 },
  enableAprilTagDetector: true,
  aprilTagDetectorMode: {
    mode: 0, width: 1280, height: 960, framerate: 30, ignoredIds: [], maxDistance: 4,
    minimumNumberOfTags: 1, fieldLayoutFile: '2026-rebuilt-welded.json', detector: 0,
  },
})
let config = defaults()
const runningLayout = config.aprilTagDetectorMode.fieldLayoutFile

const LOG_LINES = [
  ['Log', '[NT] Connected to robot at 10.12.34.2 (NT4)'],
  ['Log', '[AprilTag] Loaded field layout 2026-rebuilt-welded.json (32 tags)'],
  ['Log', '[Camera] Passthrough camera started 1280x960 @ 60 Hz'],
  ['Log', '[AprilTag] Published measurement tags=[25,26] inliers=8/8 dist=1.21m sd=0.018m'],
  ['Log', '[VIO] Tracking epoch 3, pose published at 120 Hz'],
  ['Warning', '[Stream] Client fell behind, dropped 4 MJPEG frames'],
  ['Log', '[AprilTag] Published measurement tags=[26] inliers=4/4 dist=1.19m sd=0.031m'],
  ['Warning', '[AprilTag] Rejected: avgTagDistance=4.62m > maxDistance=4.00m'],
  ['Error', '[Camera] Capture failed: ImageReader acquireLatestImage returned null'],
  ['Log', '[VIO] Depth drift estimate 0.8 cm over the last 10 s'],
]
let logs = Array.from({ length: 48 }, (_, i) => {
  const [Type, Message] = LOG_LINES[i % LOG_LINES.length]
  return { Type, Message, StackTrace: Type === 'Error' ? 'at PassthroughFrameSource.Capture()\nat QuestX.Update()' : '', Timestamp: startedAt - (48 - i) * 1700 }
})

// ---- Scene geometry: robot parked 1.4 m in front of tags 25 and 26 ----

const field = JSON.parse(await readFile(new URL('2026-rebuilt-welded.json', LAYOUT_DIR), 'utf8'))
const TAG_SIZE = 0.1651
const ROBOT = { x: 2.6, y: 4.2, yaw: 0.06 }
const W = 1280
const H = 960
const INTRINSICS = { fx: 640, fy: 640, cx: 640, cy: 480 }

const tagPose = id => field.tags.find(t => t.ID === id).pose
const yawFromQuat = q => Math.atan2(2 * (q.W * q.Z + q.X * q.Y), 1 - 2 * (q.Y * q.Y + q.Z * q.Z))
const quat = yaw => ({ qw: Math.cos(yaw / 2), qx: 0, qy: 0, qz: Math.sin(yaw / 2) })

// Field point -> camera frame (X forward, Y left, Z up) for the headset on the robot.
function toCamera([x, y, z]) {
  const m = config.robotToQuest
  const yaw = ROBOT.yaw + m.yaw * Math.PI / 180
  const hx = ROBOT.x + m.x * Math.cos(ROBOT.yaw) - m.y * Math.sin(ROBOT.yaw)
  const hy = ROBOT.y + m.x * Math.sin(ROBOT.yaw) + m.y * Math.cos(ROBOT.yaw)
  const dx = x - hx
  const dy = y - hy
  return [dx * Math.cos(yaw) + dy * Math.sin(yaw), -dx * Math.sin(yaw) + dy * Math.cos(yaw), z - m.z]
}

const project = ([X, Y, Z]) => ({ x: INTRINSICS.cx - INTRINSICS.fx * Y / X, y: INTRINSICS.cy - INTRINSICS.fy * Z / X })

const VISIBLE = [25, 26].map(id => {
  const pose = tagPose(id)
  const t = pose.translation
  const yaw = yawFromQuat(pose.rotation.quaternion)
  // Tag plane axes in field coordinates: right is the viewer's right when facing the tag.
  const right = [-Math.sin(yaw), Math.cos(yaw), 0]
  const center = [t.x, t.y, t.z]
  const at = (a, b) => toCamera(center.map((c, i) => c + right[i] * a + (i === 2 ? b : 0)))
  const half = TAG_SIZE / 2
  return {
    id,
    yaw,
    center: toCamera(center),
    right: toCamera(center.map((c, i) => c + right[i])).map((v, i) => v - toCamera(center)[i]),
    up: [0, 0, 1],
    // libapriltag order: bottom-left, bottom-right, top-right, top-left.
    corners: [at(-half, -half), at(half, -half), at(half, half), at(-half, half)].map(project),
  }
})

// ---- Synthetic grayscale frame ----

function renderFrame() {
  const pixels = new Uint8Array(W * H)
  let seed = 7
  const noise = () => ((seed = (seed * 1103515245 + 12345) & 0x7fffffff) / 0x7fffffff - 0.5) * 10
  const horizon = 560
  for (let y = 0; y < H; y++) {
    for (let x = 0; x < W; x++) {
      const wall = y < horizon - (x - 640) * 0.02
      let v = wall ? 92 - y * 0.03 : 58 - (y - horizon) * 0.035
      if (x > 1010 && x < 1110 && wall) v = 70
      pixels[y * W + x] = Math.max(0, Math.min(255, v + noise()))
    }
  }
  // Ray-cast each tag plane so tags render with real perspective.
  for (const tag of VISIBLE) {
    const xs = tag.corners.map(c => c.x)
    const ys = tag.corners.map(c => c.y)
    const pad = 30
    const cellSize = TAG_SIZE / 8
    const n = [tag.right[1] * tag.up[2] - tag.right[2] * tag.up[1], tag.right[2] * tag.up[0] - tag.right[0] * tag.up[2], tag.right[0] * tag.up[1] - tag.right[1] * tag.up[0]]
    const cells = tag36h11Cells(tag.id)
    for (let py = Math.floor(Math.min(...ys)) - pad; py < Math.max(...ys) + pad; py++) {
      for (let px = Math.floor(Math.min(...xs)) - pad; px < Math.max(...xs) + pad; px++) {
        const d = [1, -(px - INTRINSICS.cx) / INTRINSICS.fx, -(py - INTRINSICS.cy) / INTRINSICS.fy]
        const s = (tag.center[0] * n[0] + tag.center[1] * n[1] + tag.center[2] * n[2]) / (d[0] * n[0] + d[1] * n[1] + d[2] * n[2])
        const local = d.map((v, i) => v * s - tag.center[i])
        const a = (local[0] * tag.right[0] + local[1] * tag.right[1] + local[2] * tag.right[2]) / cellSize
        const b = (local[2]) / cellSize
        const col = Math.floor(a + TAG_CELLS / 2)
        const row = Math.floor(TAG_CELLS / 2 - b)
        let v
        if (col >= 0 && col < TAG_CELLS && row >= 0 && row < TAG_CELLS) v = cells[row][col] ? 214 : 24
        else if (Math.abs(a) < 6.2 && Math.abs(b) < 6.2) v = 206
        else continue
        pixels[py * W + px] = v
      }
    }
  }
  return encodePng(pixels, W, H)
}

const CRC_TABLE = Array.from({ length: 256 }, (_, n) => {
  let c = n
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1
  return c >>> 0
})

function chunk(type, data) {
  const body = Buffer.concat([Buffer.from(type), data])
  let crc = 0xffffffff
  for (const byte of body) crc = CRC_TABLE[(crc ^ byte) & 0xff] ^ (crc >>> 8)
  const out = Buffer.alloc(12 + data.length)
  out.writeUInt32BE(data.length, 0)
  body.copy(out, 4)
  out.writeUInt32BE((crc ^ 0xffffffff) >>> 0, 8 + data.length)
  return out
}

function encodePng(gray, width, height) {
  const header = Buffer.alloc(13)
  header.writeUInt32BE(width, 0)
  header.writeUInt32BE(height, 4)
  header[8] = 8 // bit depth
  header[9] = 0 // grayscale
  const raw = Buffer.alloc((width + 1) * height)
  for (let y = 0; y < height; y++) Buffer.from(gray.buffer, y * width, width).copy(raw, y * (width + 1) + 1)
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', header),
    chunk('IDAT', deflateSync(raw)),
    chunk('IEND', Buffer.alloc(0)),
  ])
}

const FRAME = renderFrame()
let frameId = 0
setInterval(() => { frameId++ }, 66)

// ---- Response builders ----

const wobble = (amp, period) => amp * Math.sin((Date.now() - startedAt) / 1000 / period * Math.PI * 2)
const robotPose = () => ({ x: ROBOT.x + wobble(0.004, 3), y: ROBOT.y + wobble(0.003, 4), z: 0, ...quat(ROBOT.yaw) })

function status() {
  const detecting = config.enableAprilTagDetector
  const fast = config.aprilTagDetectorMode.detector === 0
  return {
    position: { x: 2.418 + wobble(0.003, 3), y: 5.073 + wobble(0.002, 5), z: 0.512 },
    rotation: { x: 0, y: 0, z: 0.7, w: 0.71 },
    eulerAngles: { pitch: 91.4 + wobble(0.2, 6), yaw: -1.2, roll: 0.4 },
    isTracking: true,
    trackingLostEvents: 1,
    trackingEpoch: 3,
    batteryPercent: 82,
    batteryLevel: 0.82,
    batteryStatus: 'Discharging',
    batteryCharging: false,
    networkConnected: true,
    ipAddress: '10.12.34.200',
    teamNumber: config.teamNumber,
    robotIpAddress: config.debugIpOverride || `10.${Math.floor(config.teamNumber / 100)}.${config.teamNumber % 100}.2`,
    fps: 72,
    frameCount: 182340 + Math.floor((Date.now() - startedAt) / 14),
    vioPublishCount: 91204 + Math.floor((Date.now() - startedAt) / 8),
    aprilTag: {
      detectorActive: detecting,
      cameraFrameCount: 48211,
      cameraFailureCount: 0,
      solveCount: 4410,
      measurementCount: 4410,
      publishCount: detecting ? 4410 + Math.floor((Date.now() - startedAt) / 33) : 4410,
      measurementAgeMs: detecting ? 21 + wobble(4, 2) : -1,
      tagIds: detecting ? [25, 26] : [],
      inlierCount: detecting ? 8 : 0,
      cornerCount: detecting ? 8 : 0,
      inlierRatio: detecting ? 1 : 0,
      averageTagDistanceMeters: detecting ? 1.21 : 0,
      linearStdDevMeters: detecting ? 0.018 : 0,
      lastOutcome: detecting ? 'Published' : 'Disabled',
      lastReason: '',
      detector: detecting ? (fast ? 'Napa' : 'AprilTag') : '',
      lastDetectMs: fast ? 2.4 : 9.1,
      averageDetectMs: fast ? 2.2 : 8.7,
      averageReadbackMs: 0,
      captureBackend: 'Native',
      averageDeliveryMs: 22.8,
      averageSolveMs: 0.8,
      averagePreviewMs: 6.4,
      averageLatencyMs: fast ? 31 : 38,
      cameraFps: detecting ? 60 : 0,
      processedFps: detecting ? 30 : 0,
      measurementFps: detecting ? 30 : 0,
      busyFrameCount: 12,
    },
    passthroughStream: { captureFailureCount: 3, consecutiveCaptureFailures: 0, lastFrameAgeMs: config.enablePassthroughStream ? 34 : -1, connectedClients: 1 },
    connectedClients: 2,
    passthroughResolutionLockedByAprilTag: detecting,
    effectivePassthroughResolution: detecting ? { width: config.aprilTagDetectorMode.width, height: config.aprilTagDetectorMode.height } : null,
    timestamp: Date.now(),
  }
}

function scene() {
  const detecting = config.enableAprilTagDetector
  const robot = robotPose()
  const m = config.robotToQuest
  const headset = { x: robot.x + m.x, y: robot.y + m.y, z: m.z, ...quat(ROBOT.yaw) }
  const ignored = config.aprilTagDetectorMode.ignoredIds
  return {
    alliance: null,
    robotToQuest: { x: m.x, y: m.y, z: m.z, qw: 1, qx: 0, qy: 0, qz: 0 },
    vio: { tracking: true, epoch: 3, ageMs: 8, robotPose: robot, headsetPose: headset },
    field: {
      estimate: robot,
      lastCorrection: { meters: 0.009, yawRadians: 0.0021, intervalSeconds: 0.54 },
      measurement: detecting ? {
        ageMs: 22, robotPose: { ...robot, x: robot.x + 0.012, y: robot.y + 0.006 }, headsetPose: headset,
        tagIds: [25, 26], ambiguity: 0.041, reprojectionErrorPixels: 0.38, averageTagDistanceMeters: 1.21,
        stdDevs: { xMeters: 0.018, yMeters: 0.018, zMeters: 0.05, rollRadians: 0.05, pitchRadians: 0.05, yawRadians: 0.021 },
      } : null,
    },
    detections: detecting ? {
      ageMs: 20, width: W, height: H, intrinsics: INTRINSICS,
      headsetToCamera: { x: 0.05, y: 0, z: -0.02, qw: 1, qx: 0, qy: 0, qz: 0 },
      tags: [
        ...VISIBLE.map(tag => ({
          id: tag.id,
          status: ignored.includes(tag.id) ? 'ignored' : 'used',
          cameraToTag: { x: tag.center[0], y: tag.center[1], z: tag.center[2], ...quat(tag.yaw - ROBOT.yaw) },
          ambiguity: tag.id === 25 ? 0.04 : 0.06,
          reprojectionErrorPixels: 0.4,
        })),
        { id: 41, status: 'unknown', cameraToTag: { x: 3.3, y: -1.4, z: 0.35, qw: 0, qx: 0, qy: 0, qz: 1 }, ambiguity: 0.27, reprojectionErrorPixels: 1.1 },
      ],
    } : null,
    detector: { active: detecting, lastOutcome: detecting ? 'Published' : 'Disabled', lastReason: '' },
  }
}

function layoutEntries() {
  const bundled = BUNDLED.map(fileName => ({
    fileName, source: 'bundled',
    displayName: fileName === BUNDLED[0] ? '2026 Rebuilt (welded)' : '2025 Reefscape (welded)',
    tagCount: fileName === BUNDLED[0] ? 32 : 22,
  }))
  const custom = [...customLayouts].map(([fileName, content]) => ({
    fileName, source: 'custom', displayName: fileName.replace(/\.json$/, ''),
    tagCount: JSON.parse(content).tags?.length ?? 0,
  }))
  return [...bundled, ...custom]
}

function previewPacket() {
  const live = config.enableAprilTagDetector && config.enablePassthroughStream
  const metadata = {
    version: 1,
    enabled: config.enableAprilTagDetector,
    streamEnabled: config.enablePassthroughStream,
    frameId,
    width: live ? W : 0,
    height: live ? H : 0,
    ageMs: live ? 14 : -1,
    waitMs: 0,
    maxAgeMs: 1000,
    tags: live ? VISIBLE.map(tag => ({ id: tag.id, corners: tag.corners })) : [],
  }
  const json = Buffer.from(JSON.stringify(metadata))
  const length = Buffer.alloc(4)
  length.writeUInt32LE(json.length)
  return Buffer.concat([length, json, live ? FRAME : Buffer.alloc(0)])
}

// ---- HTTP ----

const sendJson = (res, body, code = 200) => {
  res.writeHead(code, { 'Content-Type': 'application/json' })
  res.end(JSON.stringify(body))
}

const readBody = req => new Promise(resolve => {
  const parts = []
  req.on('data', part => parts.push(part))
  req.on('end', () => resolve(Buffer.concat(parts)))
})

function merge(target, patch) {
  for (const [key, value] of Object.entries(patch)) {
    if (value && typeof value === 'object' && !Array.isArray(value) && target[key]) merge(target[key], value)
    else target[key] = value
  }
}

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://mock')
  const path = url.pathname
  const method = req.method

  if (path === '/api/config' && method === 'GET') return sendJson(res, { success: true, ...config, timestamp: Date.now() })
  if (path === '/api/config' && method === 'POST') {
    const patch = JSON.parse((await readBody(req)).toString() || '{}')
    // Matching the real server: a team number update clears the debug override.
    if ('teamNumber' in patch && !('debugIpOverride' in patch)) patch.debugIpOverride = ''
    merge(config, patch)
    return sendJson(res, { success: true, message: 'Updated' })
  }
  if (path === '/api/reset-config') { config = defaults(); return sendJson(res, { success: true, message: 'Reset' }) }
  if (path === '/api/info') {
    return sendJson(res, {
      appName: 'QuestX', version: '2026.2.2', unityVersion: '6000.0.41f1', buildDate: '2026-10-14 18:02',
      platform: 'Android', deviceModel: 'Oculus Quest 3', operatingSystem: 'Android OS 14 / API-34',
      connectedClients: 2, serverPort: 5801, timestamp: Date.now(),
    })
  }
  if (path === '/api/status') return sendJson(res, status())
  if (path === '/api/scene') return sendJson(res, scene())
  if (path === '/api/scene/layout') {
    const tags = field.tags.map(t => ({ id: t.ID, pose: { x: t.pose.translation.x, y: t.pose.translation.y, z: t.pose.translation.z, ...quat(yawFromQuat(t.pose.rotation.quaternion)) } }))
    return sendJson(res, { file: runningLayout, loaded: true, tagSize: TAG_SIZE, field: field.field, tags })
  }
  if (path === '/api/logs' && method === 'DELETE') { logs = []; return sendJson(res, { success: true, message: 'Cleared' }) }
  if (path === '/api/logs') {
    const [Type, Message] = LOG_LINES[Math.floor(Date.now() / 2200) % LOG_LINES.length]
    if (!logs.length || Date.now() - logs.at(-1).Timestamp > 2200) logs.push({ Type, Message, StackTrace: '', Timestamp: Date.now() })
    return sendJson(res, { success: true, logs: logs.slice(-Number(url.searchParams.get('count') ?? 100)) })
  }
  if (path === '/api/restart' || path === '/api/reset-pose') return sendJson(res, { success: true, message: 'OK' })
  if (path === '/api/video-modes' || path === '/api/apriltag-video-modes') {
    const sizes = path === '/api/video-modes' ? [[320, 240], [640, 480], [1280, 960], [1280, 1280]] : [[1280, 960], [1280, 1280]]
    return sendJson(res, sizes.flatMap(([width, height]) => [15, 30, 60].map(framerate => ({ width, height, framerate }))))
  }
  if (path === '/api/download-database') {
    res.writeHead(200, { 'Content-Type': 'application/octet-stream' })
    return res.end(Buffer.from('mock config.db'))
  }
  if (path === '/api/upload-database') { await readBody(req); return sendJson(res, { success: true, message: 'Database uploaded. Restart QuestX to apply it.' }) }

  if (path === '/api/apriltag-field-layouts' && method === 'GET') return sendJson(res, layoutEntries())
  if (path === '/api/apriltag-field-layouts' && method === 'POST') {
    const { name, content } = JSON.parse((await readBody(req)).toString())
    try { JSON.parse(content) } catch { return sendJson(res, { success: false, message: 'Invalid JSON' }, 400) }
    const fileName = `${name}.json`
    customLayouts.set(fileName, content)
    return sendJson(res, { success: true, fileName, tagCount: JSON.parse(content).tags?.length ?? 0 })
  }
  if (path.startsWith('/api/apriltag-field-layouts/')) {
    const fileName = decodeURIComponent(path.slice('/api/apriltag-field-layouts/'.length))
    if (!customLayouts.has(fileName)) return sendJson(res, { success: false, message: 'Bundled layouts are read-only' }, 403)
    if (method === 'GET') { res.writeHead(200, { 'Content-Type': 'application/json' }); return res.end(customLayouts.get(fileName)) }
    if (method === 'DELETE') { customLayouts.delete(fileName); return sendJson(res, { success: true, deletedFileName: fileName, fellBackTo: null }) }
    if (method === 'PATCH') {
      const { newName } = JSON.parse((await readBody(req)).toString())
      const newFileName = `${newName}.json`
      customLayouts.set(newFileName, customLayouts.get(fileName))
      customLayouts.delete(fileName)
      return sendJson(res, { success: true, oldFileName: fileName, newFileName })
    }
  }

  if (path === '/api/apriltag-frame') {
    const after = Number(url.searchParams.get('after') ?? -1)
    if (frameId <= after) await new Promise(resolve => setTimeout(resolve, 66))
    res.writeHead(200, { 'Content-Type': 'application/octet-stream', 'Cache-Control': 'no-store' })
    return res.end(previewPacket())
  }
  if (path === '/video') {
    res.writeHead(200, { 'Content-Type': 'multipart/x-mixed-replace; boundary=frame', 'Cache-Control': 'no-store' })
    const timer = setInterval(() => {
      res.write(`--frame\r\nContent-Type: image/jpeg\r\nContent-Length: ${FRAME.length}\r\n\r\n`)
      res.write(FRAME)
      res.write('\r\n')
    }, 100)
    return req.on('close', () => clearInterval(timer))
  }

  sendJson(res, { success: false, message: `No mock for ${method} ${path}` }, 404)
})

server.listen(PORT, '127.0.0.1', () => console.log(`Mock QuestX API on http://127.0.0.1:${PORT}`))
