<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useQuery } from '@tanstack/vue-query'
import AppButton from '@/components/base/AppButton.vue'
import AppSelect from '@/components/base/AppSelect.vue'
import EmptyState from '@/components/base/EmptyState.vue'
import ErrorState from '@/components/base/ErrorState.vue'
import SegmentedControl from '@/components/base/SegmentedControl.vue'
import SkeletonBlock from '@/components/base/SkeletonBlock.vue'
import { bungieUrl } from '@/lib/api/bungie'
import { fetchLeaderboard, fetchLeaderboardCatalog, leaderboardKeys } from '@/lib/api/leaderboards'
import type { LeaderboardEntry } from '@/lib/api/types'
import { formatHours } from '@/lib/formatting/duration'
import { formatInteger } from '@/lib/formatting/numbers'
import {
  findLeaderboardChoice,
  findLeaderboardCollection,
  organizeLeaderboards,
} from './leaderboard-collections'
import LeaderboardWarmingState from './LeaderboardWarmingState.vue'

const PAGE_SIZE = 50
const route = useRoute()
const router = useRouter()
const search = ref('')

const catalogQuery = useQuery({
  queryKey: leaderboardKeys.catalog,
  queryFn: ({ signal }) => fetchLeaderboardCatalog(signal),
  staleTime: 60_000,
})

const allBoards = computed(() => catalogQuery.data.value?.leaderboards ?? [])
const collections = computed(() => organizeLeaderboards(allBoards.value))
const requestedKey = computed(() =>
  typeof route.query.board === 'string' ? route.query.board : '',
)
const activeCollectionKey = computed(() =>
  findLeaderboardCollection(collections.value, requestedKey.value),
)
const activeCollection = computed(() =>
  collections.value.find((collection) => collection.key === activeCollectionKey.value),
)
const allChoices = computed(() => collections.value.flatMap((collection) => collection.choices))
const visibleChoices = computed(() => {
  const needle = search.value.trim().toLocaleLowerCase()
  if (!needle) return activeCollection.value?.choices ?? []
  return allChoices.value.filter((choice) =>
    [
      choice.title,
      ...choice.variants.flatMap((variant) => [variant.board.title, variant.board.description]),
    ]
      .join(' ')
      .toLocaleLowerCase()
      .includes(needle),
  )
})
const selectedChoice = computed(() => {
  const requestedChoice = findLeaderboardChoice(collections.value, requestedKey.value)
  return requestedChoice &&
    visibleChoices.value.some((choice) => choice.key === requestedChoice.key)
    ? requestedChoice
    : visibleChoices.value[0]
})
const selectedKey = computed(() => {
  const requestedVariant = selectedChoice.value?.variants.find(
    (variant) => variant.board.key === requestedKey.value,
  )
  return requestedVariant?.board.key ?? selectedChoice.value?.variants[0]?.board.key ?? ''
})
const metricOptions = computed(() =>
  (selectedChoice.value?.variants ?? []).map((variant) => ({
    value: variant.board.key,
    label: variant.label,
  })),
)

watch(selectedKey, (key) => {
  if (!key || key === requestedKey.value) return
  void router.replace({ query: { board: key } })
})

const page = computed(() => {
  const parsed = Number(route.query.page)
  return Number.isInteger(parsed) && parsed > 1 ? parsed : 1
})
const offset = computed(() => (page.value - 1) * PAGE_SIZE)

const boardQuery = useQuery({
  queryKey: computed(() => leaderboardKeys.board(selectedKey.value, offset.value)),
  queryFn: ({ signal }) => fetchLeaderboard(selectedKey.value, offset.value, signal),
  enabled: computed(() => catalogQuery.data.value?.isReady === true && !!selectedKey.value),
  staleTime: 60_000,
})

function selectCollection(key: string) {
  search.value = ''
  const firstVariant = collections.value.find((collection) => collection.key === key)?.choices[0]
    ?.variants[0]
  if (firstVariant) void router.replace({ query: { board: firstVariant.board.key } })
}

