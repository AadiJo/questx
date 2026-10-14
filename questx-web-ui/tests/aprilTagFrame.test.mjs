import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import ts from 'typescript'

// Run the production parser with Node's test runner, without another test framework.
const source = await readFile(new URL('../src/api/aprilTagFrame.ts', import.meta.url), 'utf8')
const compiled = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.ESNext } }).outputText
const { parseAprilTagFrame } = await import(`data:text/javascript;base64,${Buffer.from(compiled).toString('base64')}`)
const tag = { id: 7, corners: [{ x: 20, y: 20 }, { x: 10, y: 20 }, { x: 10, y: 10 }, { x: 20, y: 10 }] }
const metadata = { version: 1, enabled: true, streamEnabled: true, frameId: 42,
  width: 640, height: 480, ageMs: 25, waitMs: 100, maxAgeMs: 2000, tags: [tag] }
function packet(overrides = {}, jpeg = [255, 216, 7, 255, 217]) {
  const json = new TextEncoder().encode(JSON.stringify({ ...metadata, ...overrides }))
  const bytes = new Uint8Array(4 + json.length + jpeg.length)
  new DataView(bytes.buffer).setUint32(0, json.length, true)
  bytes.set(json, 4)
  bytes.set(jpeg, 4 + json.length)
  return bytes.buffer
}
test('keeps frame ID, corner order, dimensions and JPEG bytes together', async () => {
  const frame = parseAprilTagFrame(packet())
  assert.deepEqual(frame.metadata, metadata)
  assert.equal(frame.jpeg.type, 'image/jpeg')
  assert.deepEqual([...new Uint8Array(await frame.jpeg.arrayBuffer())], [255, 216, 7, 255, 217])
})
test('empty detections still carry an image', () => {
  const frame = parseAprilTagFrame(packet({ tags: [] }))
  assert.equal(frame.metadata.tags.length, 0)
  assert.ok(frame.jpeg)
})
test('heartbeat has no image or corners', () => {
  const frame = parseAprilTagFrame(packet({ width: 0, height: 0, ageMs: -1, tags: [] }, []))
  assert.equal(frame.jpeg, null)
})
test('rejects truncated prefix and metadata', () => {
  assert.throws(() => parseAprilTagFrame(new ArrayBuffer(3)))
  const full = packet()
  assert.throws(() => parseAprilTagFrame(full.slice(0, 20)))
})
test('rejects missing images and wrong versions', () => {
  assert.throws(() => parseAprilTagFrame(packet({}, [])))
  assert.throws(() => parseAprilTagFrame(packet({ version: 2 })))
})
test('rejects malformed IDs, corner data, ages and dimensions', () => {
  for (const fields of [
    { frameId: -1 }, { frameId: Number.MAX_SAFE_INTEGER + 1 }, { enabled: 'true' },
    { tags: [{ id: 7, corners: [{ x: 1, y: 2 }] }] },
    { tags: [{ ...tag, corners: [...tag.corners.slice(0, 3), { x: 'NaN', y: 1 }] }] },
    { width: 0 }, { height: -1 }, { width: 100000 }, { width: 8000, height: 8000 },
    { ageMs: -1 }, { waitMs: -1 }, { maxAgeMs: 0 }, { maxAgeMs: 100000 },
  ]) assert.throws(() => parseAprilTagFrame(packet(fields)), JSON.stringify(fields))
})
