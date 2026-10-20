// Composable for centralized connection state management with exponential backoff

import { ref, computed } from 'vue'
import { configApi } from '../api/config'

type ConnectionStatus = 'connected' | 'disconnected' | 'connecting' | 'reconnecting'

const connectionStatus = ref<ConnectionStatus>('connecting')
const lastSuccessfulConnection = ref<number>(Date.now())
const consecutiveFailures = ref(0)
const reconnectDelay = ref(3000) // Start with 3 seconds
const nextRetryTime = ref<number>(0)

// Clock for the retry countdown. Date.now() is not reactive, so this ticks only while
// a retry is pending and stops once the connection is back.
const now = ref(Date.now())
let clock: ReturnType<typeof setInterval> | undefined
function runClock(running: boolean) {
  if (running && clock === undefined) {
    clock = setInterval(() => { now.value = Date.now() }, 500)
  } else if (!running && clock !== undefined) {
    clearInterval(clock)
    clock = undefined
  }
}

// Exponential backoff configuration
const MIN_DELAY = 3000      // 3 seconds
const MAX_DELAY = 30000     // 30 seconds
const BACKOFF_MULTIPLIER = 2

export function useConnectionState() {
  const isConnected = computed(() => connectionStatus.value === 'connected')
  const isDisconnected = computed(() => 
    connectionStatus.value === 'disconnected' || connectionStatus.value === 'reconnecting'
  )
  
  const secondsUntilRetry = computed(() => {
    if (nextRetryTime.value === 0) return 0
    const seconds = Math.ceil((nextRetryTime.value - now.value) / 1000)
    return Math.max(0, seconds)
  })

  async function checkConnection(isManualRetry: boolean = false): Promise<boolean> {
    try {
      await configApi.getConfig()
      // Success - reset failure counter and delay
      consecutiveFailures.value = 0
      reconnectDelay.value = MIN_DELAY
      lastSuccessfulConnection.value = Date.now()
      
      if (connectionStatus.value !== 'connected') {
        console.log('[ConnectionState] Connection restored')
      }
      connectionStatus.value = 'connected'
      nextRetryTime.value = 0
      runClock(false)
      return true
    } catch (error) {
      // Only increment failure counter for automatic retries, not manual
      if (!isManualRetry) {
        consecutiveFailures.value++
      }
      
      if (connectionStatus.value === 'connected') {
        console.log('[ConnectionState] Connection lost')
      }
      
      connectionStatus.value = consecutiveFailures.value === 1 ? 'disconnected' : 'reconnecting'
      
      // Calculate exponential backoff (uses existing consecutiveFailures)
      reconnectDelay.value = Math.min(
        MIN_DELAY * Math.pow(BACKOFF_MULTIPLIER, consecutiveFailures.value - 1),
        MAX_DELAY
      )
      
      nextRetryTime.value = Date.now() + reconnectDelay.value
      now.value = Date.now()
      runClock(true)
      
      if (isManualRetry) {
        console.log(`[ConnectionState] Manual retry failed. Next automatic retry in ${reconnectDelay.value / 1000}s`)
      } else {
        console.log(`[ConnectionState] Connection failed (${consecutiveFailures.value} consecutive). Retrying in ${reconnectDelay.value / 1000}s`)
      }
      return false
    }
  }

  function getReconnectDelay(): number {
    return reconnectDelay.value
  }

  function resetConnectionState() {
    consecutiveFailures.value = 0
    reconnectDelay.value = MIN_DELAY
    nextRetryTime.value = 0
  }

  return {
    connectionStatus,
    isConnected,
    isDisconnected,
    lastSuccessfulConnection,
    consecutiveFailures,
    secondsUntilRetry,
    checkConnection,
    getReconnectDelay,
    resetConnectionState
  }
}

