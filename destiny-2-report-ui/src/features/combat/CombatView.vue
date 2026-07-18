<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useQuery } from '@tanstack/vue-query'
import SegmentedControl from '@/components/base/SegmentedControl.vue'
import ReportSection from '@/components/base/ReportSection.vue'
import ErrorState from '@/components/base/ErrorState.vue'
import EmptyState from '@/components/base/EmptyState.vue'
import SkeletonBlock from '@/components/base/SkeletonBlock.vue'
import BarList from '@/components/charts/BarList.vue'
import DonutChart from '@/components/charts/DonutChart.vue'
import AppButton from '@/components/base/AppButton.vue'
import ExplainedLabel from '@/components/base/ExplainedLabel.vue'
import AbilityKillIcon from './AbilityKillIcon.vue'
import { fetchDeaths, fetchWeapons, reportKeys } from '@/lib/api/reports'
import type { ActivityModeParam } from '@/lib/api/types'
import { formatInteger } from '@/lib/formatting/numbers'
import { useReportIdentity } from '@/features/report-overview/useReport'
import { humanizeModeName } from '@/features/report-overview/report-view'
import {
  MISSING_ACTIVITY_MODE_EXPLANATION,
  UNKNOWN_KILLS_EXPLANATION,
  isMissingActivityMode,
  isUnknownKillCategory,
} from '@/lib/stat-explanations'
import {
  ALL,
  MODE_COLOR,
  DONUT_COLORS,
  availableClasses,
  availableModes,
  categoryShares,
  flattenWeapons,
} from './combat-view'
import { abilityKillIconKind } from './ability-kills'

const identity = useReportIdentity()
const route = useRoute()
const router = useRouter()

// -- Deep-linkable filter state (?mode=&class=&list=) -----------------------

const MODES: Array<{ value: ActivityModeParam; label: string }> = [
  { value: 'PvE', label: 'PvE' },
  { value: 'PvP', label: 'PvP' },
  { value: 'Gambit', label: 'Gambit' },
]

const bucket = computed<ActivityModeParam>(() => {
  const q = route.query.mode
  return q === 'PvP' || q === 'Gambit' ? q : 'PvE'
})

const classFilter = computed(() =>
  typeof route.query.class === 'string' && route.query.class ? route.query.class : ALL,
)
const modeFilter = computed(() =>
  typeof route.query.list === 'string' && route.query.list ? route.query.list : ALL,
)

function setFilters(next: { mode?: ActivityModeParam; class?: string; list?: string }) {
  const mode = next.mode ?? bucket.value
  // Changing the bucket resets narrower filters; changing class resets list.
  const cls = next.mode ? ALL : (next.class ?? classFilter.value)
  const list = next.mode || next.class ? ALL : (next.list ?? modeFilter.value)
  void router.replace({
    query: {
      ...(mode !== 'PvE' ? { mode } : {}),
      ...(cls !== ALL ? { class: cls } : {}),
      ...(list !== ALL ? { list } : {}),
    },
  })
}

// -- Queries (weapons and deaths fail/retry independently) ------------------

const weaponsQuery = useQuery({
  queryKey: computed(() => reportKeys.weapons(identity.value, bucket.value)),
  queryFn: ({ signal }) => fetchWeapons(identity.value, bucket.value, signal),
  staleTime: 5 * 60_000,
})

const deathsQuery = useQuery({
  queryKey: computed(() => reportKeys.deaths(identity.value, bucket.value)),
  queryFn: ({ signal }) => fetchDeaths(identity.value, bucket.value, signal),
  staleTime: 5 * 60_000,
})

// -- Derived weapon views ----------------------------------------------------

const classOptions = computed(() => {
  const report = weaponsQuery.data.value
  if (!report) return []
  const classes = availableClasses(report)
  if (classes.length <= 1) return []
  return [ALL, ...classes].map((value) => ({ value, label: value }))
})

const modeOptions = computed(() => {
  const report = weaponsQuery.data.value
  if (!report) return []
  const modes = availableModes(report, classFilter.value)
  if (modes.length <= 1) return []
  return [ALL, ...modes]
})

const flattened = computed(() => {
  const report = weaponsQuery.data.value
  if (!report) return null
  return flattenWeapons(report, { className: classFilter.value, specificMode: modeFilter.value })
})

const categoryBars = computed(() =>
  (flattened.value?.categories ?? []).map((category) => ({
    key: category.key,
    label: category.name,
    value: category.kills,
    display: formatInteger(category.kills),
    color: MODE_COLOR[bucket.value],
    tooltip: isUnknownKillCategory(category.name) ? UNKNOWN_KILLS_EXPLANATION : undefined,
  })),
)

