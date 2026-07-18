<script setup lang="ts">
defineProps<{
  text: string
  explanation: string
}>()
</script>

<template>
  <span
    class="explained-label"
    :title="explanation"
    :aria-label="`${text}: ${explanation}`"
    tabindex="0"
  >
    {{ text }}
  </span>
</template>

<style scoped>
.explained-label {
  text-decoration-line: underline;
  text-decoration-style: dotted;
  text-decoration-color: var(--color-text-muted);
  text-underline-offset: 0.2em;
  cursor: help;
}

.explained-label:focus-visible {
  outline: 1px solid var(--color-accent);
  outline-offset: 2px;
  border-radius: 1px;
}
</style>
