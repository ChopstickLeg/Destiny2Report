<script setup lang="ts">
import LeaderboardStandingBadge from '@/components/base/LeaderboardStandingBadge.vue'
import type { PlayerLeaderboardStanding } from '@/lib/api/types'
import { formatDurationCompact } from '@/lib/formatting/duration'
import { formatInteger } from '@/lib/formatting/numbers'

defineProps<{ standings: PlayerLeaderboardStanding[] }>()

function formatScore(standing: PlayerLeaderboardStanding): string {
  if (standing.unit === 'seconds') return formatDurationCompact(standing.score)
  if (standing.unit === 'days') return `${formatInteger(standing.score)} days`
  return formatInteger(standing.score)
}
</script>

<template>
  <aside v-if="standings.length > 0" class="distinctions" aria-labelledby="standout-ranks-title">
    <div class="container distinctions-inner">
      <header class="distinctions-heading">
        <span class="distinctions-kicker">Standout ranks</span>
        <h2 id="standout-ranks-title">Best {{ standings.length }}</h2>
      </header>

      <ol class="distinctions-list">
        <li
          v-for="(standing, index) in standings"
          :key="standing.metricKey"
          class="distinction"
          :style="{ '--reveal-order': index }"
        >
          <span class="distinction-number tnum" aria-hidden="true">0{{ index + 1 }}</span>
          <div class="distinction-copy">
            <span class="distinction-category">{{ standing.category }}</span>
            <strong class="distinction-title">{{ standing.title }}</strong>
            <span class="distinction-score tnum">{{ formatScore(standing) }}</span>
          </div>
          <LeaderboardStandingBadge :standing="standing" />
        </li>
      </ol>
    </div>
  </aside>
</template>

<style scoped>
.distinctions {
  position: relative;
  overflow: hidden;
  border-bottom: 1px solid var(--color-border);
  background:
    linear-gradient(90deg, rgb(255 193 92 / 0.055), transparent 35%), var(--color-surface);
}

.distinctions::after {
  position: absolute;
  inset: 0;
  pointer-events: none;
  content: '';
  opacity: 0.35;
  background-image: repeating-linear-gradient(
    115deg,
    transparent 0,
    transparent 28px,
    rgb(255 255 255 / 0.018) 29px,
    transparent 30px
  );
}

.distinctions-inner {
  position: relative;
  z-index: 1;
  display: grid;
  grid-template-columns: auto minmax(0, 1fr);
  align-items: stretch;
  gap: var(--space-5);
  padding-block: var(--space-3);
}

.distinctions-heading {
  display: flex;
  min-width: 8.5rem;
  flex-direction: column;
  justify-content: center;
  padding-right: var(--space-5);
  border-right: 1px solid var(--color-border);
}

.distinctions-kicker {
  color: var(--color-text-muted);
  font-size: 0.625rem;
  font-weight: 650;
  letter-spacing: 0.11em;
  text-transform: uppercase;
}

.distinctions-heading h2 {
  margin-top: 0.15rem;
  font-family: var(--font-display);
  font-size: var(--text-lg);
  line-height: 1;
}

.distinctions-list {
  display: grid;
  grid-template-columns: repeat(5, minmax(8rem, 1fr));
  min-width: 0;
  padding: 0;
  list-style: none;
}

.distinction {
  display: grid;
  min-width: 0;
  grid-template-columns: auto minmax(0, 1fr);
  align-content: center;
  column-gap: var(--space-2);
  padding-inline: var(--space-3);
  animation: distinction-in 360ms both;
  animation-delay: calc(var(--reveal-order) * 55ms);
}

.distinction + .distinction {
  border-left: 1px solid var(--color-border);
}

.distinction-number {
  grid-row: 1 / span 2;
  align-self: start;
  color: var(--color-text-muted);
  font-family: var(--font-display);
  font-size: 0.625rem;
}

.distinction-copy {
  min-width: 0;
}

.distinction-category {
  display: block;
  overflow: hidden;
  color: var(--color-text-muted);
  font-size: 0.625rem;
  letter-spacing: 0.07em;
  text-overflow: ellipsis;
  text-transform: uppercase;
  white-space: nowrap;
}

.distinction-title {
  display: block;
  overflow: hidden;
  margin-top: 0.1rem;
  font-size: var(--text-sm);
  font-weight: 600;
  line-height: 1.2;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.distinction-score {
  display: block;
  margin-top: 0.15rem;
  color: var(--color-text-secondary);
  font-size: var(--text-xs);
}

.distinction :deep(.standing) {
  grid-column: 2;
  margin-top: var(--space-2);
}

@keyframes distinction-in {
  from {
    opacity: 0;
    transform: translateY(4px);
  }
}

@media (max-width: 64rem) {
  .distinctions-inner {
    grid-template-columns: 1fr;
    gap: var(--space-3);
  }
  .distinctions-heading {
    flex-direction: row;
    align-items: baseline;
    justify-content: flex-start;
    gap: var(--space-2);
    padding-right: 0;
    border-right: 0;
  }
  .distinctions-list {
    grid-template-columns: repeat(5, minmax(10rem, 1fr));
    overflow-x: auto;
    padding-bottom: var(--space-1);
  }
}

@media (prefers-reduced-motion: reduce) {
  .distinction {
    animation: none;
  }
}
</style>
