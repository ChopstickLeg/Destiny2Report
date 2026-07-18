<script setup lang="ts">
defineProps<{
  title: string
  /** Anchor id for in-page navigation. */
  id?: string
  subtitle?: string
}>()
</script>

<template>
  <section :id="id" class="section">
    <header class="section-header">
      <h2 class="section-title">{{ title }}</h2>
      <p v-if="subtitle" class="section-subtitle">{{ subtitle }}</p>
      <div v-if="$slots.actions" class="section-actions">
        <slot name="actions" />
      </div>
    </header>
    <slot />
  </section>
</template>

<style scoped>
.section {
  padding-top: var(--space-7);
}

.section-header {
  display: grid;
  grid-template-columns: 1fr auto;
  align-items: baseline;
  row-gap: var(--space-1);
  margin-bottom: var(--space-4);
  padding-bottom: var(--space-2);
  border-bottom: 1px solid var(--color-border);
}

.section-title {
  font-size: var(--text-lg);
}

.section-subtitle {
  grid-column: 1;
  font-size: var(--text-sm);
  color: var(--color-text-secondary);
}

.section-actions {
  grid-column: 2;
  grid-row: 1;
}
</style>
