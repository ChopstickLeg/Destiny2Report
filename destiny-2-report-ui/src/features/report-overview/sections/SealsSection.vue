<script setup lang="ts">
import ReportSection from '@/components/base/ReportSection.vue'
import type { DestinyReport } from '@/lib/api/types'

defineProps<{ report: DestinyReport }>()
</script>

<template>
  <ReportSection id="seals" title="Seals">
    <ul class="seal-list">
      <li v-for="seal in report.triumphSeals" :key="seal.name" class="seal">
        <img
          v-if="seal.iconUrl"
          class="seal-icon"
          :src="seal.iconUrl"
          alt=""
          width="48"
          height="48"
          loading="lazy"
        />
        <span v-else class="seal-icon seal-icon--fallback" aria-hidden="true" />
        <span class="seal-text">
          <span class="seal-name">{{ seal.name }}</span>
          <span class="seal-description">{{ seal.description }}</span>
        </span>
      </li>
    </ul>
  </ReportSection>
</template>

<style scoped>
.seal-list {
  list-style: none;
  padding: 0;
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(min(18rem, 100%), 1fr));
  gap: var(--space-3);
}

.seal {
  display: flex;
  gap: var(--space-3);
  align-items: flex-start;
}

.seal-icon {
  width: 3rem;
  height: 3rem;
  flex: none;
  background: var(--color-surface-raised);
  border-radius: var(--radius-sm);
}

.seal-name {
  display: block;
  font-weight: 550;
  font-size: var(--text-sm);
}

.seal-description {
  display: block;
  font-size: var(--text-xs);
  color: var(--color-text-muted);
}
</style>
