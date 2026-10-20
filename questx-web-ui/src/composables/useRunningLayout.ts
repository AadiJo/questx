// The field layout QuestX loaded at startup, from /api/scene/layout. The saved config
// value can differ until the app restarts; that gap drives the restart banner and the
// "in use" / "loads on restart" chips.

import { ref } from 'vue'
import { sceneApi } from '../api/scene'

const runningLayoutFile = ref<string | null>(null)

export async function refreshRunningLayout() {
  try {
    const layout = await sceneApi.getLayout()
    runningLayoutFile.value = layout.loaded ? layout.file : null
  } catch {
    // Unknown until the next successful refresh; callers treat null as "no banner".
  }
}

export function useRunningLayout() {
  return { runningLayoutFile }
}
