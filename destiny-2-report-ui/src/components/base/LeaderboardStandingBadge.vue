<script setup lang="ts">
import { computed, inject } from 'vue'
import { RouterLink } from 'vue-router'
import type { PlayerLeaderboardStanding } from '@/lib/api/types'
import { playerStandingsKey } from '@/features/report-overview/useReport'

const props = defineProps<{ standing?: PlayerLeaderboardStanding; metricKey?: string }>()
const reportStandings = inject(
  playerStandingsKey,
  computed(() => []),
)
const resolved = computed(
  () =>
    props.standing ??
    reportStandings.value.find((item) => item.metricKey === props.metricKey) ??
    null,
)
const label = computed(() => {
  const value = resolved.value
  if (!value) return ''
  if (value.rank != null) return `#${value.rank.toLocaleString()}`
  return `Top ${value.tier.slice(4)}%`
})
const title = computed(() =>
  resolved.value
    ? `View the ${resolved.value.title} leaderboard — ranked ${label.value} among tracked players`
    : '',
)
</script>

<template>
  <RouterLink
    v-if="resolved"
    class="standing"
    :class="`standing--${resolved.tier.replace('.', '-')}`"
    :to="{ name: 'leaderboards', query: { board: resolved.metricKey } }"
    :title="title"
    :aria-label="title"
  >
    <span class="standing-mark" aria-hidden="true">◆</span>
    <span>{{ label }}</span>
  </RouterLink>
</template>

<style scoped>
.standing {
  --standing-rgb: 126 186 255;
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
  width: max-content;
  padding: 0.2rem 0.48rem 0.22rem;
  color: rgb(var(--standing-rgb));
  background: linear-gradient(
    105deg,
    rgb(var(--standing-rgb) / 0.16),
    rgb(var(--standing-rgb) / 0.05)
  );
  border: 1px solid rgb(var(--standing-rgb) / 0.42);
  border-radius: var(--radius-full);
  box-shadow:
    inset 0 1px rgb(255 255 255 / 0.06),
    0 0 14px rgb(var(--standing-rgb) / 0.08);
  font-family: var(--font-display);
  font-size: 0.6875rem;
  font-weight: 650;
  letter-spacing: 0.025em;
  line-height: 1;
  white-space: nowrap;
  text-decoration: none;
  transition:
    border-color var(--transition-fast),
    background var(--transition-fast),
    box-shadow var(--transition-fast),
    transform var(--transition-fast);
}

.standing:hover {
  border-color: rgb(var(--standing-rgb) / 0.78);
  background: linear-gradient(
    105deg,
    rgb(var(--standing-rgb) / 0.24),
    rgb(var(--standing-rgb) / 0.09)
  );
  box-shadow:
    inset 0 1px rgb(255 255 255 / 0.08),
    0 0 18px rgb(var(--standing-rgb) / 0.16);
  transform: translateY(-1px);
}

.standing:focus-visible {
  outline: 2px solid rgb(var(--standing-rgb) / 0.9);
  outline-offset: 2px;
}

.standing--top-1000 {
  --standing-rgb: 255 193 92;
}
.standing--top-0-1 {
  --standing-rgb: 255 119 148;
}
.standing--top-1 {
  --standing-rgb: 194 143 255;
}
.standing--top-5 {
  --standing-rgb: 93 211 206;
}

.standing-mark {
  font-size: 0.52rem;
  filter: drop-shadow(0 0 4px rgb(var(--standing-rgb) / 0.75));
}
</style>
