<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import type { ReportQueueStatusResponse } from '@/lib/api/types'
import { formatInteger } from '@/lib/formatting/numbers'
import { formatDurationCompact } from '@/lib/formatting/duration'

const props = defineProps<{
  latest: ReportQueueStatusResponse | null
  reconnectAttempt: number
  startedAt: number | null
}>()

const now = ref(Date.now())
let timer: ReturnType<typeof setInterval> | undefined

onMounted(() => {
  timer = setInterval(() => {
    now.value = Date.now()
  }, 1_000)
})

onBeforeUnmount(() => clearInterval(timer))

const elapsed = computed(() => {
  if (!props.startedAt) return null
  return formatDurationCompact((now.value - props.startedAt) / 1000)
})

const statusLine = computed(() => {
  const status = props.latest
  if (!status) return 'Contacting the crawler…'
  if (status.status === 'queued') {
    if (status.position !== null && status.position > 0) {
      return status.queueLength > 0
        ? `Queued at position ${formatInteger(Number(status.position))} of ${formatInteger(Number(status.queueLength))}`
        : `Queued at position ${formatInteger(Number(status.position))}`
    }
    return 'Queued and waiting for a crawler'
  }
  if (status.status === 'running') {
    return status.progress?.label ? status.progress.label : 'Crawling activity history…'
  }
  return 'Working…'
})

const progressFraction = computed(() => {
  const progress = props.latest?.progress
  if (!progress || progress.current === null || progress.total === null || progress.total <= 0) {
    return null
  }
  return Math.min(1, Number(progress.current) / Number(progress.total))
})

const progressDetail = computed(() => {
  const progress = props.latest?.progress
  if (!progress || progress.current === null || progress.total === null) return null
  return `${formatInteger(Number(progress.current))} of ${formatInteger(Number(progress.total))}`
})
</script>

<template>
  <div class="progress">
    <div class="progress-status" role="status" aria-live="polite">
      <p class="status-line">{{ statusLine }}</p>
      <p v-if="progressDetail" class="status-detail tnum">{{ progressDetail }}</p>
    </div>

    <div
      class="progress-track"
      role="progressbar"
      :aria-valuemin="0"
      :aria-valuemax="100"
      :aria-valuenow="progressFraction === null ? undefined : Math.round(progressFraction * 100)"
      :aria-valuetext="progressFraction === null ? 'In progress' : undefined"
      aria-label="Crawl progress"
    >
      <div
        v-if="progressFraction !== null"
        class="progress-fill"
        :style="{ width: `${(progressFraction * 100).toFixed(1)}%` }"
      />
      <div v-else class="progress-fill progress-fill--indeterminate" />
    </div>

    <div class="progress-meta">
      <span v-if="elapsed" class="tnum">Elapsed {{ elapsed }}</span>
      <span v-if="reconnectAttempt > 0" class="progress-reconnect">
        Connection interrupted. Retrying ({{ reconnectAttempt }})…
      </span>
    </div>

    <p class="progress-note">
      A full history crawl can take a while for veteran accounts. You can keep this page open. The
      report appears automatically when it's ready.
    </p>
  </div>
</template>

<style scoped>
.progress-status {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: var(--space-3);
}

.status-line {
  font-weight: 550;
}

.status-detail {
  font-size: var(--text-sm);
  color: var(--color-text-secondary);
}

.progress-track {
  margin-top: var(--space-3);
  height: 6px;
  border-radius: var(--radius-full);
  background: var(--color-bar-track);
  overflow: hidden;
  position: relative;
}

.progress-fill {
  height: 100%;
  background: var(--color-accent);
  border-radius: var(--radius-full);
  transition: width var(--transition-medium);
}

.progress-fill--indeterminate {
  width: 30%;
  position: absolute;
  animation: slide 1.8s ease-in-out infinite;
}

@keyframes slide {
  0% {
    left: -30%;
  }
  100% {
    left: 100%;
  }
}

@media (prefers-reduced-motion: reduce) {
  .progress-fill--indeterminate {
    animation: none;
    left: 0;
    width: 100%;
    opacity: 0.4;
  }
}

.progress-meta {
  margin-top: var(--space-2);
  display: flex;
  gap: var(--space-4);
  font-size: var(--text-xs);
  color: var(--color-text-muted);
}

.progress-reconnect {
  color: var(--color-warning);
}

.progress-note {
  margin-top: var(--space-4);
  font-size: var(--text-sm);
  color: var(--color-text-secondary);
}
</style>