const donutSegments = computed(() =>
  categoryShares(flattened.value?.categories ?? []).map((share, index) => ({
    label: share.label,
    value: share.value,
    color: DONUT_COLORS[index % DONUT_COLORS.length] as string,
    tooltip: isUnknownKillCategory(share.label) ? UNKNOWN_KILLS_EXPLANATION : undefined,
  })),
)

const WEAPON_PAGE = 25
const showAllWeapons = ref(false)
watch([bucket, classFilter, modeFilter], () => {
  showAllWeapons.value = false
})

const weaponRows = computed(() => {
  const rows = flattened.value?.weapons ?? []
  return showAllWeapons.value ? rows : rows.slice(0, WEAPON_PAGE)
})

const hiddenWeaponCount = computed(() =>
  Math.max(0, (flattened.value?.weapons.length ?? 0) - weaponRows.value.length),
)

// -- Deaths -------------------------------------------------------------------

const deathBars = computed(() =>
  [...(deathsQuery.data.value?.modes ?? [])]
    .filter((mode) => mode.deaths > 0)
    .sort((a, b) => b.deaths - a.deaths)
    .map((mode) => {
      const label = humanizeModeName(mode.specificActivityMode)
      return {
        key: String(mode.specificActivityModeId),
        label,
        value: mode.deaths,
        display: formatInteger(mode.deaths),
        color: 'var(--color-bar)',
        tooltip: isMissingActivityMode(label) ? MISSING_ACTIVITY_MODE_EXPLANATION : undefined,
      }
    }),
)
</script>

<template>
  <div class="container combat">
    <div class="combat-controls">
      <SegmentedControl
        :model-value="bucket"
        :options="MODES"
        label="Activity bucket"
        @update:model-value="setFilters({ mode: $event })"
      />
      <SegmentedControl
        v-if="classOptions.length > 0"
        :model-value="classFilter"
        :options="classOptions"
        label="Character class"
        @update:model-value="setFilters({ class: $event })"
      />
      <label v-if="modeOptions.length > 0" class="mode-select">
        <span class="mode-select-label">Activity</span>
        <select
          class="mode-select-input"
          :value="modeFilter"
          @change="setFilters({ list: ($event.target as HTMLSelectElement).value })"
        >
          <option v-for="mode in modeOptions" :key="mode" :value="mode">
            {{ mode === ALL ? 'All activities' : humanizeModeName(mode) }}
          </option>
        </select>
      </label>
    </div>

    <ReportSection
      title="Weapon & ability kills"
      :subtitle="
        flattened && flattened.totalKills > 0
          ? `${formatInteger(flattened.totalKills)} kills in the current selection`
          : undefined
      "
    >
      <div v-if="weaponsQuery.isPending.value" class="loading-stack">
        <SkeletonBlock v-for="n in 5" :key="n" height="2rem" />
      </div>

      <ErrorState
        v-else-if="weaponsQuery.isError.value"
        :error="weaponsQuery.error.value"
        context="Couldn't load weapon data"
        @retry="weaponsQuery.refetch()"
      />

      <EmptyState
        v-else-if="!flattened || flattened.totalKills === 0"
        title="No recorded kills here"
        :description="`No weapon or ability kills are recorded for ${bucket} with the current filters.`"
      />

      <template v-else>
        <div class="weapons-layout">
          <div class="category-block">
            <h3 class="block-title">By category</h3>
            <BarList :items="categoryBars" unit="kills" />
          </div>
          <div v-if="donutSegments.length > 1" class="donut-block">
            <h3 class="block-title">Category share</h3>
            <DonutChart :segments="donutSegments" unit="kills" />
          </div>
        </div>

        <div class="weapon-table-block">
          <h3 class="block-title">Ranked weapons & abilities</h3>
          <table class="weapon-table">
            <thead>
              <tr>
                <th scope="col" class="rank-col" aria-label="Rank">#</th>
                <th scope="col">Weapon</th>
                <th scope="col" class="category-col">Category</th>
                <th scope="col" class="num">Kills</th>
                <th scope="col" class="num">Share</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="(weapon, index) in weaponRows" :key="weapon.key">
                <td class="rank-col tnum">{{ index + 1 }}</td>
                <td>
                  <span class="weapon-cell">
                    <img
                      v-if="weapon.iconUrl"
                      class="weapon-icon"
                      :src="weapon.iconUrl"
                      alt=""
                      width="24"
                      height="24"
                      loading="lazy"
                    />
                    <AbilityKillIcon
                      v-else-if="abilityKillIconKind(weapon.name)"
                      class="weapon-icon ability-kill-icon"
                      :kind="abilityKillIconKind(weapon.name)!"
                    />
                    <span v-else class="weapon-icon weapon-icon--fallback" aria-hidden="true" />
                    {{ weapon.name }}
                  </span>
                </td>
                <td class="category-col">
                  <ExplainedLabel
                    v-if="isUnknownKillCategory(weapon.categoryName)"
                    :text="weapon.categoryName"
                    :explanation="UNKNOWN_KILLS_EXPLANATION"
                  />
                  <template v-else>{{ weapon.categoryName }}</template>
                </td>
                <td class="num tnum">{{ formatInteger(weapon.kills) }}</td>
                <td class="num tnum share">
                  {{ ((weapon.kills / flattened.totalKills) * 100).toFixed(1) }}%
                </td>
              </tr>
            </tbody>
          </table>
          <div v-if="hiddenWeaponCount > 0" class="table-more">
            <AppButton size="sm" variant="ghost" @click="showAllWeapons = true">
              Show all {{ formatInteger(flattened.weapons.length) }} entries
            </AppButton>
          </div>
        </div>
      </template>
    </ReportSection>

    <ReportSection
      title="Deaths"
      :subtitle="
        deathsQuery.data.value
          ? `${formatInteger(deathsQuery.data.value.deaths)} deaths recorded in ${bucket}`
          : undefined
      "
    >
      <div v-if="deathsQuery.isPending.value" class="loading-stack">
        <SkeletonBlock v-for="n in 3" :key="n" height="2rem" />
      </div>

      <ErrorState
        v-else-if="deathsQuery.isError.value"
        :error="deathsQuery.error.value"
        context="Couldn't load death data"
        @retry="deathsQuery.refetch()"
      />

      <EmptyState
        v-else-if="deathBars.length === 0"
        title="No deaths recorded"
        :description="`A spotless record in ${bucket} — or no activity at all.`"
      />

      <BarList v-else :items="deathBars" unit="deaths" />
    </ReportSection>
  </div>
