<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { RouterLink, useRoute, useRouter } from 'vue-router'
import { useSessionStore } from '@/stores/session'
import { bungieUrl } from '@/lib/api/bungie'
import type { DestinyMembership } from '@/lib/api/types'
import { platformLabel } from '@/lib/platform'

const session = useSessionStore()
const route = useRoute()
const router = useRouter()

const open = ref(false)
const rootEl = ref<HTMLElement | null>(null)

const avatarUrl = computed(() => bungieUrl(session.profile?.bungieNetUser?.profilePicturePath))
const active = computed(() => session.activeMembership)

const reportLink = computed(() => {
  const membership = active.value
  if (!membership) return null
  return {
    name: 'report-overview',
    params: {
      membershipTypeId: membership.membershipType,
      membershipId: membership.membershipId,
    },
  }
})

function toggle() {
  open.value = !open.value
}

function close() {
  open.value = false
}

function onDocumentClick(event: MouseEvent) {
  if (open.value && rootEl.value && !rootEl.value.contains(event.target as Node)) close()
}

function onKeydown(event: KeyboardEvent) {
  if (event.key === 'Escape' && open.value) {
    close()
    ;(rootEl.value?.querySelector('.account-trigger') as HTMLElement | null)?.focus()
  }
}

onMounted(() => {
  document.addEventListener('click', onDocumentClick)
  document.addEventListener('keydown', onKeydown)
})

onBeforeUnmount(() => {
  document.removeEventListener('click', onDocumentClick)
  document.removeEventListener('keydown', onKeydown)
})

function signIn() {
  session.beginSignIn(route.fullPath)
}

function signOut() {
  close()
  session.signOut()
}

function chooseMembership(membership: DestinyMembership) {
  session.selectMembership(membership)
  close()
  void router.push({
    name: 'report-overview',
    params: {
      membershipTypeId: membership.membershipType,
      membershipId: membership.membershipId,
    },
  })
}
</script>

<template>
  <div ref="rootEl" class="account">
    <!-- Resolving: stable-width placeholder, no flashing label. -->
    <div
      v-if="session.status === 'unknown' || session.status === 'resolving'"
      class="account-skeleton"
      aria-hidden="true"
    />

    <button
      v-else-if="!session.isSignedIn"
      v-show="session.signInAvailable"
      class="sign-in"
      type="button"
      @click="signIn"
    >
      Sign in with Bungie
    </button>

    <template v-else>
      <button
        class="account-trigger"
        type="button"
        :aria-expanded="open"
        aria-haspopup="true"
        @click="toggle"
      >
        <img v-if="avatarUrl" class="avatar" :src="avatarUrl" alt="" width="24" height="24" />
        <span v-else class="avatar avatar--fallback" aria-hidden="true" />
        <span class="account-name">{{ session.displayName ?? 'Account' }}</span>
        <svg class="chevron" viewBox="0 0 12 12" aria-hidden="true">
          <path d="M2.5 4.5 L6 8 L9.5 4.5" fill="none" stroke="currentColor" stroke-width="1.5" />
        </svg>
      </button>

      <div v-if="open" class="menu" role="menu">
        <template v-if="session.selectableMemberships.length > 1">
          <p class="menu-label">Destiny profile</p>
          <button
            v-for="membership in session.selectableMemberships"
            :key="`${membership.membershipType}:${membership.membershipId}`"
            class="menu-item profile-item"
            role="menuitemradio"
            type="button"
            :aria-checked="session.activeMembership?.membershipId === membership.membershipId"
            @click="chooseMembership(membership)"
          >
            <span>
              {{ platformLabel(membership.membershipType) }}
              <small>{{ membership.displayName || 'Destiny profile' }}</small>
            </span>
            <span
              v-if="session.activeMembership?.membershipId === membership.membershipId"
              aria-hidden="true"
              >✓</span
            >
          </button>
          <div class="menu-rule" role="presentation" />
        </template>

        <RouterLink
          v-if="reportLink"
          class="menu-item"
          role="menuitem"
          :to="reportLink"
          @click="close"
        >
          My report
        </RouterLink>
        <RouterLink class="menu-item" role="menuitem" :to="{ name: 'story' }" @click="close">
          Your Story
        </RouterLink>
        <RouterLink
          v-if="session.isAdmin"
          class="menu-item"
          role="menuitem"
          :to="{ name: 'admin' }"
          @click="close"
        >
          Crawl operations
        </RouterLink>

        <div class="menu-rule" role="presentation" />
        <button class="menu-item" role="menuitem" type="button" @click="signOut">Sign out</button>
      </div>
    </template>
  </div>
</template>

<style scoped>
.account {
  position: relative;
  margin-left: auto;
  flex: none;
}

.account-skeleton {
  width: 8.5rem;
  height: 2.25rem;
  border-radius: var(--radius-md);
  background: var(--color-surface);
}

.sign-in,
.account-trigger {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  height: 2.25rem;
  padding: 0 var(--space-3);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-md);
  font-size: var(--text-sm);
  font-weight: 500;
  color: var(--color-text);
  background: var(--color-surface);
  transition: border-color var(--transition-fast);
}

.sign-in:hover,
.account-trigger:hover {
  border-color: var(--color-border-strong);
  color: var(--color-text);
}

.avatar {
  width: 1.5rem;
  height: 1.5rem;
  border-radius: var(--radius-sm);
  object-fit: cover;
}

.avatar--fallback {
  background: var(--color-surface-raised);
}

.account-name {
  max-width: 10rem;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.chevron {
  width: 0.75rem;
  height: 0.75rem;
  color: var(--color-text-muted);
}

.menu {
  position: absolute;
  top: calc(100% + var(--space-1));
  right: 0;
  z-index: 40;
  min-width: 13rem;
  padding: var(--space-1);
  background: var(--color-surface-raised);
  border: 1px solid var(--color-border-strong);
  border-radius: var(--radius-md);
  box-shadow: var(--shadow-raised);
}

.menu-item {
  display: flex;
  align-items: center;
  justify-content: space-between;
  width: 100%;
  padding: var(--space-2) var(--space-3);
  border-radius: var(--radius-sm);
  font-size: var(--text-sm);
  color: var(--color-text);
  text-align: left;
}

.menu-item:hover {
  background: var(--color-surface);
  color: var(--color-text);
}

.menu-label {
  padding: var(--space-2) var(--space-3) var(--space-1);
  color: var(--color-text-muted);
  font-size: var(--text-xs);
  font-weight: 600;
  letter-spacing: 0.06em;
  text-transform: uppercase;
}

.profile-item > span:first-child {
  display: grid;
  gap: 0.1rem;
  min-width: 0;
}

.profile-item small {
  max-width: 11rem;
  overflow: hidden;
  color: var(--color-text-muted);
  font-size: 0.7rem;
  font-weight: 400;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.menu-rule {
  height: 1px;
  margin: var(--space-1) 0;
  background: var(--color-border);
}
</style>
