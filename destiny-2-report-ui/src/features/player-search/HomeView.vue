<script setup lang="ts">
import { onMounted, ref } from 'vue'
import GlobalSearch from '@/components/shell/GlobalSearch.vue'
import PlayerIdentityRow from '@/components/base/PlayerIdentityRow.vue'
import { loadRecentPlayers, type RecentPlayer } from '@/lib/recent-players'
import { platformLabel } from '@/lib/platform'
import LeaderboardShowcase from '@/features/leaderboards/LeaderboardShowcase.vue'

const recentPlayers = ref<RecentPlayer[]>([])

onMounted(() => {
  recentPlayers.value = loadRecentPlayers()
})
</script>

<template>
  <div class="home container">
    <div class="hero">
      <h1 class="hero-title display">Explore a Guardian's Destiny 2 history.</h1>
      <p class="hero-copy">
        Destiny 2 Report crawls every raid, Crucible match, and patrol hour in a player's full
        activity history and organizes the results into one shareable report.
      </p>
      <GlobalSearch size="large" class="hero-search" />
    </div>

    <section v-if="recentPlayers.length > 0" class="recent" aria-labelledby="recent-heading">
      <h2 id="recent-heading" class="recent-title">Recently viewed</h2>
      <ul class="recent-list">
        <li
          v-for="player in recentPlayers"
          :key="`${player.membershipTypeId}-${player.membershipId}`"
        >
          <PlayerIdentityRow
            :name="player.displayName"
            :code="player.displayCode"
            :detail="platformLabel(player.membershipTypeId)"
            :emblem-url="player.emblemIconUrl || null"
            :to="{
              name: 'report-overview',
              params: {
                membershipTypeId: player.membershipTypeId,
                membershipId: player.membershipId,
              },
            }"
          />
        </li>
      </ul>
    </section>

    <LeaderboardShowcase />
  </div>
</template>

<style scoped>
.home {
  padding-top: clamp(var(--space-6), 10vh, var(--space-8));
}

.hero {
  display: flex;
  flex-direction: column;
  align-items: center;
  text-align: center;
}

.hero-title {
  font-size: clamp(var(--text-xl), 4.5vw, var(--text-2xl));
  max-width: 26ch;
}

.hero-copy {
  margin-top: var(--space-3);
  max-width: 44rem;
  color: var(--color-text-secondary);
}

.hero-search {
  margin-top: var(--space-5);
}

.recent {
  margin-top: var(--space-7);
}

.recent-title {
  font-size: var(--text-sm);
  text-transform: uppercase;
  letter-spacing: 0.06em;
  color: var(--color-text-muted);
  margin-bottom: var(--space-2);
}

.recent-list {
  list-style: none;
  padding: 0;
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(16rem, 1fr));
  gap: var(--space-1);
}
</style>
