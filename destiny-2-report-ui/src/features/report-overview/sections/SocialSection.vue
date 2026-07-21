<script setup lang="ts">
import { computed } from 'vue'
import ReportSection from '@/components/base/ReportSection.vue'
import BarList from '@/components/charts/BarList.vue'
import PlayerIdentityRow from '@/components/base/PlayerIdentityRow.vue'
import type { DestinyReport } from '@/lib/api/types'
import { formatInteger } from '@/lib/formatting/numbers'
import { platformLabel } from '@/lib/platform'

const props = defineProps<{ report: DestinyReport }>()

const teammates = computed(() =>
  [...props.report.mostPlayedWith].sort((a, b) => b.encounterCount - a.encounterCount),
)

const sherpaBars = computed(() =>
  [...props.report.playersSherpaed]
    .filter((sherpa) => sherpa.playerCount > 0)
    .sort((a, b) => b.playerCount - a.playerCount)
    .map((sherpa) => ({
      key: sherpa.raidName,
      label: sherpa.raidName,
      value: sherpa.playerCount,
      display: formatInteger(sherpa.playerCount),
      color: 'var(--color-bar-emphasis)',
    })),
)

const sherpaTotal = computed(() =>
  props.report.playersSherpaed.reduce((sum, sherpa) => sum + sherpa.playerCount, 0),
)
</script>

<template>
  <ReportSection
    id="fireteam"
    title="Fireteam history"
    :subtitle="
      report.uniquePlayersPlayedWith > 0
        ? `${formatInteger(report.uniquePlayersPlayedWith)} unique Guardians have shared an activity with this player`
        : undefined
    "
  >
    <div class="social-grid">
      <div v-if="teammates.length > 0" class="social-block">
        <h3 class="block-title">Most played with</h3>
        <ol class="teammate-list">
          <li v-for="entry in teammates" :key="entry.player.membershipId">
            <PlayerIdentityRow
              :name="entry.player.displayName || 'Unknown Guardian'"
              :detail="platformLabel(entry.player.membershipType)"
              :emblem-url="entry.player.emblemUrl || null"
              :to="
                entry.player.membershipType > 0
                  ? {
                      name: 'report-overview',
                      params: {
                        membershipTypeId: entry.player.membershipType,
                        membershipId: entry.player.membershipId,
                      },
                    }
                  : undefined
              "
            >
              <span class="encounter-count tnum">
                {{ formatInteger(entry.encounterCount) }}
                <span class="encounter-label">activities</span>
              </span>
            </PlayerIdentityRow>
          </li>
        </ol>
      </div>

      <div v-if="sherpaBars.length > 0" class="social-block">
        <h3 class="block-title">
          Sherpa record
          <span class="block-note">
            {{ formatInteger(sherpaTotal) }} first-time raiders guided through a clear
          </span>
        </h3>
        <BarList :items="sherpaBars" unit="players sherpaed" />
      </div>
    </div>
  </ReportSection>
</template>

<style scoped>
.social-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(min(22rem, 100%), 1fr));
  gap: var(--space-6);
}

.block-title {
  font-size: var(--text-sm);
  font-weight: 550;
  color: var(--color-text-secondary);
  margin-bottom: var(--space-3);
}

.block-note {
  display: block;
  font-weight: 400;
  font-size: var(--text-xs);
  color: var(--color-text-muted);
  margin-top: 2px;
}

.teammate-list {
  list-style: none;
  padding: 0;
  display: flex;
  flex-direction: column;
}

.encounter-count {
  margin-left: auto;
  font-size: var(--text-sm);
  text-align: right;
  flex: none;
}

.encounter-label {
  display: block;
  font-size: var(--text-xs);
  color: var(--color-text-muted);
}
</style>
