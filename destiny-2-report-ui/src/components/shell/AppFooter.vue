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
      <div class="creator">
        <p class="creator-label">Made by <span>Chopstickleg</span></p>
        <nav class="creator-links" aria-label="Chopstickleg on social media">
          <a href="https://x.com/Chopstickleg">X (Twitter)</a>
          <a href="https://github.com/ChopstickLeg">GitHub</a>
        </nav>
        <RouterLink class="faq-link" :to="{ name: 'faq' }">Frequently asked questions</RouterLink>
      </div>
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
  align-items: flex-start;
  justify-content: space-between;
  gap: var(--space-6);
  flex-wrap: wrap;
  max-width: none;
}

.footer-note {
  font-size: var(--text-xs);
  color: var(--color-text-muted);
  max-width: 40rem;
}

.creator {
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  gap: var(--space-2);
  text-align: right;
}

.creator-label {
  color: var(--color-text-secondary);
  font-size: var(--text-xs);
}

.creator-label span {
  color: var(--color-text);
  font-weight: 600;
}

.creator-links {
  display: flex;
  align-items: center;
  gap: var(--space-3);
}

.creator-links a,
.faq-link {
  font-size: var(--text-xs);
  color: var(--color-text-secondary);
}

.creator-links a {
  text-decoration: underline;
  text-decoration-color: var(--color-border-strong);
  text-underline-offset: 0.2em;
}

.faq-link {
  color: var(--color-accent-strong);
}

@media (max-width: 40rem) {
  .creator {
    align-items: flex-start;
    text-align: left;
  }
}
</style>
