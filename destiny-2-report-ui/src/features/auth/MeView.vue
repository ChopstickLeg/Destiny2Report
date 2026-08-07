<script setup lang="ts">
import { computed, watch } from 'vue'
import { useRouter } from 'vue-router'
import AppButton from '@/components/base/AppButton.vue'
import SkeletonBlock from '@/components/base/SkeletonBlock.vue'
import { useSessionStore } from '@/stores/session'
import MembershipSelector from './MembershipSelector.vue'

const router = useRouter()
const session = useSessionStore()

const resolving = computed(() => session.status === 'unknown' || session.status === 'resolving')

// The signed-in experience always follows Bungie's primary membership.
watch(
  () => [session.status, session.activeMembership] as const,
  ([status, membership]) => {
    if (status !== 'signed-in' || !membership) return
    void router.replace({
      name: 'report-overview',
      params: {
        membershipTypeId: membership.membershipType,
        membershipId: membership.membershipId,
      },
    })
  },
  { immediate: true },
)

function signIn() {
  session.beginSignIn('/me')
}
</script>

<template>
  <div class="me container">
    <div v-if="resolving" class="me-loading">
      <SkeletonBlock height="2rem" width="14rem" />
      <SkeletonBlock height="3.5rem" />
    </div>

    <div v-else-if="!session.isSignedIn" class="me-panel">
      <h1 class="me-title">Your report</h1>
      <p class="me-copy">
        Sign in with Bungie to open your report and view “Your Story,” a guided summary of your
        Destiny 2 history.
      </p>
      <AppButton v-if="session.signInAvailable" class="me-action" variant="primary" @click="signIn">
        Sign in with Bungie
      </AppButton>
      <p v-else class="me-copy me-unconfigured">
        Sign-in isn't configured in this deployment. You can still search for your Bungie name and
        view your public report.
      </p>
    </div>

    <MembershipSelector v-else-if="session.needsMembershipSelection" />

    <div v-else class="me-panel">
      <h1 class="me-title">Your report is unavailable</h1>
      <p class="me-copy">No Destiny membership could be resolved for your Bungie account.</p>
    </div>
  </div>
</template>

<style scoped>
.me {
  padding-top: var(--space-7);
  max-width: 40rem;
}

.me-loading {
  display: flex;
  flex-direction: column;
  gap: var(--space-3);
}

.me-title {
  font-size: var(--text-xl);
  margin-bottom: var(--space-3);
}

.me-copy {
  color: var(--color-text-secondary);
  font-size: var(--text-sm);
}

.me-action {
  margin-top: var(--space-5);
}

.me-unconfigured {
  margin-top: var(--space-3);
}
</style>
