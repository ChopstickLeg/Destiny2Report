<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import AppButton from '@/components/base/AppButton.vue'
import ReportSection from '@/components/base/ReportSection.vue'
import BarList from '@/components/charts/BarList.vue'
import type { DestinyReport } from '@/lib/api/types'
import { formatHours, parseTimeSpan } from '@/lib/formatting/duration'
import { formatDate } from '@/lib/formatting/dates'
import { rankCharacterPlaytime, rankPatrolTime, summarizeStreak } from '../report-view'

const props = defineProps<{ report: DestinyReport }>()

const DELETED_CHARACTER_LIMIT = 20
const showAllDeletedCharacters = ref(false)

const CLASS_COLOR: Readonly<Record<string, string>> = {
  Titan: 'var(--color-class-titan)',
  Warlock: 'var(--color-class-warlock)',
  Hunter: 'var(--color-class-hunter)',
}

watch(
  () => props.report.playerMembershipId,
  () => {
    showAllDeletedCharacters.value = false
  },
)

const allCharacterBars = computed(() =>
  rankCharacterPlaytime(props.report.characterPlaytime).map((entry) => ({
    key: entry.key,
    label: entry.label,
    value: entry.seconds,
    display: formatHours(entry.seconds),
    tag: entry.tag,
    color: CLASS_COLOR[entry.className ?? ''] ?? 'var(--color-class-unknown)',
    muted: entry.tag === 'Deleted',
  })),
)

const deletedCharacterCount = computed(
  () => allCharacterBars.value.filter((entry) => entry.tag === 'Deleted').length,
)

const hiddenDeletedCharacterCount = computed(() =>
  showAllDeletedCharacters.value
    ? 0
    : Math.max(0, deletedCharacterCount.value - DELETED_CHARACTER_LIMIT),
)

const characterBars = computed(() => {
  if (showAllDeletedCharacters.value || hiddenDeletedCharacterCount.value === 0) {
    return allCharacterBars.value
  }

  let deletedShown = 0
  return allCharacterBars.value.filter((entry) => {
    if (entry.tag !== 'Deleted') return true
    deletedShown += 1
    return deletedShown <= DELETED_CHARACTER_LIMIT
  })
})

const patrolBars = computed(() =>
  rankPatrolTime(props.report.patrolTimeByPlanet).map((entry) => ({
    key: entry.key,
    label: entry.label,
    value: entry.seconds,
    display: formatHours(entry.seconds),
    color: 'var(--color-bar-emphasis)',
  })),
)

const activityTime = computed(() => parseTimeSpan(props.report.totalActivityTime))
const longestStreak = computed(() => summarizeStreak(props.report.longestPlaytimeStreak))
const currentStreak = computed(() => summarizeStreak(props.report.currentPlaytimeStreak))
</script>

<template>
  <ReportSection
    id="time"
    title="Time spent"
    subtitle="Character playtime across the account's history"
  >
    <div class="time-grid">
      <div v-if="characterBars.length > 0" class="time-block">
        <h3 class="block-title">By character</h3>
        <BarList :items="characterBars" unit="hours played" />
        <div v-if="hiddenDeletedCharacterCount > 0" class="character-more">
          <AppButton size="sm" variant="ghost" @click="showAllDeletedCharacters = true">
            Show {{ hiddenDeletedCharacterCount }} more deleted characters
          </AppButton>
        </div>
      </div>

      <div v-if="patrolBars.length > 0" class="time-block">
        <h3 class="block-title">Patrol time by destination</h3>
        <BarList :items="patrolBars" unit="hours on patrol" />
      </div>
    </div>

    <div v-if="activityTime || longestStreak || currentStreak" class="time-facts">
      <p v-if="activityTime" class="time-fact">
        <span class="fact-value tnum">{{ formatHours(activityTime) }}</span>
        <span class="fact-label">inside recorded activities</span>
      </p>
      <p v-if="longestStreak" class="time-fact">
        <span class="fact-value tnum">{{ longestStreak.days }} consecutive days</span>
        <span class="fact-label">
          longest play streak · {{ formatDate(longestStreak.start) }} –
          {{ formatDate(longestStreak.end) }}
        </span>
      </p>
      <p v-if="currentStreak && currentStreak.days > 1" class="time-fact">
        <span class="fact-value tnum">{{ currentStreak.days }} days</span>
        <span class="fact-label">current streak, still running</span>
      </p>
    </div>
  </ReportSection>
</template>

<style scoped>
.time-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(min(22rem, 100%), 1fr));
  gap: var(--space-6);
}

.block-title {
  font-size: var(--text-sm);
  color: var(--color-text-secondary);
  margin-bottom: var(--space-3);
  font-weight: 550;
}

.character-more {
  display: flex;
  justify-content: center;
  margin-top: var(--space-3);
}

.time-facts {
  display: flex;
  flex-wrap: wrap;
  gap: var(--space-5);
  margin-top: var(--space-5);
  padding-top: var(--space-4);
  border-top: 1px solid var(--color-border);
}

.time-fact {
  display: flex;
  flex-direction: column;
}

.fact-value {
  font-weight: 600;
}

.fact-label {
  font-size: var(--text-xs);
  color: var(--color-text-muted);
}
</style>
