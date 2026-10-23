import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import ts from 'typescript'

const source = await readFile(new URL('../src/previewScale.ts', import.meta.url), 'utf8')
const code = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.ESNext } }).outputText
const { previewFactor, previewOptions, STANDARD_MAX_PIXELS } = await import(
  `data:text/javascript;base64,${Buffer.from(code).toString('base64')}`
)

// Same cases as tests/apriltag-preview/EncoderChecks.cs, so the labels match the headset.
test('scale factors match the headset', () => {
  const cap = STANDARD_MAX_PIXELS
  assert.equal(previewFactor(1280, 1280, 4, 0), 4)
  assert.equal(previewFactor(1280, 960, 4, 0), 4)
  assert.equal(previewFactor(1280, 960, 8, 0), 8)
  assert.equal(previewFactor(800, 600, 8, 0), 8)
  assert.equal(previewFactor(1000, 750, 8, 0), 2)
  assert.equal(previewFactor(1280, 960, 2, cap), 2)
  assert.equal(previewFactor(1280, 1280, 2, cap), 4)
  assert.equal(previewFactor(1280, 1280, 1, cap), 4)
  assert.equal(previewFactor(1280, 1280, 1, 0), 1)
  assert.equal(previewFactor(640, 480, 1, cap), 1)
})

test('1280x960 offers Limelight-style sizes', () => {
  const labels = previewOptions(1280, 960, false, true).map(o => o.label)
  assert.deepEqual(labels, ['1280x960 (Full)', '640x480 (½)', '320x240 (¼)', '160x120 (⅛)'])
})

test('sizes above 640x480 need the high-quality stream', () => {
  const options = previewOptions(1280, 1280, false, false)
  assert.deepEqual(options.map(o => o.available), [false, false, true, true])
  assert.equal(options[2].label, '320x320 (¼)')
  assert.match(options[0].label, /needs high quality/)
  assert.deepEqual(previewOptions(1280, 1280, false, true).map(o => o.available), [true, true, true, true])
})

test('sideways cameras show swapped sizes', () => {
  assert.equal(previewOptions(1280, 960, true, true)[2].label, '240x320 (¼)')
})
