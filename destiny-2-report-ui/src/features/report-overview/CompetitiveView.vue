<script setup lang="ts">
import { computed } from 'vue'
import EmptyState from '@/components/base/EmptyState.vue'
import { hasCompetitiveData } from './report-view'
import CompetitiveSection from './sections/CompetitiveSection.vue'
import { useReportIdentity, useReportQuery } from './useReport'

const identity = useReportIdentity()
const { data } = useReportQuery(identity)
const report = computed(() => data.value ?? null)
</script>

<template>
  <div v-if="report" class="container competitive-view">
    <CompetitiveSection v-if="hasCompetitiveData(report)" :report="report" />
    <EmptyState
      v-else
      class="empty-state"
      title="No competitive history"
      description="Crucible and Gambit records will appear here once matches are recorded."
    />
  </div>
</template>

<style scoped>
.empty-state {
  margin-top: var(--space-7);
}
</style>
