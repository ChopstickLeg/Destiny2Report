<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import ErrorState from '@/components/base/ErrorState.vue'
import SkeletonBlock from '@/components/base/SkeletonBlock.vue'
import { apiFetch } from '@/lib/api/http'
import type { StatusResponse } from '@/lib/api/types'
import { formatDateTime, parseApiDate } from '@/lib/formatting/dates'

const query = useQuery({
  queryKey: ['status'],
  queryFn: ({ signal }) => apiFetch<StatusResponse>('/status', { signal }),
  refetchInterval: 60_000,
})
</script>

<template>
  <div class="status container">
    <h1 class="status-title">Service status</h1>

    <SkeletonBlock v-if="query.isPending.value" height="6rem" radius="var(--radius-md)" />

    <ErrorState
      v-else-if="query.isError.value"
      :error="query.error.value"
      context="The API is not responding"
      @retry="query.refetch()"
    />

    <dl v-else-if="query.data.value" class="status-grid">
      <div class="status-item">
        <dt>Status</dt>
        <dd>
          <span
            class="status-dot"
            :class="{ 'status-dot--ok': query.data.value.status === 'ok' }"
            aria-hidden="true"
          />
          {{ query.data.value.status }}
        </dd>
      </div>
      <div class="status-item">
        <dt>Environment</dt>
        <dd>{{ query.data.value.environment }}</dd>
      </div>
      <div class="status-item">
        <dt>Server time (UTC)</dt>
        <dd class="tnum">
          {{
            parseApiDate(query.data.value.serverTimeUtc)
              ? formatDateTime(parseApiDate(query.data.value.serverTimeUtc)!)
              : query.data.value.serverTimeUtc
          }}
        </dd>
      </div>
    </dl>
  </div>
</template>

<style scoped>
.status {
  padding-top: var(--space-7);
  max-width: 36rem;
}

.status-title {
  font-size: var(--text-xl);
  margin-bottom: var(--space-5);
}

.status-grid {
  display: flex;
  flex-direction: column;
  gap: var(--space-3);
  padding: var(--space-4);
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-md);
}

.status-item {
  display: flex;
  justify-content: space-between;
  gap: var(--space-4);
  font-size: var(--text-sm);
}

.status-item dt {
  color: var(--color-text-muted);
}

.status-dot {
  display: inline-block;
  width: 0.5rem;
  height: 0.5rem;
  border-radius: var(--radius-full);
  background: var(--color-negative);
  margin-right: var(--space-1);
}

.status-dot--ok {
  background: var(--color-positive);
}
</style>
