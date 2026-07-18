<script setup lang="ts">
import { computed } from 'vue'
import ReportSection from '@/components/base/ReportSection.vue'
import { bungieUrl } from '@/lib/api/bungie'
import type { DestinyReport } from '@/lib/api/types'
import { formatHours, parseTimeSpan } from '@/lib/formatting/duration'

const props = defineProps<{ report: DestinyReport }>()

const emblems = computed(() =>
  props.report.mostUsedEmblems
    .map((emblem) => ({
      ...emblem,
      backgroundUrl: bungieUrl(emblem.backgroundUrl),
      seconds: parseTimeSpan(emblem.totalPlaytime) ?? 0,
    }))
    .filter((emblem) => emblem.seconds > 0)
    .sort((a, b) => b.seconds - a.seconds),
)
</script>

<template>
  <ReportSection id="emblems" title="Emblems you lived in" subtitle="Ranked by time worn">
    <ul class="emblem-list">
      <li v-for="emblem in emblems" :key="emblem.name" class="emblem-card">
        <div class="emblem-art">
          <img
            v-if="emblem.backgroundUrl"
            :src="emblem.backgroundUrl"
            :alt="emblem.name"
            loading="lazy"
            width="474"
            height="96"
          />
          <span v-else class="emblem-art-fallback" aria-hidden="true" />
        </div>
        <div class="emblem-meta">
          <span class="emblem-name">{{ emblem.name }}</span>
          <span class="emblem-time tnum">{{ formatHours(emblem.seconds) }}</span>
        </div>
      </li>
    </ul>
  </ReportSection>
</template>

<style scoped>
.emblem-list {
  list-style: none;
  padding: 0;
  display: grid;
  grid-auto-flow: column;
  grid-auto-columns: minmax(16rem, 1fr);
  gap: var(--space-3);
  overflow-x: auto;
  padding-bottom: var(--space-2);
  scroll-snap-type: x proximity;
}

.emblem-card {
  scroll-snap-align: start;
}

.emblem-art {
  /* Destiny emblem banners are 474×96; fixed ratio prevents layout shift. */
  aspect-ratio: 474 / 96;
  background: var(--color-surface-raised);
  border-radius: var(--radius-sm);
  overflow: hidden;
}

.emblem-art img {
  width: 100%;
  height: 100%;
  object-fit: cover;
}

.emblem-art-fallback {
  display: block;
  width: 100%;
  height: 100%;
}

.emblem-meta {
  display: flex;
  justify-content: space-between;
  align-items: baseline;
  gap: var(--space-2);
  margin-top: var(--space-2);
}

.emblem-name {
  font-size: var(--text-sm);
  font-weight: 550;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.emblem-time {
  font-size: var(--text-xs);
  color: var(--color-text-muted);
  flex: none;
}
</style>