</template>

<style scoped>
.combat {
  padding-top: var(--space-5);
}

.combat-controls {
  display: flex;
  align-items: center;
  gap: var(--space-3);
  flex-wrap: wrap;
}

.mode-select {
  position: relative;
  display: inline-flex;
  align-items: center;
  gap: var(--space-2);
}

.mode-select::after {
  content: '';
  position: absolute;
  right: 0.875rem;
  top: 50%;
  width: 0.4rem;
  height: 0.4rem;
  border-right: 1px solid var(--color-text-muted);
  border-bottom: 1px solid var(--color-text-muted);
  transform: translateY(-70%) rotate(45deg);
  pointer-events: none;
}

.mode-select-label {
  font-size: var(--text-xs);
  color: var(--color-text-muted);
  text-transform: uppercase;
  letter-spacing: 0.05em;
}

.mode-select-input {
  appearance: none;
  height: 2.25rem;
  padding: 0 2.25rem 0 var(--space-3);
  color: var(--color-text);
  background-color: var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-md);
  font-size: var(--text-sm);
  max-width: 16rem;
  transition:
    background-color var(--transition-fast),
    border-color var(--transition-fast);
}

.mode-select-input:hover {
  color: var(--color-text);
  background-color: var(--color-surface-raised);
  border-color: var(--color-border-strong);
}

.mode-select-input:focus {
  color: var(--color-text);
  background-color: var(--color-surface-raised);
  border-color: var(--color-accent);
}

.loading-stack {
  display: flex;
  flex-direction: column;
  gap: var(--space-2);
}

.block-title {
  font-size: var(--text-sm);
  font-weight: 550;
  color: var(--color-text-secondary);
  margin-bottom: var(--space-3);
}

.weapons-layout {
  display: grid;
  grid-template-columns: minmax(0, 3fr) minmax(0, 2fr);
  gap: var(--space-6);
}

@media (max-width: 52rem) {
  .weapons-layout {
    grid-template-columns: 1fr;
  }
}

.weapon-table-block {
  margin-top: var(--space-6);
}

.weapon-table {
  font-size: var(--text-sm);
}

.weapon-table th {
  font-size: var(--text-xs);
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: var(--color-text-muted);
  padding: var(--space-2) var(--space-3) var(--space-2) 0;
  border-bottom: 1px solid var(--color-border);
}

.weapon-table td {
  padding: var(--space-2) var(--space-3) var(--space-2) 0;
  border-bottom: 1px solid var(--color-border);
}

.weapon-table .num {
  text-align: right;
}

.rank-col {
  width: 2rem;
  color: var(--color-text-muted);
}

.weapon-cell {
  display: inline-flex;
  align-items: center;
  gap: var(--space-2);
}

.weapon-icon {
  width: 1.5rem;
  height: 1.5rem;
  border-radius: var(--radius-sm);
  background: var(--color-surface-raised);
  flex: none;
}

.ability-kill-icon {
  width: 1.875rem;
  height: 1.875rem;
  padding: 0.125rem;
  color: var(--color-text-secondary);
}

.share {
  color: var(--color-text-secondary);
}

@media (max-width: 40rem) {
  .category-col {
    display: none;
  }
}

.table-more {
  margin-top: var(--space-3);
  display: flex;
  justify-content: center;
}
</style>
