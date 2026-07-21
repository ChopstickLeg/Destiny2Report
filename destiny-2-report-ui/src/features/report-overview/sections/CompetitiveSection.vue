<script setup lang="ts">
import { computed } from 'vue'
import ReportSection from '@/components/base/ReportSection.vue'
import BarList from '@/components/charts/BarList.vue'
import SplitBar from '@/components/charts/SplitBar.vue'
import ExplainedLabel from '@/components/base/ExplainedLabel.vue'
import type { DestinyReport } from '@/lib/api/types'
import { formatInteger, formatPercent, formatRatio } from '@/lib/formatting/numbers'
import { humanizeModeName, rankByMode } from '../report-view'
import { MISSING_ACTIVITY_MODE_EXPLANATION, isMissingActivityMode } from '@/lib/stat-explanations'

const props = defineProps<{ report: DestinyReport }>()

const crucible = computed(() => {
  const r = props.report
  if (r.crucibleMatchesPlayed <= 0) return null
  return {
    kd: r.crucibleKd,
    kda: r.crucibleKda,
    matches: r.crucibleMatchesPlayed,
    wins: r.crucibleWins,
    losses: Math.max(0, r.crucibleMatchesPlayed - r.crucibleWins),
  }
})

const gambit = computed(() => {
  const r = props.report
  if (r.gambitMatchesPlayed <= 0) return null
  return {
    kd: r.gambitKd,
    kda: r.gambitKda,
    matches: r.gambitMatchesPlayed,
    wins: r.gambitWins,
    losses: Math.max(0, r.gambitMatchesPlayed - r.gambitWins),
  }
})

const playlists = computed(() =>
  [...props.report.pvpPlaylists]
    .filter((playlist) => playlist.matches > 0)
    .sort((a, b) => b.matches - a.matches),
)

const crucibleKillBars = computed(() =>
  rankByMode(props.report.crucibleKills.byMode).map((entry) => {
    const label = entry.label
    return {
      key: entry.key,
      label,
      value: entry.value,
      display: formatInteger(entry.value),
      color: 'var(--color-mode-pvp)',
      tooltip: isMissingActivityMode(label) ? MISSING_ACTIVITY_MODE_EXPLANATION : undefined,
    }
  }),
)

interface MoteRow {
  mode: string
  banked: number
  lost: number
  denied: number
}

const moteRows = computed<MoteRow[]>(() => {
  const motes = props.report.gambitMotes
  const modes = new Set([
    ...Object.keys(motes.motesBanked.byMode),
    ...Object.keys(motes.motesLost.byMode),
    ...Object.keys(motes.motesDenied.byMode),
  ])
  return [...modes]
    .map((mode) => ({
      mode,
      banked: motes.motesBanked.byMode[mode] ?? 0,
      lost: motes.motesLost.byMode[mode] ?? 0,
      denied: motes.motesDenied.byMode[mode] ?? 0,
    }))
    .filter((row) => row.banked > 0 || row.lost > 0 || row.denied > 0)
    .sort((a, b) => b.banked - a.banked)
})

const showMotes = computed(() => props.report.gambitMotes.matches > 0 && moteRows.value.length > 0)
</script>

