<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, useId } from 'vue'

const props = defineProps<{
  modelValue: string
  options: ReadonlyArray<{ value: string; label: string }>
  label: string
}>()

const emit = defineEmits<{ 'update:modelValue': [value: string] }>()
const root = ref<HTMLElement | null>(null)
const trigger = ref<HTMLButtonElement | null>(null)
const isOpen = ref(false)
const highlightedIndex = ref(0)
const listboxId = useId()

const selectedIndex = computed(() =>
  Math.max(
    0,
    props.options.findIndex((option) => option.value === props.modelValue),
  ),
)
const selectedLabel = computed(
  () => props.options.find((option) => option.value === props.modelValue)?.label ?? '',
)
const activeOptionId = computed(() =>
  isOpen.value ? `${listboxId}-${highlightedIndex.value}` : undefined,
)

function openMenu() {
  highlightedIndex.value = selectedIndex.value
  isOpen.value = true
}

function toggleMenu() {
  if (isOpen.value) isOpen.value = false
  else openMenu()
}

function selectOption(index: number) {
  const option = props.options[index]
  if (!option) return
  emit('update:modelValue', option.value)
  isOpen.value = false
  void nextTick(() => trigger.value?.focus())
}

function moveHighlight(offset: number) {
  if (!isOpen.value) openMenu()
  const count = props.options.length
  if (count > 0) highlightedIndex.value = (highlightedIndex.value + offset + count) % count
}

function onKeydown(event: KeyboardEvent) {
  if (event.key === 'ArrowDown') {
    event.preventDefault()
    moveHighlight(1)
  } else if (event.key === 'ArrowUp') {
    event.preventDefault()
    moveHighlight(-1)
  } else if (event.key === 'Home' && isOpen.value) {
    event.preventDefault()
    highlightedIndex.value = 0
  } else if (event.key === 'End' && isOpen.value) {
    event.preventDefault()
    highlightedIndex.value = Math.max(0, props.options.length - 1)
  } else if ((event.key === 'Enter' || event.key === ' ') && isOpen.value) {
    event.preventDefault()
    selectOption(highlightedIndex.value)
  } else if (event.key === 'Escape' && isOpen.value) {
    event.preventDefault()
    isOpen.value = false
  }
}

function onDocumentPointerDown(event: PointerEvent) {
  if (!root.value?.contains(event.target as Node)) isOpen.value = false
}

onMounted(() => document.addEventListener('pointerdown', onDocumentPointerDown))
onBeforeUnmount(() => document.removeEventListener('pointerdown', onDocumentPointerDown))
</script>

<template>
  <div ref="root" class="app-select" @keydown="onKeydown">
    <button
      ref="trigger"
      type="button"
      class="select-trigger"
      aria-haspopup="listbox"
      :aria-expanded="isOpen"
      :aria-controls="listboxId"
      :aria-activedescendant="activeOptionId"
      :aria-label="label"
      @click="toggleMenu"
    >
      <span>{{ selectedLabel }}</span>
      <svg class="chevron" viewBox="0 0 12 12" aria-hidden="true">
        <path d="M2.5 4.5 L6 8 L9.5 4.5" fill="none" stroke="currentColor" stroke-width="1.5" />
      </svg>
    </button>

    <ul v-if="isOpen" :id="listboxId" class="option-list" role="listbox" :aria-label="label">
      <li
        v-for="(option, index) in options"
        :id="`${listboxId}-${index}`"
        :key="option.value"
        class="option"
        :class="{
          'option--highlighted': highlightedIndex === index,
          'option--selected': option.value === modelValue,
        }"
        role="option"
        :aria-selected="option.value === modelValue"
        @mouseenter="highlightedIndex = index"
        @mousedown.prevent
        @click="selectOption(index)"
      >
        <span>{{ option.label }}</span>
        <span v-if="option.value === modelValue" class="check" aria-hidden="true">✓</span>
      </li>
    </ul>
  </div>
</template>

<style scoped>
.app-select {
  position: relative;
  width: 100%;
}
.select-trigger {
  display: flex;
  width: 100%;
  height: 2.75rem;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-3);
  padding-inline: var(--space-3);
  text-align: left;
  background: var(--color-surface);
  border: 1px solid var(--color-border-strong);
  border-radius: var(--radius-md);
  transition:
    background-color var(--transition-fast),
    border-color var(--transition-fast),
    box-shadow var(--transition-fast);
}
.select-trigger:hover,
.select-trigger[aria-expanded='true'] {
  background: var(--color-surface-raised);
  border-color: var(--color-accent);
}
.select-trigger:focus-visible {
  border-color: var(--color-accent);
  box-shadow: 0 0 0 3px var(--color-accent-muted);
}
.chevron {
  display: block;
  flex: none;
  width: 0.75rem;
  height: 0.75rem;
  color: var(--color-text-muted);
}
.select-trigger:hover .chevron,
.select-trigger[aria-expanded='true'] .chevron {
  color: var(--color-accent-strong);
}
.option-list {
  position: absolute;
  z-index: 20;
  top: calc(100% + var(--space-1));
  right: 0;
  left: 0;
  max-height: 19rem;
  padding: var(--space-1);
  overflow-y: auto;
  list-style: none;
  background: var(--color-surface-raised);
  border: 1px solid var(--color-border-strong);
  border-radius: var(--radius-md);
  box-shadow: var(--shadow-raised);
}
.option {
  display: flex;
  min-height: 2.5rem;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-3);
  padding: var(--space-2) var(--space-3);
  color: var(--color-text-secondary);
  border-radius: var(--radius-sm);
  cursor: pointer;
}
.option--highlighted {
  color: var(--color-text);
  background: var(--color-accent-muted);
}
.option--selected {
  color: var(--color-accent-strong);
  font-weight: 600;
}
.check {
  flex: none;
  color: var(--color-accent-strong);
}
</style>
