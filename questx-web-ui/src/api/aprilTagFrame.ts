import type { AprilTagFrameMetadata } from '../types'

export interface AprilTagFrame {
  metadata: AprilTagFrameMetadata
  jpeg: Blob | null
}

// One HTTP response owns the metadata and JPEG. Never match independently polled
// results by arrival time. Reject incomplete packets before touching the display.
export function parseAprilTagFrame(buffer: ArrayBuffer): AprilTagFrame {
  if (buffer.byteLength < 4) throw new Error('Incomplete preview packet')
  const length = new DataView(buffer).getUint32(0, true)
  if (length === 0 || length > 1024 * 1024 || length > buffer.byteLength - 4) {
    throw new Error('Invalid preview metadata length')
  }
  const metadata: AprilTagFrameMetadata = JSON.parse(
    new TextDecoder().decode(new Uint8Array(buffer, 4, length)))
  if (metadata.version !== 1 || typeof metadata.enabled !== 'boolean' ||
    typeof metadata.streamEnabled !== 'boolean' ||
    !Number.isSafeInteger(metadata.frameId) || metadata.frameId < 0 ||
    !Number.isFinite(metadata.ageMs) || !Number.isFinite(metadata.waitMs) || metadata.waitMs < 0 ||
    !Number.isFinite(metadata.maxAgeMs) || metadata.maxAgeMs <= 0 || metadata.maxAgeMs > 10000 ||
    !Array.isArray(metadata.tags) || metadata.tags.some(tag =>
      !Number.isInteger(tag.id) || !Array.isArray(tag.corners) || tag.corners.length !== 4 ||
      tag.corners.some(p => !Number.isFinite(p.x) || !Number.isFinite(p.y)))) {
    throw new Error('Invalid preview metadata')
  }
  const imageBytes = new Uint8Array(buffer, 4 + length)
  if (imageBytes.length === 0) {
    if (metadata.width !== 0 || metadata.height !== 0 || metadata.tags.length !== 0 || metadata.ageMs !== -1) {
      throw new Error('Preview image missing')
    }
    return { metadata, jpeg: null }
  }
  if (!metadata.enabled || !metadata.streamEnabled || metadata.ageMs < 0 ||
    !Number.isInteger(metadata.width) || !Number.isInteger(metadata.height) ||
    metadata.width <= 0 || metadata.height <= 0 || metadata.width > 8192 || metadata.height > 8192 ||
    metadata.width * metadata.height > 32 * 1024 * 1024) {
    throw new Error('Invalid preview dimensions')
  }
  return { metadata, jpeg: new Blob([imageBytes], { type: 'image/jpeg' }) }
}
