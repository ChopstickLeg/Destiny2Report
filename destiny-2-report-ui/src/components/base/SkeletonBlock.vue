<script setup lang="ts">
withDefaults(
  defineProps<{
    width?: string
    height: string
    radius?: string
  }>(),
  { width: '100%', radius: 'var(--radius-sm)' },
)
</script>

<template>
  <div class="skeleton" :style="{ width, height, borderRadius: radius }" aria-hidden="true" />
</template>

<style scoped>
.skeleton {
  background: var(--color-surface);
  position: relative;
  overflow: hidden;
}

.skeleton::after {
  content: '';
  position: absolute;
  inset: 0;
  background: linear-gradient(90deg, transparent, rgb(255 255 255 / 0.04), transparent);
  animation: sweep 1.6s infinite;
}

@keyframes sweep {
  from {
    transform: translateX(-100%);
  }
  to {
    transform: translateX(100%);
  }
}

@media (prefers-reduced-motion: reduce) {
  .skeleton::after {
    animation: none;
  }
}
</style>