function selectChoice(key: string) {
  const choice = visibleChoices.value.find((item) => item.key === key)
  if (!choice) return
  const currentKind = selectedChoice.value?.variants.find(
    (variant) => variant.board.key === selectedKey.value,
  )?.kind
  const variant = choice.variants.find((item) => item.kind === currentKind) ?? choice.variants[0]
  if (variant) selectBoard(variant.board.key)
}

function selectBoard(key: string) {
  void router.replace({ query: { board: key } })
}

function goToPage(nextPage: number) {
  void router.replace({
    query: { board: selectedKey.value, ...(nextPage > 1 ? { page: String(nextPage) } : {}) },
  })
}

function formatScore(score: number, unit: string): string {
  if (unit === 'seconds') return formatHours(score)
  if (unit === 'days') return `${formatInteger(score)} ${score === 1 ? 'day' : 'days'}`
  return formatInteger(score)
}

function rowStyle(entry: LeaderboardEntry): Record<string, string> | undefined {
  const background = bungieUrl(entry.emblemBackgroundUrl)
  return background ? { '--emblem-background': `url("${background}")` } : undefined
}

const totalPages = computed(() =>
  Math.max(1, Math.ceil((boardQuery.data.value?.retainedEntryCount ?? 0) / PAGE_SIZE)),
)
</script>

<template>
  <div class="leaderboards container">
    <SkeletonBlock v-if="catalogQuery.isPending.value" height="18rem" radius="var(--radius-md)" />
    <ErrorState
      v-else-if="catalogQuery.isError.value"
      :error="catalogQuery.error.value"
      context="Leaderboards could not be loaded"
      @retry="catalogQuery.refetch()"
    />

    <LeaderboardWarmingState
      v-else-if="catalogQuery.data.value && !catalogQuery.data.value.isReady"
      :completed-player-count="catalogQuery.data.value.completedPlayerCount"
      :minimum-completed-players="catalogQuery.data.value.minimumCompletedPlayers"
    />

    <template v-else-if="catalogQuery.data.value">
      <section class="explorer" aria-labelledby="explore-title">
        <div class="explorer-heading">
          <h2 id="explore-title" class="visually-hidden">Choose a leaderboard</h2>
          <input
            v-model="search"
            class="catalog-search"
            type="search"
            placeholder="Find a leaderboard"
            aria-label="Search all leaderboards"
          />
        </div>

        <div class="collection-grid" role="list" aria-label="Leaderboard collections">
          <button
            v-for="collection in collections"
            :key="collection.key"
            type="button"
            class="collection-option"
            :class="{
              'collection-option--active': !search && activeCollectionKey === collection.key,
            }"
            @click="selectCollection(collection.key)"
          >
            <strong>{{ collection.title }}</strong>
          </button>
        </div>

        <div v-if="visibleChoices.length" class="record-controls">
          <label class="record-select-label">
            <span>{{ search ? 'Matching leaderboard' : 'Leaderboard' }}</span>
            <AppSelect
              class="record-select"
              :model-value="selectedChoice?.key ?? ''"
              :options="
                visibleChoices.map((choice) => ({ value: choice.key, label: choice.title }))
              "
              label="Leaderboard"
              @update:model-value="selectChoice"
            />
          </label>
          <div v-if="metricOptions.length > 1" class="metric-control">
            <span>Measure by</span>
            <SegmentedControl
              :model-value="selectedKey"
              :options="metricOptions"
              label="Leaderboard metric"
              @update:model-value="selectBoard"
            />
          </div>
        </div>
        <p v-else class="no-results">No leaderboards match “{{ search }}”.</p>
      </section>

      <main class="board-panel">
        <SkeletonBlock v-if="boardQuery.isPending.value" height="24rem" radius="var(--radius-md)" />
        <ErrorState
          v-else-if="boardQuery.isError.value"
          :error="boardQuery.error.value"
          context="This leaderboard could not be loaded"
          @retry="boardQuery.refetch()"
        />
        <template v-else-if="boardQuery.data.value">
          <header class="board-heading">
            <h2>{{ boardQuery.data.value.title }}</h2>
            <span v-if="boardQuery.data.value.isRepairing" class="refreshing"
              >Refreshing rankings</span
            >
          </header>

          <EmptyState
            v-if="boardQuery.data.value.entries.length === 0"
            title="No ranked Guardians yet"
            description="A positive score will appear here after a completed crawl."
          />
          <div v-else class="ranking-list" role="table" :aria-label="boardQuery.data.value.title">
            <div class="ranking-header" role="row">
              <span role="columnheader">Rank</span>
              <span role="columnheader">Guardian</span>
              <span role="columnheader">Score</span>
            </div>
            <RouterLink
              v-for="entry in boardQuery.data.value.entries"
              :key="`${entry.membershipTypeId}:${entry.membershipId}`"
              class="ranking-row"
              :class="{ 'ranking-row--podium': entry.rank <= 3 }"
              :style="rowStyle(entry)"
              role="row"
              :to="{
                name: 'report-overview',
                params: {
                  membershipTypeId: entry.membershipTypeId,
                  membershipId: entry.membershipId,
                },
              }"
            >
              <span class="rank tnum" role="cell">#{{ entry.rank }}</span>
              <span class="guardian" role="cell">{{ entry.fullDisplayName }}</span>
              <strong class="score tnum" role="cell">
                {{ formatScore(entry.score, boardQuery.data.value.unit) }}
              </strong>
            </RouterLink>
          </div>

          <nav v-if="totalPages > 1" class="pagination" aria-label="Leaderboard pages">
            <AppButton size="sm" :disabled="page <= 1" @click="goToPage(page - 1)"
              >Previous</AppButton
            >
            <span class="tnum">Page {{ page }} of {{ totalPages }}</span>
            <AppButton size="sm" :disabled="page >= totalPages" @click="goToPage(page + 1)"
              >Next</AppButton
            >
          </nav>
        </template>
      </main>
    </template>
  </div>
