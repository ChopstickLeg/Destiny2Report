<script setup lang="ts" generic="T extends string">
defineProps<{
  modelValue: T
  options: ReadonlyArray<{ value: T; label: string }>
  label: string
}>()

const emit = defineEmits<{ 'update:modelValue': [value: T] }>()
</script>

<template>
  <div class="segmented" role="group" :aria-label="label">
    <button
      v-for="option in options"
      :key="option.value"
      type="button"
      class="segment"
      :class="{ 'segment--active': option.value === modelValue }"
      :aria-pressed="option.value === modelValue"
      @click="emit('update:modelValue', option.value)"
    >
      {{ option.label }}
    </button>
  </div>
</template>

<style scoped>
.segmented {
  display: inline-flex;
  padding: 2px;
  background: var(--color-surface-sunken);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-md);
  gap: 2px;
}

.segment {
  padding: var(--space-1) var(--space-3);
  min-height: 2rem;
  border-radius: calc(var(--radius-md) - 2px);
  font-size: var(--text-sm);
  font-weight: 500;
  color: var(--color-text-secondary);
  transition:
    background-color var(--transition-fast),
    color var(--transition-fast);
}

.segment:hover {
  color: var(--color-text);
}

.segment--active {
  background: var(--color-surface-raised);
  color: var(--color-text);
}
</style>
