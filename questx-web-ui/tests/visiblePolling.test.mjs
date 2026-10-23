import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import ts from 'typescript'
import { compileScript, parse } from '@vue/compiler-sfc'
import * as vue from 'vue'
import * as THREE from 'three'

// Run actual SFC setup/lifecycle code with Vue. Replace only the host DOM,
// network, timers, and WebGL boundary; no source-string behavior assertions.
function compile(source, dependencies) {
  const compiled = ts.transpileModule(source, {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
  }).outputText
  const module = { exports: {} }
  new Function('require', 'module', 'exports', compiled)(name => {
    assert.ok(name in dependencies, `Unexpected import: ${name}`)
    return dependencies[name]
  }, module, module.exports)
  return module.exports
}
const read = path => readFile(new URL(`../src/${path}`, import.meta.url), 'utf8')
const polling = compile(await read('composables/useVisiblePolling.ts'), { vue })
const types = compile(await read('types.ts'), {})
const mountMath = compile(await read('three/mountMath.ts'), { '../types': types })
const flush = () => new Promise(resolve => setImmediate(resolve))

const cleanups = new WeakMap()
function cleanup(t, callback) {
  if (!cleanups.has(t)) {
    const callbacks = []
    cleanups.set(t, callbacks)
    t.after(() => { for (const fn of callbacks.reverse()) fn() })
  }
  cleanups.get(t).push(callback)
}

function globals(t, values) {
  for (const [name, value] of Object.entries(values)) {
    const original = Object.getOwnPropertyDescriptor(globalThis, name)
    Object.defineProperty(globalThis, name, { configurable: true, writable: true, value })
    cleanup(t, () => {
      if (original) Object.defineProperty(globalThis, name, original)
      else delete globalThis[name]
    })
  }
}

function environment(t, hidden = false) {
  const listeners = new Set()
  const document = {
    hidden,
    addEventListener: (name, listener) => {
      assert.equal(name, 'visibilitychange')
      listeners.add(listener)
    },
    removeEventListener: (name, listener) => listeners.delete(listener),
    setHidden(value) {
      this.hidden = value
      for (const listener of listeners) listener()
    },
  }
  let now = 0, nextId = 0
  const timers = new Map()
  globals(t, {
    document,
    performance: { now: () => now },
    localStorage: { getItem: () => null, setItem() {} },
    setTimeout: (fn, delay) => {
      const id = ++nextId
      // Browser timeout delays are converted to integer milliseconds.
      timers.set(id, { fn, due: now + Math.trunc(delay) })
      return id
    },
    clearTimeout: id => timers.delete(id),
  })
  return {
    document, listeners, timers,
    async advance(ms) {
      now += ms
      for (const [id, timer] of [...timers]) {
        if (timer.due <= now) { timers.delete(id); timer.fn() }
      }
      await flush()
    },
  }
}

function network() {
  const requests = []
  let pending = 0, maxPending = 0
  function request() {
    pending++
    maxPending = Math.max(maxPending, pending)
    return new Promise((resolve, reject) => requests.push({
      resolve: value => { pending--; resolve(value) },
      reject: error => { pending--; reject(error) },
    }))
  }
  return { requests, request, maxPending: () => maxPending }
}

const renderer = vue.createRenderer({
  createElement: tag => ({ tag }), createText: text => ({ text }), createComment: () => ({}),
  insert() {}, remove() {}, setText() {}, setElementText() {}, patchProp() {},
  parentNode: () => null, nextSibling: () => null,
})

async function mountView(t, file, dependencies, initiallyActive = false) {
  const { descriptor } = parse(await read(`components/${file}.vue`))
  const script = compileScript(descriptor, { id: file })
  const component = compile(script.content, {
    vue, '../composables/useVisiblePolling': polling,
    '@lucide/vue': {}, ...dependencies,
  }).default
  // Templates are irrelevant to ownership; setup's refs remain real Vue refs.
  component.render = () => null
  const active = vue.ref(initiallyActive)
  const child = vue.ref()
  const app = renderer.createApp({ setup: () => () => vue.h(component, { active: active.value, ref: child }) })
  app.mount({})
  const state = child.value.$.setupState
  let unmounted = false
  const unmount = () => { if (!unmounted) { app.unmount(); unmounted = true } }
  cleanup(t, unmount)
  return { state, unmount, async activate(value) { active.value = value; await flush() } }
}

async function threeFixture(t, hidden = false) {
  const env = environment(t, hidden)
  const scenes = network(), layouts = network(), viewers = []
  class Viewer {
    constructor() { viewers.push(this); this.disposed = false; this.updates = [] }
    setMode(mode) { this.mode = mode }
    setFieldPreset(preset) { this.preset = preset }
    setActive(active) { this.active = active }
    update(input) { this.updates.push(input) }
    dispose() { this.disposed = true; this.active = false }
  }
  const view = await mountView(t, 'ThreeDView', {
    '../api/scene': { sceneApi: { getScene: scenes.request, getLayout: layouts.request } },
    '../stores/config': { useConfigStore: () => ({ config: null }) },
    '../types': types,
    '../three/mountMath': mountMath,
    '../three/SceneViewer': { SceneViewer: Viewer },
    '../three/robotModel': { BUMPER_COLORS: { blue: 1, red: 2 } },
  })
  view.state.viewport = {}
  return { ...env, ...view, scenes, layouts, viewers }
}

async function completeScene(f, scene, layout = { loaded: true }) {
  f.scenes.requests.at(-1).resolve(scene)
  await flush()
  if (!f.state.layout) f.layouts.requests.at(-1).resolve(layout)
  await flush()
}

