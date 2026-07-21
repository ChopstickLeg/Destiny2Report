<script setup lang="ts">
import { RouterLink } from 'vue-router'
</script>

<template>
  <footer class="footer">
    <div class="container footer-row">
      <p class="footer-note">
        Destiny 2 Report is an independent statistics project. Destiny content and imagery are the
        property of Bungie, Inc.
      </p>
      <nav class="footer-nav" aria-label="Footer">
        <RouterLink class="footer-link" :to="{ name: 'status' }">Service status</RouterLink>
      </nav>
    </div>
  </footer>
</template>

<style scoped>
.footer {
  width: 100%;
  border-top: 1px solid var(--color-border);
  padding-block: var(--space-5);
  margin-top: auto;
}

.footer-row {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: var(--space-4);
  flex-wrap: wrap;
  max-width: none;
}

.footer-note {
  font-size: var(--text-xs);
  color: var(--color-text-muted);
  max-width: 40rem;
}

.footer-link {
  font-size: var(--text-xs);
  color: var(--color-text-secondary);
}
</style>
