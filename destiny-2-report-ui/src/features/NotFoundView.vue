<script setup lang="ts">
import AppButton from '@/components/base/AppButton.vue'
</script>

<template>
  <div class="not-found container">
    <p class="code display tnum">404</p>
    <h1 class="title">This page doesn't exist</h1>
    <p class="copy">The link may be old, or the address may have a typo.</p>
    <AppButton class="action" :to="{ name: 'home' }">Back to search</AppButton>
  </div>
</template>

<style scoped>
.not-found {
  padding-top: var(--space-8);
  display: flex;
  flex-direction: column;
  align-items: center;
  text-align: center;
}

.code {
  font-size: var(--text-3xl);
  color: var(--color-text-muted);
  font-weight: 700;
}

.title {
  margin-top: var(--space-3);
  font-size: var(--text-xl);
}

.copy {
  margin-top: var(--space-2);
  color: var(--color-text-secondary);
  font-size: var(--text-sm);
}

.action {
  margin-top: var(--space-5);
}
</style>
