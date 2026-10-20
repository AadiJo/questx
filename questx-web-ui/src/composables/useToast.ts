// Small transient messages shown bottom-right by App.vue. Use for confirmations and
// failures of one-off actions; persistent state belongs in a banner instead.

import { ref } from 'vue'

export type ToastTone = 'info' | 'error'

export interface Toast {
  id: number
  text: string
  tone: ToastTone
}

const toasts = ref<Toast[]>([])
let nextId = 0

export function toast(text: string, tone: ToastTone = 'info') {
  const id = nextId++
  toasts.value = [...toasts.value.slice(-2), { id, text, tone }]
  setTimeout(() => {
    toasts.value = toasts.value.filter(t => t.id !== id)
  }, tone === 'error' ? 5000 : 2200)
}

/** Formats a caught error for a toast. */
export function errorText(prefix: string, error: unknown) {
  return `${prefix}: ${error instanceof Error ? error.message : String(error)}`
}

export function useToasts() {
  return { toasts }
}
