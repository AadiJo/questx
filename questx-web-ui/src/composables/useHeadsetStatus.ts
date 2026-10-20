// Shared /api/status poll. App.vue drives refreshHeadsetStatus() once a second while
// connected; every page reads the same snapshot instead of running its own poller.

import { ref } from 'vue'
import { configApi } from '../api/config'
import type { HeadsetStatus } from '../types'

const status = ref<HeadsetStatus | null>(null)

export async function refreshHeadsetStatus() {
  try {
    status.value = await configApi.getHeadsetStatus()
  } catch {
    // Keep the last snapshot; the connection banner reports the outage.
  }
}

export function useHeadsetStatus() {
  return { status }
}
