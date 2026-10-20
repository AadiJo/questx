// Field layout list from /api/apriltag-field-layouts, shared by the Detector tab's
// dropdown and the Field layouts page so an edit on one shows up on the other.

import { ref } from 'vue'
import { configApi } from '../api/config'
import type { AprilTagFieldLayoutEntry } from '../types'

const layouts = ref<AprilTagFieldLayoutEntry[]>([])
const loaded = ref(false)

export async function refreshFieldLayouts() {
  try {
    layouts.value = await configApi.getAprilTagFieldLayouts()
    loaded.value = true
  } catch (error) {
    console.error('Failed to load AprilTag field layouts:', error)
  }
}

export function useFieldLayouts() {
  return { layouts, loaded }
}
