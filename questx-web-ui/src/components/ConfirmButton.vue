<template>
  <!-- Destructive actions need a second click within 3 s instead of a browser dialog.
       Classes passed by the parent (btn, danger, icon-btn) land on the button. -->
  <button type="button" :class="{ confirming: armed }" @click="onClick" @blur="disarm">
    <template v-if="armed">{{ confirmText }}</template>
    <slot v-else />
  </button>
</template>

<script setup lang="ts">
import { onUnmounted, ref } from 'vue'

defineProps<{ confirmText: string }>()
const emit = defineEmits<{ confirm: [] }>()

const armed = ref(false)
let timer: ReturnType<typeof setTimeout> | undefined

function disarm() {
  armed.value = false
  clearTimeout(timer)
}

function onClick() {
  if (armed.value) {
    disarm()
    emit('confirm')
    return
  }
  armed.value = true
  timer = setTimeout(disarm, 3000)
}

onUnmounted(() => clearTimeout(timer))
</script>