</template>

<style scoped>
.leaderboards {
  padding-top: var(--space-7);
}
.leaderboards-hero {
  margin-bottom: var(--space-5);
}
.leaderboards-hero h1 {
  font-size: var(--text-2xl);
}
.explorer {
  margin-bottom: 0;
  padding-block: var(--space-2) var(--space-5);
}
.explorer-heading {
  display: flex;
  justify-content: flex-end;
}
.catalog-search {
  width: min(100%, 18rem);
  height: 2.75rem;
  padding: 0 var(--space-3);
  background: var(--color-surface);
  border: 1px solid var(--color-border-strong);
  border-radius: var(--radius-md);
}
.catalog-search:focus-visible {
  outline-color: var(--color-border-strong);
}
.collection-grid {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: var(--space-2);
  margin-top: var(--space-4);
}
.collection-option {
  display: flex;
  min-height: 3.75rem;
  justify-content: center;
  flex-direction: column;
  padding: var(--space-3);
  color: var(--color-text);
  text-align: left;
  background: linear-gradient(145deg, rgb(33 24 25 / 0.92), rgb(23 17 18 / 0.72));
  border: 1px solid var(--color-border);
  border-radius: var(--radius-md);
  box-shadow: 0 1px 0 rgb(255 255 255 / 0.025);
  transition:
    transform var(--transition-fast),
    border-color var(--transition-fast),
    background-color var(--transition-fast),
    box-shadow var(--transition-fast);
}
.collection-option:hover {
  z-index: 1;
  color: var(--color-text);
  border-color: var(--color-border-strong);
  box-shadow: var(--shadow-raised);
  transform: translateY(-2px);
}
.collection-option--active {
  background: linear-gradient(145deg, rgb(215 172 75 / 0.18), rgb(33 24 25 / 0.92));
  border-color: var(--color-accent);
}
.collection-option strong {
  font-family: var(--font-display);
  font-size: var(--text-sm);
}
.record-controls {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  align-items: end;
  gap: var(--space-2);
  margin-top: var(--space-5);
  padding-top: var(--space-4);
  border-top: 1px solid var(--color-border);
}
.record-select-label,
.metric-control {
  display: flex;
  flex-direction: column;
  gap: var(--space-1);
}
.record-select-label {
  grid-column: 1 / 3;
}
.metric-control {
  grid-column: 3 / 5;
  align-items: flex-end;
}
.record-select-label > span:first-child,
.metric-control > span {
  color: var(--color-text-muted);
  font-size: var(--text-xs);
  font-weight: 600;
  letter-spacing: 0.06em;
  text-transform: uppercase;
}
.record-select {
  width: 100%;
}
.no-results {
  padding: var(--space-5);
  color: var(--color-text-secondary);
  text-align: center;
}
.board-panel {
  padding-top: var(--space-6);
  border-top: 1px solid var(--color-border-strong);
}
.board-heading {
  display: flex;
  justify-content: space-between;
  gap: var(--space-4);
  margin-bottom: var(--space-5);
}
.board-heading h2 {
  font-size: var(--text-xl);
}
.refreshing {
  align-self: start;
  padding: var(--space-1) var(--space-2);
  color: var(--color-warning);
  background: rgb(223 169 61 / 0.12);
  border-radius: var(--radius-full);
  font-size: var(--text-xs);
  white-space: nowrap;
}
.ranking-list {
  overflow: hidden;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-md);
}
.ranking-header,
.ranking-row {
  display: grid;
  grid-template-columns: 5rem minmax(0, 1fr) minmax(7rem, auto);
  align-items: center;
  gap: var(--space-3);
}
.ranking-header {
  padding: var(--space-2) var(--space-4);
  color: var(--color-text-muted);
  background: var(--color-surface-sunken);
  font-size: var(--text-xs);
}
.ranking-header span:last-child {
  text-align: right;
}
.ranking-row {
  position: relative;
  isolation: isolate;
  min-height: 4.25rem;
  padding: var(--space-3) var(--space-4);
  overflow: hidden;
  color: var(--color-text);
  border-top: 1px solid var(--color-border);
}
.ranking-row::before {
  position: absolute;
  z-index: -2;
  inset: 0;
  background-image: var(--emblem-background);
  background-position: center;
  background-size: cover;
  content: '';
  opacity: 0.72;
}
.ranking-row::after {
  position: absolute;
  z-index: -1;
  inset: 0;
  background: linear-gradient(
    90deg,
    rgb(13 10 11 / 0.76) 0%,
    rgb(13 10 11 / 0.4) 52%,
    rgb(13 10 11 / 0.7) 100%
  );
  content: '';
}
.ranking-row:hover {
  color: var(--color-text);
}
.ranking-row:hover::before {
  opacity: 0.88;
}
.ranking-row--podium {
  min-height: 4.75rem;
  background: linear-gradient(90deg, var(--color-accent-muted), transparent 45%);
}
.rank {
  color: var(--color-accent-strong);
  font-family: var(--font-display);
  font-size: var(--text-md);
  font-weight: 650;
}
.guardian {
  overflow: hidden;
  font-weight: 600;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.score {
  text-align: right;
}
.pagination {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-3);
  margin-top: var(--space-5);
  color: var(--color-text-secondary);
  font-size: var(--text-sm);
}
@media (max-width: 56rem) {
  .collection-grid {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
  .record-controls {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
  .record-select-label,
  .metric-control {
    grid-column: auto;
  }
}
@media (max-width: 40rem) {
  .leaderboards {
    padding-top: var(--space-5);
  }
  .explorer {
    padding-inline: var(--space-3);
  }
  .explorer-heading {
    align-items: stretch;
    flex-direction: column;
    gap: var(--space-3);
  }
  .catalog-search {
    width: 100%;
  }
  .record-controls {
    align-items: stretch;
    grid-template-columns: 1fr;
  }
  .record-select-label,
  .metric-control {
    grid-column: 1;
  }
  .collection-option {
    min-height: auto;
  }
  .board-heading {
    flex-direction: column;
  }
  .ranking-header {
    display: none;
  }
  .ranking-row {
    grid-template-columns: 3rem minmax(0, 1fr);
    gap: var(--space-2);
  }
  .ranking-row .score {
    grid-column: 2;
    text-align: left;
    color: var(--color-text-secondary);
    font-size: var(--text-sm);
  }
}
@media (max-width: 28rem) {
  .collection-grid {
    grid-template-columns: 1fr;
  }
}
</style>
