<template>
  <!-- Ctrl/Cmd+K palette. App.vue owns the command list and the open state. -->
  <div v-if="open" class="cmdk" @mousedown.self="close">
    <div class="cmdk-box" role="dialog" aria-label="Command palette">
      <input
        ref="input"
        v-model="query"
        type="text"
        placeholder="Type a command or page"
        aria-label="Command"
        @keydown.down.prevent="move(1)"
        @keydown.up.prevent="move(-1)"
        @keydown.enter.prevent="run(selected)"
        @keydown.esc.prevent="close"
      />
      <div ref="list" class="cmdk-list">
        <template v-for="(command, index) in matches" :key="command.label">
          <div v-if="index === 0 || matches[index - 1].group !== command.group" class="cmdk-group">
            {{ command.group }}
          </div>
          <button
            :class="['cmdk-item', { selected: index === selected }]"
            @mousemove="selected = index"
            @click="run(index)"
          >
            {{ command.label }}
            <kbd v-if="command.shortcut">{{ command.shortcut }}</kbd>
          </button>
        </template>
        <div v-if="matches.length === 0" class="cmdk-group">No matches</div>
      </div>
      <div class="cmdk-foot">↑↓ to move · Enter to run · Esc to close</div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import type { Command } from '../types'

const props = defineProps<{ commands: Command[]; open: boolean }>()
const emit = defineEmits<{ 'update:open': [open: boolean] }>()

const query = ref('')
const selected = ref(0)
const input = ref<HTMLInputElement | null>(null)
const list = ref<HTMLElement | null>(null)

const matches = computed(() => {
  const q = query.value.trim().toLowerCase()
  return props.commands.filter(c => !q || `${c.group} ${c.label}`.toLowerCase().includes(q))
})

watch(() => props.open, async isOpen => {
  if (!isOpen) return
  query.value = ''
  selected.value = 0
  await nextTick()
  input.value?.focus()
})

watch(query, () => { selected.value = 0 })

async function move(step: number) {
  if (matches.value.length === 0) return
  selected.value = (selected.value + step + matches.value.length) % matches.value.length
  await nextTick()
  list.value?.querySelector('.selected')?.scrollIntoView({ block: 'nearest' })
}

function close() {
  emit('update:open', false)
}

function run(index: number) {
  const command = matches.value[index]
  close()
  command?.run()
}
</script>

<style scoped>
.cmdk {
  position: fixed;
  inset: 0;
  z-index: 90;
  display: flex;
  align-items: flex-start;
  justify-content: center;
  padding-top: 14vh;
  background: rgba(0, 0, 0, 0.55);
}

.cmdk-box {
  width: min(560px, calc(100vw - 32px));
  overflow: hidden;
  border: 1px solid rgba(255, 255, 255, 0.1);
  border-radius: 12px;
  background: #141518;
  box-shadow: 0 24px 64px rgba(0, 0, 0, 0.6);
}

.cmdk-box input {
  height: 48px;
  padding: 0 16px;
  border: 0;
  border-bottom: 1px solid var(--line);
  border-radius: 0;
  background: transparent;
  font-size: 15px;
  box-shadow: none;
}

.cmdk-list {
  max-height: 340px;
  overflow: auto;
  padding: 6px;
}

.cmdk-group {
  padding: 8px 10px 4px;
  color: var(--muted);
  font-size: 11.5px;
}

.cmdk-item {
  display: flex;
  align-items: center;
  width: 100%;
  padding: 8px 10px;
  border: 0;
  border-radius: 7px;
  background: transparent;
  color: #d0d3da;
  font-size: 13.5px;
  text-align: left;
}

.cmdk-item.selected {
  background: rgba(255, 255, 255, 0.06);
  color: #fff;
}

.cmdk-item kbd {
  margin-left: auto;
  color: var(--muted);
  font-family: var(--font-mono);
  font-size: 11px;
}

.cmdk-foot {
  padding: 8px 14px;
  border-top: 1px solid var(--line);
  color: var(--muted);
  font-size: 11.5px;
}
</style>
