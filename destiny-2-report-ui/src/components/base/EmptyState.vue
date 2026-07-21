<script setup lang="ts">
defineProps<{
  title: string
  description?: string
}>()
</script>

<template>
  <div class="empty">
    <p class="empty-title">{{ title }}</p>
    <p v-if="description" class="empty-description">{{ description }}</p>
    <div v-if="$slots.default" class="empty-actions">
      <slot />
    </div>
  </div>
</template>

<style scoped>
.empty {
  padding: var(--space-6) var(--space-4);
  text-align: center;
  border: 1px dashed var(--color-border);
  border-radius: var(--radius-md);
}

.empty-title {
  font-weight: 550;
}

.empty-description {
  margin-top: var(--space-1);
  font-size: var(--text-sm);
  color: var(--color-text-secondary);
  max-width: 32rem;
  margin-inline: auto;
}

.empty-actions {
  margin-top: var(--space-4);
  display: flex;
  justify-content: center;
  gap: var(--space-3);
}
</style>
