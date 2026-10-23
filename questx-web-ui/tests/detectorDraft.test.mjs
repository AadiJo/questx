import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import ts from 'typescript'
import * as vue from 'vue'
import * as pinia from 'pinia'

// Execute the production store/composable with real Vue reactivity. Only HTTP and
// toast timers are replaced; no DOM or additional test framework is needed.
async function loadSource(path, dependencies) {
  const source = await readFile(new URL(path, import.meta.url), 'utf8')
  const compiled = ts.transpileModule(source, {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
  }).outputText
  const module = { exports: {} }
  new Function('require', 'module', 'exports', compiled)(name => {
    if (!(name in dependencies)) throw new Error(`Unexpected test import: ${name}`)
    return dependencies[name]
  }, module, module.exports)
  return module.exports
}

const initialMode = () => ({ mode: 0, width: 1280, height: 1280, framerate: 30,
  detector: 0, fieldLayoutFile: 'field.json', maxDistance: 5,
  minimumNumberOfTags: 1, ignoredIds: [1, 2] })

async function fixture(t) {
  pinia.setActivePinia(pinia.createPinia())
  let sent
  const storeModule = await loadSource('../src/stores/config.ts', {
    vue, pinia, '../api/config': { configApi: {
      updateConfig: async body => { sent = body; return { success: true } },
    } },
  })
  const { useDetectorDraft } = await loadSource('../src/composables/useDetectorDraft.ts', {
    vue, '../stores/config': storeModule, './useToast': { toast() {}, errorText: String },
  })
  const store = storeModule.useConfigStore()
  store.config = { aprilTagDetectorMode: initialMode() }
  const scope = vue.effectScope()
  const draft = scope.run(useDetectorDraft)
  t.after(() => scope.stop())
  async function poll(mode) {
    store.config = { aprilTagDetectorMode: mode }
    await vue.nextTick()
  }
  return { store, ...draft, poll, sent: () => sent }
}

test('ordinary polls clone reactive ignored IDs without throwing or sharing the array', async t => {
  const f = await fixture(t)
  await f.poll(initialMode())
  assert.deepEqual(f.dirtyFields.value, [])
  await f.poll({ ...initialMode(), ignoredIds: [3], framerate: 24 })
  assert.deepEqual(f.draft.value.ignoredIds, [3])
  assert.equal(f.draft.value.framerate, 24)
  f.draft.value.ignoredIds.push(4)
  assert.deepEqual(f.store.config.aprilTagDetectorMode.ignoredIds, [3])
})

test('polling updates untouched fields and preserves dirty scalar and array edits', async t => {
  const f = await fixture(t)
  f.draft.value.ignoredIds.push(9)
  f.draft.value.maxDistance = 7
  await f.poll({ ...initialMode(), ignoredIds: [3], maxDistance: 6, framerate: 24 })
  assert.deepEqual(f.draft.value.ignoredIds, [1, 2, 9])
  assert.equal(f.draft.value.maxDistance, 7)
  assert.equal(f.draft.value.framerate, 24)
  assert.deepEqual(f.dirtyFields.value, ['maxDistance', 'ignoredIds'])
})

test('cancel restores the latest polled configuration without aliasing it', async t => {
  const f = await fixture(t)
  f.draft.value.ignoredIds.push(9)
  await f.poll({ ...initialMode(), ignoredIds: [3] })
  f.cancel()
  assert.deepEqual(f.draft.value.ignoredIds, [3])
  assert.deepEqual(f.dirtyFields.value, [])
  f.draft.value.ignoredIds.push(4)
  assert.deepEqual(f.store.config.aprilTagDetectorMode.ignoredIds, [3])
})

test('apply followed by a poll leaves saved settings clean and independent', async t => {
  const f = await fixture(t)
  f.draft.value.ignoredIds = [7]
  await f.apply()
  await vue.nextTick()
  assert.deepEqual(f.sent().aprilTagDetectorMode.ignoredIds, [7])
  assert.equal(f.applying.value, false)
  await f.poll({ ...initialMode(), ignoredIds: [7] })
  assert.deepEqual(f.dirtyFields.value, [])
  f.draft.value.ignoredIds.push(8)
  assert.deepEqual(f.store.config.aprilTagDetectorMode.ignoredIds, [7])
})
