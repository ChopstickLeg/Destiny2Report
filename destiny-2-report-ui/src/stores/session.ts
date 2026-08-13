/**
 * Bungie session state.
 *
 * Bungie tokens remain on the backend. The browser receives only an
 * HttpOnly session cookie that expires after 30 days.
 *
 * OAuth `state` is generated here, stored in sessionStorage alongside the
 * intended return path, and validated on the callback route.
 */

import { defineStore } from 'pinia'
import { exchangeOAuthCode, fetchWhoAmI, signOut as deleteSession } from '@/lib/api/auth'
import type { DestinyMembership, SignedInPlayerResponse } from '@/lib/api/types'
import { rememberQueueTicket } from '@/lib/queue-tickets'

const OAUTH_STATE_KEY = 'd2r.oauth-state'
const ACTIVE_MEMBERSHIP_KEY = 'd2r.active-membership'

const AUTHORIZE_URL =
  import.meta.env.VITE_BUNGIE_AUTHORIZE_URL ?? 'https://www.bungie.net/en/OAuth/Authorize'
const CLIENT_ID = import.meta.env.VITE_BUNGIE_CLIENT_ID

interface PendingOAuth {
  state: string
  returnTo: string
}

type SessionStatus = 'unknown' | 'resolving' | 'signed-in' | 'signed-out'

export const useSessionStore = defineStore('session', {
  state: () => ({
    status: 'unknown' as SessionStatus,
    profile: null as SignedInPlayerResponse | null,
    selectedMembershipKey: null as string | null,
    storyPromptOpen: false,
    storyPromptPending: false,
  }),

  getters: {
    isSignedIn: (state) => state.status === 'signed-in',
    isAdmin: (state) => state.status === 'signed-in' && state.profile?.isAdmin === true,
    signInAvailable: () => Boolean(CLIENT_ID),

    displayName(state): string | null {
      const user = state.profile?.bungieNetUser
      return user?.cachedBungieGlobalDisplayName ?? user?.displayName ?? null
    },

    activeMembership(state): DestinyMembership | null {
      const memberships = state.profile?.destinyMemberships ?? []
      const primary = state.profile?.primaryDestinyMembership
      if (primary) return primary

      const selectable = memberships.filter(isValidMembership)
      if (selectable.length === 1) return selectable[0] ?? null
      return (
        selectable.find(
          (membership) => membershipKey(membership) === state.selectedMembershipKey,
        ) ?? null
      )
    },

    selectableMemberships(state): DestinyMembership[] {
      const primary = state.profile?.primaryDestinyMembership
      return primary
        ? [primary]
        : (state.profile?.destinyMemberships ?? []).filter(isValidMembership)
    },

    needsMembershipSelection(): boolean {
      return this.selectableMemberships.length > 1 && this.activeMembership === null
    },
  },

  actions: {
    async bootstrap() {
      if (this.status !== 'unknown') return
      this.status = 'resolving'
      try {
        const profile = await fetchWhoAmI()
        if (profile.signedIn) {
          this.profile = profile
          this.rememberMembershipQueueTickets(profile)
          this.restoreMembershipSelection()
          this.status = 'signed-in'
        } else {
          this.clearSession()
        }
      } catch {
        // whoami unavailable; keep the server session and allow a later retry.
        this.status = 'signed-out'
      }
    },

    /** Redirects the browser to Bungie's authorization page. */
    beginSignIn(returnTo: string) {
      if (!CLIENT_ID) return
      const pending: PendingOAuth = { state: crypto.randomUUID(), returnTo }
      sessionStorage.setItem(OAUTH_STATE_KEY, JSON.stringify(pending))

      const url = new URL(AUTHORIZE_URL)
      url.searchParams.set('client_id', CLIENT_ID)
      url.searchParams.set('response_type', 'code')
      url.searchParams.set('state', pending.state)
      window.location.assign(url.toString())
    },

    /**
     * Exchange the callback code. Returns the stored return path on
     * success; throws on state mismatch or exchange failure.
     */
    async completeSignIn(code: string, state: string | null): Promise<string> {
      const raw = sessionStorage.getItem(OAUTH_STATE_KEY)
      sessionStorage.removeItem(OAUTH_STATE_KEY)
      const pending = raw ? (JSON.parse(raw) as PendingOAuth) : null

      if (!pending || !state || state !== pending.state) {
        throw new Error('Sign-in state did not match. Please try signing in again.')
      }

      const redirectUri = `${window.location.origin}/auth/callback`
      this.status = 'resolving'
      const profile = await exchangeOAuthCode(code, redirectUri)
      if (!profile.signedIn) {
        this.clearSession()
        throw new Error('Bungie accepted the sign-in but no profile was returned.')
      }
      this.profile = profile
      this.rememberMembershipQueueTickets(profile)
      this.restoreMembershipSelection()
      this.status = 'signed-in'
      return pending.returnTo || '/me'
    },

    showStoryPrompt() {
      if (this.needsMembershipSelection) {
        this.storyPromptPending = true
        return
      }
      this.storyPromptOpen = true
    },

    dismissStoryPrompt() {
      this.storyPromptOpen = false
      this.storyPromptPending = false
    },

    selectMembership(membership: DestinyMembership) {
      if (
        !this.selectableMemberships.some(
          (candidate) => membershipKey(candidate) === membershipKey(membership),
        )
      ) {
        return
      }

      this.selectedMembershipKey = membershipKey(membership)
      localStorage.setItem(ACTIVE_MEMBERSHIP_KEY, this.selectedMembershipKey)
      if (this.storyPromptPending) {
        this.storyPromptPending = false
        this.storyPromptOpen = true
      }
    },

    restoreMembershipSelection() {
      const stored = localStorage.getItem(ACTIVE_MEMBERSHIP_KEY)
      this.selectedMembershipKey = this.selectableMemberships.some(
        (membership) => membershipKey(membership) === stored,
      )
        ? stored
        : null
    },

    rememberMembershipQueueTickets(profile: SignedInPlayerResponse) {
      for (const membership of profile.destinyMemberships) {
        rememberQueueTicket(
          { membershipTypeId: membership.membershipType, membershipId: membership.membershipId },
          membership.queueTicket,
        )
      }
    },

    async signOut() {
      try {
        await deleteSession()
      } finally {
        this.clearSession()
      }
    },

    clearSession() {
      this.profile = null
      this.selectedMembershipKey = null
      this.storyPromptOpen = false
      this.storyPromptPending = false
      this.status = 'signed-out'
    },
  },
})

function isValidMembership(membership: DestinyMembership): boolean {
  return (
    membership.membershipType > 0 &&
    /^\d+$/.test(membership.membershipId) &&
    membership.membershipId !== '0'
  )
}

export function membershipKey(membership: DestinyMembership): string {
  return `${membership.membershipType}:${membership.membershipId}`
}
