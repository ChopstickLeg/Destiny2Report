<script setup lang="ts">
import { onMounted, ref } from 'vue'
import GlobalSearch from '@/components/shell/GlobalSearch.vue'
import PlayerIdentityRow from '@/components/base/PlayerIdentityRow.vue'
import { loadRecentPlayers, type RecentPlayer } from '@/lib/recent-players'
import { platformLabel } from '@/lib/platform'

const recentPlayers = ref<RecentPlayer[]>([])

onMounted(() => {
  recentPlayers.value = loadRecentPlayers()
})
</script>

<template>
  <div class="home container">
    <div class="hero">
      <h1 class="hero-title display">See the story behind a Guardian's history.</h1>
      <p class="hero-copy">
        Destiny 2 Report crawls every raid, Crucible match, and patrol hour in a player's full
        activity history, then turns it into one honest, shareable report.
      </p>
      <GlobalSearch size="large" class="hero-search" />
      <p class="hero-hint">Search any public Bungie name. Partial names work too.</p>
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

    <section class="explain" aria-label="What the report covers">
      <div class="explain-item">
        <h2 class="explain-title">Complete history</h2>
        <p class="explain-copy">
          Playtime by character and destination, day streaks, and every activity since your Guardian
          first set foot in the Cosmodrome.
        </p>
      </div>
      <div class="explain-item">
        <h2 class="explain-title">Combat, itemized</h2>
        <p class="explain-copy">
          Weapon and ability kills layered by class, mode, and category. Deaths and competitive
          records stay separate and clearly labeled.
        </p>
      </div>
      <div class="explain-item">
        <h2 class="explain-title">The people beside you</h2>
        <p class="explain-copy">
          Recurring fireteam members, unique players encountered, and the raiders you sherpaed
          through their first clear.
        </p>
      </div>
    </section>
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

.hero-hint {
  margin-top: var(--space-2);
  font-size: var(--text-xs);
  color: var(--color-text-muted);
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

.explain {
  margin-top: var(--space-8);
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(15rem, 1fr));
  gap: var(--space-5);
  padding-top: var(--space-5);
  border-top: 1px solid var(--color-border);
}

.explain-title {
  font-size: var(--text-base);
  font-weight: 600;
}

.explain-copy {
  margin-top: var(--space-2);
  font-size: var(--text-sm);
  color: var(--color-text-secondary);
}
</style>
