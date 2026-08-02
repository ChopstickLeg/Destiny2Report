<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import AppButton from '@/components/base/AppButton.vue'
import { getErrorMessage } from '@/lib/api/http'
import { useSessionStore } from '@/stores/session'

const route = useRoute()
const router = useRouter()
const session = useSessionStore()

type CallbackState = 'working' | 'denied' | 'error'
const state = ref<CallbackState>('working')
const errorMessage = ref('')

onMounted(async () => {
  // Bungie sends ?error= when the user declines authorization.
  if (typeof route.query.error === 'string') {
    state.value = 'denied'
    return
  }

  const code = typeof route.query.code === 'string' ? route.query.code : null
  const oauthState = typeof route.query.state === 'string' ? route.query.state : null

  if (!code) {
    state.value = 'error'
    errorMessage.value = 'No authorization code was returned by Bungie.'
    return
  }

  try {
    const returnTo = await session.completeSignIn(code, oauthState)
    await router.replace(returnTo)
    session.showStoryPrompt()
  } catch (error) {
    state.value = 'error'
    errorMessage.value = getErrorMessage(error, 'Sign-in could not be completed.')
  }
})

function retry() {
  session.beginSignIn('/me')
}
</script>

<template>
  <div class="callback container">
    <div class="callback-panel" role="status" aria-live="polite">
      <template v-if="state === 'working'">
        <h1 class="callback-title">Signing you in…</h1>
        <p class="callback-copy">Exchanging the Bungie authorization and loading your profile.</p>
      </template>

      <template v-else-if="state === 'denied'">
        <h1 class="callback-title">Sign-in cancelled</h1>
        <p class="callback-copy">
          You declined the Bungie authorization, so nothing was connected. You can keep using public
          reports without signing in.
        </p>
        <div class="callback-actions">
          <AppButton :to="{ name: 'home' }">Back to search</AppButton>
        </div>
      </template>

      <template v-else>
        <h1 class="callback-title">Sign-in failed</h1>
        <p class="callback-copy">{{ errorMessage }}</p>
        <div class="callback-actions">
          <AppButton variant="primary" @click="retry">Try again</AppButton>
          <AppButton variant="ghost" :to="{ name: 'home' }">Back to search</AppButton>
        </div>
      </template>
    </div>
  </div>
</template>

<style scoped>
.callback {
  display: flex;
  justify-content: center;
  padding-top: var(--space-8);
}

.callback-panel {
  max-width: 28rem;
  width: 100%;
  padding: var(--space-6);
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-md);
}

.callback-title {
  font-size: var(--text-xl);
  margin-bottom: var(--space-3);
}

.callback-copy {
  font-size: var(--text-sm);
  color: var(--color-text-secondary);
}

.callback-actions {
  margin-top: var(--space-5);
  display: flex;
  gap: var(--space-3);
}
</style>
