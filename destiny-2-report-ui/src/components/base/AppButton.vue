<script setup lang="ts">
import { RouterLink, type RouteLocationRaw } from 'vue-router'

withDefaults(
  defineProps<{
    variant?: 'primary' | 'secondary' | 'ghost'
    size?: 'md' | 'sm'
    disabled?: boolean
    /** Render as a router link with button styling. */
    to?: RouteLocationRaw
    type?: 'button' | 'submit'
  }>(),
  { variant: 'secondary', size: 'md', disabled: false, type: 'button' },
)
</script>

<template>
  <RouterLink
    v-if="to && !disabled"
    class="btn"
    :class="[`btn--${variant}`, `btn--${size}`]"
    :to="to"
  >
    <slot />
  </RouterLink>
  <button
    v-else
    class="btn"
    :class="[`btn--${variant}`, `btn--${size}`]"
    :type="type"
    :disabled="disabled"
  >
    <slot />
  </button>
</template>

<style scoped>
.btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: var(--space-2);
  height: 2.5rem;
  padding: 0 var(--space-4);
  border-radius: var(--radius-md);
  border: 1px solid transparent;
  font-size: var(--text-sm);
  font-weight: 550;
  white-space: nowrap;
  transition:
    background-color var(--transition-fast),
    border-color var(--transition-fast),
    color var(--transition-fast);
}

.btn--sm {
  height: 2rem;
  padding: 0 var(--space-3);
  font-size: var(--text-xs);
}

.btn--primary {
  background: var(--color-accent);
  color: var(--color-text-inverse);
}

.btn--primary:hover {
  background: var(--color-accent-strong);
  color: var(--color-text-inverse);
}

.btn--secondary {
  background: var(--color-surface);
  border-color: var(--color-border-strong);
  color: var(--color-text);
}

.btn--secondary:hover {
  background: var(--color-surface-raised);
  color: var(--color-text);
}

.btn--ghost {
  color: var(--color-text-secondary);
}

.btn--ghost:hover {
  color: var(--color-text);
  background: var(--color-surface);
}

.btn:disabled {
  opacity: 0.55;
  cursor: not-allowed;
}
</style>
