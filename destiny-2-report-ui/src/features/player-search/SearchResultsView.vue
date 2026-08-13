<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useQuery } from '@tanstack/vue-query'
import PlayerIdentityRow from '@/components/base/PlayerIdentityRow.vue'
import EmptyState from '@/components/base/EmptyState.vue'
import ErrorState from '@/components/base/ErrorState.vue'
import SkeletonBlock from '@/components/base/SkeletonBlock.vue'
import { searchPlayers } from '@/lib/api/players'
import type { PlayerSearchResult } from '@/lib/api/types'
import { platformLabel } from '@/lib/platform'
import { rememberPlayer } from '@/lib/recent-players'
import { rememberQueueTicket } from '@/lib/queue-tickets'
import { filterByCode, isSearchable, parseSearchQuery } from './search-utils'

const route = useRoute()
const router = useRouter()

const input = ref(typeof route.query.q === 'string' ? route.query.q : '')

// Keep the field in sync when navigation changes the query externally.
watch(
  () => route.query.q,
  (q) => {
    if (typeof q === 'string' && q !== input.value) input.value = q
  },
)

// Debounce typing into the shareable ?q= URL; Enter submits immediately.
let debounceTimer: ReturnType<typeof setTimeout> | undefined
watch(input, (value) => {
  clearTimeout(debounceTimer)
  debounceTimer = setTimeout(() => commitQuery(value), 300)
})

function commitQuery(value: string) {
  clearTimeout(debounceTimer)
  const q = value.trim()
  if (q === (route.query.q ?? '')) return
  void router.replace({ name: 'search', query: q ? { q } : {} })
}

const parsed = computed(() =>
  parseSearchQuery(typeof route.query.q === 'string' ? route.query.q : ''),
)
const enabled = computed(() => isSearchable(parsed.value))

const query = useQuery({
  queryKey: computed(() => ['player-search', parsed.value.prefix, parsed.value.code] as const),
  queryFn: ({ signal }) => searchPlayers(parsed.value.prefix, parsed.value.code, signal),
  enabled,
  staleTime: 30_000,
  placeholderData: (previous) => previous,
})

const results = computed<PlayerSearchResult[]>(() =>
  filterByCode(query.data.value ?? [], parsed.value.code),
)

function remember(result: PlayerSearchResult) {
  rememberQueueTicket(
    { membershipTypeId: result.membershipTypeId, membershipId: result.membershipId },
    result.queueTicket,
  )
  rememberPlayer({
    membershipTypeId: result.membershipTypeId,
    membershipId: result.membershipId,
    displayName: result.displayName,
    displayCode: result.displayCode,
    emblemIconUrl: result.emblemIconUrl,
  })
}
</script>

<template>
  <div class="search-page container">
    <h1 class="page-title">Find a player</h1>

    <form
      class="search-form"
      role="search"
      aria-label="Player search"
      @submit.prevent="commitQuery(input)"
    >
      <input
        v-model="input"
        class="search-input"
        type="search"
        placeholder="Bungie name, e.g. Guardian#1234"
        aria-label="Search players by Bungie name"
        autocomplete="off"
        autocapitalize="off"
        spellcheck="false"
        enterkeyhint="search"
      />
    </form>

    <p v-if="!enabled" class="search-hint">
      Type at least two characters of a Bungie display name.
    </p>

    <div
      v-else
      class="search-results"
      aria-live="polite"
      :aria-busy="query.isFetching.value"
    >
      <template v-if="query.isFetching.value">
        <div class="result-skeletons">
          <SkeletonBlock v-for="n in 4" :key="n" height="3.5rem" radius="var(--radius-md)" />
        </div>
      </template>

      <ErrorState
        v-else-if="query.isError.value"
        :error="query.error.value"
        context="Search is unavailable"
        @retry="query.refetch()"
      />

      <EmptyState
        v-else-if="results.length === 0"
        title="No players matched"
        :description="`Nothing came back for “${parsed.prefix}”. Check the spelling. Bungie names match from the start of the name.`"
      />

      <template v-else>
        <p class="result-count" role="status">
          {{ results.length }} {{ results.length === 1 ? 'player' : 'players' }} found. A result
          here doesn't mean a report exists yet. You can generate one from their page.
        </p>
        <ul class="result-list">
          <li v-for="result in results" :key="`${result.membershipTypeId}-${result.membershipId}`">
            <PlayerIdentityRow
              :name="result.displayName"
              :code="result.displayCode"
              :detail="platformLabel(result.membershipTypeId)"
              :emblem-url="result.emblemIconUrl || null"
              :to="{
                name: 'report-overview',
                params: {
                  membershipTypeId: result.membershipTypeId,
                  membershipId: result.membershipId,
                },
              }"
              @click="remember(result)"
            />
          </li>
        </ul>
      </template>
    </div>
  </div>
</template>

<style scoped>
.search-page {
  padding-top: var(--space-6);
}

.page-title {
  font-size: var(--text-xl);
}

.search-form {
  margin-top: var(--space-4);
  max-width: 34rem;
}

.search-input {
  width: 100%;
  height: 3rem;
  padding: 0 var(--space-4);
  background: var(--color-surface);
  border: 1px solid var(--color-border-strong);
  border-radius: var(--radius-md);
  font-size: var(--text-md);
}

.search-input::placeholder {
  color: var(--color-text-muted);
}

.search-input::-webkit-search-cancel-button {
  -webkit-appearance: none;
}

.search-hint {
  margin-top: var(--space-4);
  font-size: var(--text-sm);
  color: var(--color-text-muted);
}

.search-results {
  margin-top: var(--space-5);
  max-width: 34rem;
}

.result-skeletons {
  display: flex;
  flex-direction: column;
  gap: var(--space-2);
}

.result-count {
  font-size: var(--text-xs);
  color: var(--color-text-muted);
  margin-bottom: var(--space-2);
}

.result-list {
  list-style: none;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 2px;
}
</style>
