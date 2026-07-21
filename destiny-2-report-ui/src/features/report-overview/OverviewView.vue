<script setup lang="ts">
import { computed } from 'vue'
import EmptyState from '@/components/base/EmptyState.vue'
import { useReportIdentity, useReportQuery } from './useReport'
import {
  hasEmblemData,
  hasOdditiesData,
  hasSealsData,
  hasSocialData,
  hasTimeData,
} from './report-view'
import TimeSpentSection from './sections/TimeSpentSection.vue'
import SocialSection from './sections/SocialSection.vue'
import SealsSection from './sections/SealsSection.vue'
import GuardianOdditiesSection from './sections/GuardianOdditiesSection.vue'
import EmblemsSection from './sections/EmblemsSection.vue'

// Served from the parent layout's cache; never refetches here.
const identity = useReportIdentity()
const { data } = useReportQuery(identity)
const report = computed(() => data.value ?? null)

const isSparse = computed(() => {
  const r = report.value
  if (!r) return false
  return (
    !hasTimeData(r) &&
    !hasSocialData(r) &&
    !hasSealsData(r) &&
    !hasOdditiesData(r) &&
    !hasEmblemData(r)
  )
})
</script>

<template>
  <div v-if="report" class="container overview">
    <EmptyState
      v-if="isSparse"
      class="sparse-note"
      title="Not much history here yet"
      description="This account has very little recorded activity. Sections appear as soon as there is real data to show."
    />

    <template v-else>
      <TimeSpentSection v-if="hasTimeData(report)" :report="report" />
      <SocialSection v-if="hasSocialData(report)" :report="report" />
      <SealsSection v-if="hasSealsData(report)" :report="report" />
      <GuardianOdditiesSection v-if="hasOdditiesData(report)" :report="report" />
      <EmblemsSection v-if="hasEmblemData(report)" :report="report" />
    </template>
  </div>
</template>

<style scoped>
.sparse-note {
  margin-top: var(--space-6);
}
</style>
