// Staged edits to aprilTagDetectorMode for the Camera page. The Detector and Filtering
// tabs edit one draft; Apply sends it as a single update, Cancel drops it.

import { computed, inject, ref, watch, type InjectionKey } from 'vue'
import { useConfigStore } from '../stores/config'
import { errorText, toast } from './useToast'
import type { AprilTagDetectorMode } from '../types'

/** UI fields that can be dirty; resolution covers width and height together. */
export type DraftField =
  | 'resolution' | 'framerate' | 'detector' | 'fieldLayoutFile'
  | 'maxDistance' | 'minimumNumberOfTags' | 'ignoredIds'

const FIELD_KEYS: Record<DraftField, (keyof AprilTagDetectorMode)[]> = {
  resolution: ['width', 'height'],
  framerate: ['framerate'],
  detector: ['detector'],
  fieldLayoutFile: ['fieldLayoutFile'],
  maxDistance: ['maxDistance'],
  minimumNumberOfTags: ['minimumNumberOfTags'],
  ignoredIds: ['ignoredIds'],
}

/** Fields on the Filtering tab; everything else lives on the Detector tab. */
export const FILTER_FIELDS: DraftField[] = ['maxDistance', 'minimumNumberOfTags', 'ignoredIds']

const same = (a: unknown, b: unknown) => JSON.stringify(a) === JSON.stringify(b)
const copy = (mode: AprilTagDetectorMode): AprilTagDetectorMode => ({ ...mode, ignoredIds: [...mode.ignoredIds] })

export function useDetectorDraft() {
  const configStore = useConfigStore()
  const saved = computed(() => configStore.config?.aprilTagDetectorMode ?? null)
  const draft = ref<AprilTagDetectorMode | null>(saved.value ? copy(saved.value) : null)
  const applying = ref(false)

  // Config polling replaces only the fields the user has not touched: a field that still
  // matches the previous saved value takes the new one.
  watch(saved, (next, previous) => {
    if (!next) return
    if (!draft.value || !previous) {
      draft.value = copy(next)
      return
    }
    const current = draft.value
    for (const key of Object.keys(next) as (keyof AprilTagDetectorMode)[]) {
      if (same(current[key], previous[key])) {
        // ignoredIds is a Vue proxy; the remaining mode fields are primitives.
        Object.assign(current, { [key]: key === 'ignoredIds' ? [...next.ignoredIds] : next[key] })
      }
    }
  })

  const dirtyFields = computed<DraftField[]>(() => {
    const d = draft.value
    const s = saved.value
    if (!d || !s) return []
    return (Object.keys(FIELD_KEYS) as DraftField[]).filter(field =>
      FIELD_KEYS[field].some(key => !same(d[key], s[key])))
  })

  async function apply() {
    if (!draft.value) return
    applying.value = true
    try {
      if (await configStore.updateAprilTagDetectorMode(copy(draft.value))) toast('Detector settings applied')
      else toast(errorText('Could not apply detector settings', configStore.error), 'error')
    } finally {
      applying.value = false
    }
  }

  function cancel() {
    if (saved.value) draft.value = copy(saved.value)
  }

  return { draft, saved, dirtyFields, applying, apply, cancel }
}

export type DetectorDraft = ReturnType<typeof useDetectorDraft>

/** CameraPage provides one draft; the Detector and Filtering tabs inject it. */
export const DETECTOR_DRAFT: InjectionKey<DetectorDraft> = Symbol('detector-draft')

export function injectDetectorDraft(): DetectorDraft {
  const draft = inject(DETECTOR_DRAFT)
  if (!draft) throw new Error('injectDetectorDraft() needs CameraPage to provide a draft')
  return draft
}
