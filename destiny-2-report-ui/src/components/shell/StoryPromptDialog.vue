<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref, type ComponentPublicInstance } from 'vue'
import { useRouter } from 'vue-router'
import AppButton from '@/components/base/AppButton.vue'
import { useSessionStore } from '@/stores/session'

const session = useSessionStore()
const router = useRouter()
const primaryAction = ref<ComponentPublicInstance | null>(null)

function dismiss() {
  session.dismissStoryPrompt()
}

async function viewStory() {
  dismiss()
  await router.push({ name: 'story' })
}

function onKeydown(event: KeyboardEvent) {
  if (event.key === 'Escape') dismiss()
}

onMounted(() => {
  document.addEventListener('keydown', onKeydown)
  void nextTick(() => (primaryAction.value?.$el as HTMLElement | undefined)?.focus())
})

onBeforeUnmount(() => document.removeEventListener('keydown', onKeydown))
</script>

<template>
  <Teleport to="body">
    <div class="story-prompt-backdrop" @click.self="dismiss">
      <section
        class="story-prompt"
        role="dialog"
        aria-modal="true"
        aria-labelledby="story-prompt-title"
        aria-describedby="story-prompt-copy"
      >
        <p class="story-prompt-kicker">Your Destiny 2 history</p>
        <h2 id="story-prompt-title" class="story-prompt-title display">Review your highlights</h2>
        <p id="story-prompt-copy" class="story-prompt-copy">
          Take a look back at the moments, milestones, and people that shaped your Destiny 2
          history.
        </p>
        <div class="story-prompt-actions">
          <AppButton ref="primaryAction" variant="primary" @click="viewStory">
            View my story
          </AppButton>
          <AppButton variant="ghost" @click="dismiss">Maybe later</AppButton>
        </div>
      </section>
    </div>
  </Teleport>
</template>

<style scoped>
.story-prompt-backdrop {
  position: fixed;
  inset: 0;
  z-index: 100;
  display: grid;
  place-items: center;
  padding: var(--content-pad);
  background: rgb(8 6 7 / 0.72);
}

.story-prompt {
  width: min(100%, 30rem);
  padding: var(--space-6);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-md);
  background: var(--color-surface-raised);
  box-shadow: var(--shadow-raised);
}

.story-prompt-kicker {
  margin-bottom: var(--space-2);
  color: var(--color-text-muted);
  font-size: var(--text-xs);
  font-weight: 600;
  letter-spacing: 0.08em;
  text-transform: uppercase;
}

.story-prompt-title {
  font-size: var(--text-2xl);
}

.story-prompt-copy {
  margin-top: var(--space-3);
  color: var(--color-text-secondary);
}

.story-prompt-actions {
  display: flex;
  flex-wrap: wrap;
  gap: var(--space-2);
  margin-top: var(--space-5);
}
</style>
