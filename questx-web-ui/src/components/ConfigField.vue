<template>
  <div class="config-field" :class="fieldClass">
    <div class="field-header">
      <label class="field-label">
        {{ title }}
        <slot name="badge"></slot>
      </label>
      <span class="field-description">
        <slot name="description">{{ description }}</slot>
      </span>
    </div>
    <div class="field-control" :class="controlClass">
      <slot></slot>
    </div>
  </div>
</template>

<script setup lang="ts">
defineProps<{
  title: string
  description?: string
  fieldClass?: string | object | Array<string | object>
  controlClass?: string | object | Array<string | object>
}>()
</script>

<style scoped>
.config-field {
  padding: 1.5rem;
  background: var(--card-bg);
  border: 1px solid var(--border-color);
  border-radius: 4px;

  /* Stretch the card to the tallest row height (default grid behavior) and
     space children so the header sits at the top and the control sticks to
     the bottom. Without this, cards with short descriptions leave the input
     hanging at the top of an over-tall card and the row looks ragged. */
  display: flex;
  flex-direction: column;
  justify-content: space-between;
  min-height: 100%;
}

.config-field.field-warning {
  border-color: var(--warning-color);
  background: var(--card-bg);
}

.config-field.field-override-active {
  border-color: var(--warning-color);
  background: var(--card-bg);
}

.config-field.field-override-active .field-description {
  color: var(--warning-color);
}

.field-header {
  margin-bottom: 1rem;
}

.field-label {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  font-family: var(--font-display);
  font-size: 1.25rem;
  font-weight: 520;
  line-height: 1.1;
  letter-spacing: -0.02em;
  margin-bottom: 0.4rem;
}

.field-description {
  font-size: 0.8125rem;
  color: var(--text-secondary);
}

.input-control {
  display: flex;
  gap: 0.5rem;
}

.input-control :deep(input:not([type="range"])) {
  flex: 1;
  padding: 0.5rem 0.75rem;
  border: 1px solid var(--border-strong);
  border-radius: 4px;
  font-size: 0.9375rem;
}

.checkbox-control {
  display: flex;
  align-items: center;
  gap: 0.75rem;
}

.checkbox-control :deep(input) {
  width: 1.25rem;
  height: 1.25rem;
}

.reset-field {
  grid-column: 1 / -1;
}

.database-field {
  grid-column: 1 / -1;
}

.database-buttons {
  display: flex;
  gap: 1rem;
  flex-wrap: wrap;
}
</style>
