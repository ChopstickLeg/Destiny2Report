<script setup lang="ts">
import { computed } from 'vue'
import { RouterLink, type RouteLocationRaw } from 'vue-router'
import { bungieUrl } from '@/lib/api/bungie'

const props = withDefaults(
  defineProps<{
    name: string
    /** Bungie display code; rendered as #0000 when present. */
    code?: number | null
    detail?: string
    emblemUrl?: string | null
    to?: RouteLocationRaw
  }>(),
  { code: null, detail: undefined, emblemUrl: null, to: undefined },
)

const resolvedEmblemUrl = computed(() => bungieUrl(props.emblemUrl))

function pad(code: number): string {
  return String(code).padStart(4, '0')
}
</script>

<template>
  <component :is="to ? RouterLink : 'div'" class="identity" :to="to">
    <img
      v-if="resolvedEmblemUrl"
      class="emblem"
      :src="resolvedEmblemUrl"
      alt=""
      width="40"
      height="40"
      loading="lazy"
    />
    <span v-else class="emblem emblem--fallback" aria-hidden="true" />
    <span class="identity-text">
      <span class="identity-name">
        {{ name }}<span v-if="code !== null" class="identity-code">#{{ pad(code) }}</span>
      </span>
      <span v-if="detail" class="identity-detail">{{ detail }}</span>
    </span>
    <slot />
  </component>
</template>

<style scoped>
.identity {
  display: flex;
  align-items: center;
  gap: var(--space-3);
  min-width: 0;
  padding: var(--space-2) var(--space-3);
  border-radius: var(--radius-md);
  color: var(--color-text);
}

a.identity {
  transition: background-color var(--transition-fast);
}

a.identity:hover {
  background: var(--color-surface);
  color: var(--color-text);
}

.emblem {
  width: 2.5rem;
  height: 2.5rem;
  flex: none;
  border-radius: var(--radius-sm);
  object-fit: cover;
  background: var(--color-surface-raised);
}

.emblem--fallback {
  display: block;
}

.identity-text {
  display: flex;
  flex-direction: column;
  min-width: 0;
}

.identity-name {
  font-weight: 550;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.identity-code {
  color: var(--color-text-muted);
  font-weight: 400;
}

.identity-detail {
  font-size: var(--text-xs);
  color: var(--color-text-secondary);
}
</style>
