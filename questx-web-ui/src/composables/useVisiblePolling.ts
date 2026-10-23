import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'

/** Keep one request chain while visible. The caller handles errors and checks
 * isCurrent after each await before publishing results or issuing another request. */
export function useVisiblePolling(
  active: () => boolean,
  interval: number,
  poll: (isCurrent: () => boolean) => Promise<void>,
  cadence: 'completion' | 'fixed' = 'completion',
) {
  const mounted = ref(false)
  const documentVisible = ref(!document.hidden)
  const visible = computed(() => mounted.value && active() && documentVisible.value)
  let generation = 0
  let inFlight = false
  let nextPollAt = 0
  let timer: ReturnType<typeof setTimeout> | undefined

  async function run() {
    if (!visible.value || inFlight) return
    inFlight = true
    const started = generation
    const isCurrent = () => visible.value && started === generation
    try {
      await poll(isCurrent)
    } finally {
      // Keep the lock until the old request settles, even after leaving/re-entering.
      inFlight = false
      if (visible.value) {
        if (started !== generation) void run()
        else if (cadence === 'fixed') {
          // Preserve the visible session's start cadence without overlapping requests
          // or catching up ticks missed during a slow response or suspended timer.
          const now = performance.now()
          if (nextPollAt <= now) {
            nextPollAt += (Math.floor((now - nextPollAt) / interval) + 1) * interval
          }
          timer = setTimeout(() => {
            nextPollAt += interval // Consume this boundary even if the request settles immediately.
            void run()
          }, Math.ceil(nextPollAt - now))
        } else timer = setTimeout(run, interval)
      }
    }
  }

  watch(visible, value => {
    generation++
    clearTimeout(timer)
    timer = undefined
    if (value) {
      nextPollAt = performance.now() + interval
      void run()
    }
  }, { flush: 'sync' })

  function updateVisibility() {
    documentVisible.value = !document.hidden
  }

  onMounted(() => {
    updateVisibility()
    document.addEventListener('visibilitychange', updateVisibility)
    mounted.value = true
  })
  onBeforeUnmount(() => {
    mounted.value = false
    document.removeEventListener('visibilitychange', updateVisibility)
  })

  return visible
}
