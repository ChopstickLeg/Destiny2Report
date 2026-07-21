<script setup lang="ts">
import { computed } from 'vue'
import EmptyState from '@/components/base/EmptyState.vue'
import { hasEndgameData } from './report-view'
import EndgameSection from './sections/EndgameSection.vue'
import { useReportIdentity, useReportQuery } from './useReport'

const identity = useReportIdentity()
const { data } = useReportQuery(identity)
const report = computed(() => data.value ?? null)
</script>

<template>
  <div v-if="report" class="container endgame-view">
    <EndgameSection v-if="hasEndgameData(report)" :report="report" />
    <EmptyState
      v-else
      class="empty-state"
      title="No endgame history"
      description="Raid, dungeon, and conquest records will appear here once attempts are recorded."
    />
  </div>
</template>

<style scoped>
.empty-state {
  margin-top: var(--space-7);
}
</style>