<template>
  <ReportSection
    id="competitive"
    title="Competitive record"
    subtitle="Ratios come straight from match history. KD is kills per death; KDA credits assists"
  >
    <div class="arena-grid">
      <article v-if="crucible" class="arena">
        <h3 class="arena-title">
          <span class="arena-dot" style="background: var(--color-mode-pvp)" aria-hidden="true" />
          Crucible
        </h3>
        <dl class="arena-stats">
          <div class="arena-stat">
            <dt>KD</dt>
            <dd class="tnum">{{ formatRatio(crucible.kd) }}</dd>
          </div>
          <div class="arena-stat">
            <dt>KDA</dt>
            <dd class="tnum">{{ formatRatio(crucible.kda) }}</dd>
          </div>
          <div class="arena-stat">
            <dt>Matches</dt>
            <dd class="tnum">{{ formatInteger(crucible.matches) }}</dd>
          </div>
        </dl>
        <SplitBar
          :segments="[
            { label: 'Wins', value: crucible.wins, color: 'var(--color-mode-pvp)' },
            { label: 'Losses', value: crucible.losses, color: 'var(--color-bar)' },
          ]"
          unit="matches"
        />
      </article>

      <article v-if="gambit" class="arena">
        <h3 class="arena-title">
          <span class="arena-dot" style="background: var(--color-mode-gambit)" aria-hidden="true" />
          Gambit
        </h3>
        <dl class="arena-stats">
          <div class="arena-stat">
            <dt>KD</dt>
            <dd class="tnum">{{ formatRatio(gambit.kd) }}</dd>
          </div>
          <div class="arena-stat">
            <dt>KDA</dt>
            <dd class="tnum">{{ formatRatio(gambit.kda) }}</dd>
          </div>
          <div class="arena-stat">
            <dt>Matches</dt>
            <dd class="tnum">{{ formatInteger(gambit.matches) }}</dd>
          </div>
        </dl>
        <SplitBar
          :segments="[
            { label: 'Wins', value: gambit.wins, color: 'var(--color-mode-gambit)' },
            { label: 'Losses', value: gambit.losses, color: 'var(--color-bar)' },
          ]"
          unit="matches"
        />
      </article>
    </div>

    <div v-if="playlists.length > 0" class="playlists">
      <h3 class="block-title">Crucible playlists</h3>
      <table class="playlist-table">
        <thead>
          <tr>
            <th scope="col">Playlist</th>
            <th scope="col" class="num">Matches</th>
            <th scope="col" class="num">W – L</th>
            <th scope="col" class="num">Win rate</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="playlist in playlists" :key="playlist.mode">
            <td>
              <ExplainedLabel
                v-if="isMissingActivityMode(playlist.modeName)"
                :text="humanizeModeName(playlist.modeName)"
                :explanation="MISSING_ACTIVITY_MODE_EXPLANATION"
              />
              <template v-else>{{ humanizeModeName(playlist.modeName) }}</template>
            </td>
            <td class="num tnum">{{ formatInteger(playlist.matches) }}</td>
            <td class="num tnum">
              {{ formatInteger(playlist.wins) }} – {{ formatInteger(playlist.losses) }}
            </td>
            <td class="num tnum">
              {{ formatPercent(playlist.winRate, 1) }}
              <span
                v-if="playlist.matches < 10"
                class="small-sample"
                title="Fewer than 10 matches. Treat this rate with caution"
              >
                low sample
              </span>
            </td>
          </tr>
        </tbody>
      </table>
    </div>

    <div class="detail-grid">
      <div v-if="crucibleKillBars.length > 0" class="detail-block">
        <h3 class="block-title">
          Crucible kills by mode
          <span class="block-total tnum"
            >{{ formatInteger(report.crucibleKills.total) }} total</span
          >
        </h3>
        <BarList :items="crucibleKillBars" unit="kills" />
      </div>

      <div v-if="showMotes" class="detail-block">
        <h3 class="block-title">Gambit motes</h3>
        <p class="mote-averages">
          Averages per match:
          <strong class="tnum">{{ report.gambitMotes.averageMotesBanked }}</strong> banked,
          <strong class="tnum">{{ report.gambitMotes.averageMotesLost }}</strong> lost.
        </p>
        <table class="playlist-table">
          <thead>
            <tr>
              <th scope="col">Mode</th>
              <th scope="col" class="num">Banked</th>
              <th scope="col" class="num">Lost</th>
              <th scope="col" class="num">Denied</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="row in moteRows" :key="row.mode">
              <td>{{ humanizeModeName(row.mode) }}</td>
              <td class="num tnum">{{ formatInteger(row.banked) }}</td>
              <td class="num tnum">{{ formatInteger(row.lost) }}</td>
              <td class="num tnum">{{ formatInteger(row.denied) }}</td>
            </tr>
          </tbody>
        </table>
      </div>
    </div>
  </ReportSection>
</template>

<style scoped>
.arena-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(min(20rem, 100%), 1fr));
  gap: var(--space-4);
}

.arena {
  padding: var(--space-4);
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-md);
}

.arena-title {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  font-size: var(--text-base);
  margin-bottom: var(--space-3);
}

.arena-dot {
  width: 0.625rem;
  height: 0.625rem;
  border-radius: var(--radius-full);
}

.arena-stats {
  display: flex;
  gap: var(--space-5);
  margin-bottom: var(--space-4);
}

.arena-stat dt {
  font-size: var(--text-xs);
  text-transform: uppercase;
  letter-spacing: 0.06em;
  color: var(--color-text-muted);
}

.arena-stat dd {
  font-size: var(--text-lg);
  font-weight: 600;
  font-family: var(--font-display);
}

.playlists,
.detail-block {
  margin-top: var(--space-6);
}

.block-title {
  font-size: var(--text-sm);
  font-weight: 550;
  color: var(--color-text-secondary);
  margin-bottom: var(--space-3);
  display: flex;
  align-items: baseline;
  gap: var(--space-2);
}

.block-total {
  font-weight: 400;
  color: var(--color-text-muted);
  font-size: var(--text-xs);
}

.playlist-table {
  font-size: var(--text-sm);
}

.playlist-table th {
  font-size: var(--text-xs);
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: var(--color-text-muted);
  padding: var(--space-2) var(--space-2) var(--space-2) 0;
  border-bottom: 1px solid var(--color-border);
}

.playlist-table td {
  padding: var(--space-2) var(--space-2) var(--space-2) 0;
  border-bottom: 1px solid var(--color-border);
}

.playlist-table .num {
  text-align: right;
}

.small-sample {
  display: inline-block;
  margin-left: var(--space-1);
  font-size: var(--text-xs);
  color: var(--color-text-muted);
  border: 1px solid var(--color-border-strong);
  border-radius: var(--radius-sm);
  padding: 0 var(--space-1);
}

.detail-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(min(22rem, 100%), 1fr));
  gap: 0 var(--space-6);
}

.mote-averages {
  font-size: var(--text-xs);
  color: var(--color-text-muted);
  margin-bottom: var(--space-3);
  max-width: 36rem;
}
</style>
