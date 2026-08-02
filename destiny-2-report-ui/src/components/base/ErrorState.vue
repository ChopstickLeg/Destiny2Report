<script setup lang="ts">
import { computed } from 'vue'
import { getErrorMessage } from '@/lib/api/http'
import AppButton from './AppButton.vue'

const props = defineProps<{
  error: unknown
  /** Context prefix, e.g. "Couldn't load weapons". */
  context?: string
}>()

const emit = defineEmits<{ retry: [] }>()

const message = computed(() =>
  getErrorMessage(props.error, 'Something went wrong loading this data.'),
)
</script>

<template>
  <div class="error" role="alert">
    <p class="error-title">{{ context ?? "Couldn't load this section" }}</p>
    <p class="error-message">{{ message }}</p>
    <AppButton size="sm" @click="emit('retry')">Try again</AppButton>
  </div>
</template>

<style scoped>
.error {
  padding: var(--space-5) var(--space-4);
  border: 1px solid var(--color-border);
  border-left: 3px solid var(--color-negative);
  border-radius: var(--radius-md);
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: var(--space-2);
}

.error-title {
  font-weight: 550;
}

.error-message {
  font-size: var(--text-sm);
  color: var(--color-text-secondary);
}
</style>