test('3D starts lazily, pauses for page/document visibility, and preserves viewer state', async t => {
  const f = await threeFixture(t, true)
  await f.activate(true)
  assert.equal(f.viewers.length, 0)
  assert.equal(f.scenes.requests.length, 0)
  f.document.setHidden(false)
  await flush()
  assert.equal(f.viewers.length, 1)
  assert.equal(f.scenes.requests.length, 1)
  const viewer = f.viewers[0]
  await completeScene(f, { id: 'first' })
  f.state.mode = 'robot'
  f.state.fieldPreset = 'top'
  await flush()
  assert.equal(f.timers.size, 1)
  await f.activate(false)
  assert.equal(viewer.active, false)
  assert.equal(f.timers.size, 0)
  await f.advance(10_000)
  assert.equal(f.scenes.requests.length, 1)
  assert.equal(f.state.scene.id, 'first')
  await f.activate(true)
  assert.equal(viewer.active, true)
  assert.equal(viewer.mode, 'robot')
  assert.equal(viewer.preset, 'top')
  assert.equal(f.viewers.length, 1)
  f.document.setHidden(true)
  await flush()
  assert.equal(viewer.active, false)
  f.scenes.requests[1].resolve({ id: 'hidden' })
  await flush()
  assert.equal(f.state.scene.id, 'first')
  assert.equal(f.timers.size, 0)
  f.document.setHidden(false)
  await flush()
  assert.equal(f.scenes.requests.length, 3)
  assert.equal(viewer.active, true)
  f.unmount()
  f.scenes.requests[2].resolve({ id: 'unmounted' })
  await flush()
  assert.equal(f.state.scene.id, 'first')
  assert.equal(f.listeners.size, 0)
  assert.equal(f.timers.size, 0)
  assert.equal(viewer.disposed, true)
})

test('3D off/on during scene and layout awaits retains one flight and rejects stale results', async t => {
  const f = await threeFixture(t)
  await f.activate(true)
  for (let i = 0; i < 3; i++) { await f.activate(false); await f.activate(true) }
  assert.equal(f.scenes.requests.length, 1)
  f.scenes.requests[0].resolve({ id: 'old-scene' })
  await flush()
  assert.equal(f.state.scene, null)
  assert.equal(f.layouts.requests.length, 0)
  assert.equal(f.scenes.requests.length, 2)
  f.scenes.requests[1].resolve({ id: 'old-layout-scene' })
  await flush()
  assert.equal(f.layouts.requests.length, 1)
  await f.activate(false)
  await f.activate(true)
  assert.equal(f.scenes.requests.length, 2)
  f.layouts.requests[0].resolve({ id: 'old-layout' })
  await flush()
  assert.equal(f.state.scene, null)
  assert.equal(f.state.layout, null)
  assert.equal(f.scenes.requests.length, 3)
  await completeScene(f, { id: 'current' }, { id: 'current-layout' })
  assert.equal(f.state.scene.id, 'current')
  assert.equal(f.state.layout.id, 'current-layout')
  assert.equal(f.viewers[0].updates.at(-1).scene.id, 'current')
  assert.equal(f.timers.size, 1)
  await f.advance(65)
  assert.equal(f.scenes.requests.length, 3)
  await f.advance(1)
  assert.equal(f.scenes.requests.length, 4)
  f.scenes.requests[3].reject(new Error('offline'))
  await flush()
  assert.equal(f.state.scene.id, 'current')
  assert.equal(f.timers.size, 1)
  await f.advance(66)
  assert.equal(f.scenes.requests.length, 5)
  assert.equal(f.scenes.maxPending(), 1)
  assert.equal(f.layouts.maxPending(), 1)
})

test('3D teardown while layout is pending discards both snapshots and cannot restart polling', async t => {
  const f = await threeFixture(t)
  await f.activate(true)
  f.scenes.requests[0].resolve({ id: 'pending-layout' })
  await flush()
  f.unmount()
  f.layouts.requests[0].resolve({ id: 'late-layout' })
  await flush()
  assert.equal(f.state.scene, null)
  assert.equal(f.state.layout, null)
  assert.equal(f.timers.size, 0)
  assert.equal(f.listeners.size, 0)
})

test('3D retries a failed layout without overlapping requests, and visibility invalidates pending layout', async t => {
  const f = await threeFixture(t)
  await f.activate(true)
  f.scenes.requests[0].resolve({ id: 'first' })
  await flush()
  f.layouts.requests[0].reject(new Error('layout unavailable'))
  await flush()
  assert.equal(f.timers.size, 1)
  await f.advance(66)
  f.scenes.requests[1].resolve({ id: 'second' })
  await flush()
  f.document.setHidden(true)
  f.document.setHidden(false)
  assert.equal(f.scenes.requests.length, 2)
  f.layouts.requests[1].resolve({ id: 'stale-layout' })
  await flush()
  assert.equal(f.state.layout, null)
  assert.equal(f.scenes.requests.length, 3)
  await completeScene(f, { id: 'current' }, { id: 'current-layout' })
  assert.equal(f.state.layout.id, 'current-layout')
  assert.equal(f.layouts.maxPending(), 1)
  assert.equal(f.timers.size, 1)
})

async function logsFixture(t, initiallyActive = false) {
  const env = environment(t)
  const logs = network()
  const view = await mountView(t, 'LogsView', {
    '../api/config': { configApi: { getLogs: count => {
      assert.equal(count, 200)
      return logs.request()
    } } },
    './ConfirmButton.vue': {},
  }, initiallyActive)
  return { ...env, ...view, logs }
}
const logResponse = message => ({ logs: [{ Message: message, Type: 'Log', Timestamp: 1, StackTrace: '' }] })
