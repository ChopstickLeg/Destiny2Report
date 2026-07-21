<script setup lang="ts">
import { onMounted } from 'vue'
import { RouterView } from 'vue-router'
import AppHeader from '@/components/shell/AppHeader.vue'
import AppFooter from '@/components/shell/AppFooter.vue'
import StoryPromptDialog from '@/components/shell/StoryPromptDialog.vue'
import { useSessionStore } from '@/stores/session'

const session = useSessionStore()
onMounted(() => {
  void session.bootstrap()
})
</script>

<template>
  <a class="skip-link" href="#main">Skip to content</a>
  <AppHeader />
  <main id="main" class="app-main">
    <RouterView />
  </main>
  <AppFooter />
  <StoryPromptDialog v-if="session.storyPromptOpen" />
</template>

<style scoped>
.app-main {
  flex: 1;
  display: flex;
  flex-direction: column;
  padding-bottom: var(--space-8);
}

.skip-link {
  position: absolute;
  top: var(--space-2);
  left: var(--space-2);
  z-index: 100;
  padding: var(--space-2) var(--space-3);
  background: var(--color-surface-raised);
  border: 1px solid var(--color-border-strong);
  border-radius: var(--radius-sm);
  transform: translateY(-200%);
}

.skip-link:focus {
  transform: none;
}
</style>
