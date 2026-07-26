<script setup lang="ts">
import { computed } from 'vue'
import { useQuery } from '@tanstack/vue-query'
import { fetchLeaderboardCatalog, leaderboardKeys } from '@/lib/api/leaderboards'
import { organizeLeaderboards } from './leaderboard-collections'
import LeaderboardWarmingState from './LeaderboardWarmingState.vue'

const catalogQuery = useQuery({
  queryKey: leaderboardKeys.catalog,
  queryFn: ({ signal }) => fetchLeaderboardCatalog(signal),
  staleTime: 60_000,
})

const featuredCollections = computed(() => {
  const collections = organizeLeaderboards(catalogQuery.data.value?.leaderboards ?? [])
  const preferred = ['time-invested', 'combat', 'competitive', 'curiosities']
  return preferred
    .map((key) => collections.find((collection) => collection.key === key))
    .filter((collection) => collection !== undefined)
    .map((collection) => ({ ...collection, boards: collection.boards.slice(0, 4) }))
})
</script>

<template>
  <LeaderboardWarmingState
    v-if="catalogQuery.data.value && !catalogQuery.data.value.isReady"
    class="showcase-warming"
    :completed-player-count="catalogQuery.data.value.completedPlayerCount"
    :minimum-completed-players="catalogQuery.data.value.minimumCompletedPlayers"
  />
  <section
    v-else-if="catalogQuery.data.value?.isReady"
    class="showcase"
    aria-labelledby="leaderboard-heading"
  >
    <div class="showcase-heading">
      <div>
        <p class="eyebrow">Community leaderboards</p>
        <h2 id="leaderboard-heading" class="display">See who leads the pack</h2>
      </div>
      <RouterLink class="all-link" :to="{ name: 'leaderboards' }">
        Explore every record <span aria-hidden="true">→</span>
      </RouterLink>
    </div>

    <div v-if="featuredCollections.length" class="showcase-grid">
      <article v-for="collection in featuredCollections" :key="collection.key" class="collection">
        <header>
          <h3>{{ collection.title }}</h3>
          <p>{{ collection.description }}</p>
        </header>
        <nav :aria-label="collection.title">
          <RouterLink
            v-for="board in collection.boards"
            :key="board.key"
            class="board-link"
            :to="{ name: 'leaderboards', query: { board: board.key } }"
          >
            <span>{{ board.title }}</span
            ><span aria-hidden="true">›</span>
          </RouterLink>
        </nav>
      </article>
    </div>
  </section>
</template>

<style scoped>
.showcase-warming {
  margin-top: var(--space-8);
}
.showcase {
  margin-top: var(--space-8);
  padding-top: var(--space-5);
  border-top: 1px solid var(--color-border);
}
.showcase-heading {
  display: flex;
  align-items: end;
  justify-content: space-between;
  gap: var(--space-4);
  margin-bottom: var(--space-4);
}
.eyebrow {
  color: var(--color-accent-strong);
  font-size: var(--text-xs);
  font-weight: 650;
  letter-spacing: 0.1em;
  text-transform: uppercase;
}
.showcase h2 {
  margin-top: var(--space-1);
  font-size: var(--text-xl);
}
.all-link {
  color: var(--color-text-secondary);
  font-size: var(--text-sm);
  white-space: nowrap;
}
.showcase-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  column-gap: var(--space-7);
}
.collection {
  padding-block: var(--space-4);
  border-top: 1px solid var(--color-border);
}
.collection h3 {
  font-size: var(--text-md);
}
.collection header p {
  margin-top: var(--space-1);
  color: var(--color-text-secondary);
  font-size: var(--text-sm);
}
.collection nav {
  margin-top: var(--space-3);
}
.board-link {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-3);
  padding-block: var(--space-2);
  color: var(--color-text);
  font-size: var(--text-sm);
  transition:
    color var(--transition-fast),
    transform var(--transition-fast);
}
.board-link:hover {
  transform: translateX(var(--space-1));
}
.board-link span:last-child {
  color: var(--color-accent-strong);
  font-size: var(--text-lg);
}
@media (max-width: 40rem) {
  .showcase-heading {
    align-items: start;
    flex-direction: column;
  }
  .showcase-grid {
    grid-template-columns: 1fr;
  }
}
</style>
