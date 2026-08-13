<script setup lang="ts">
import { computed } from 'vue'
import { RouterLink } from 'vue-router'
import AppButton from '@/components/base/AppButton.vue'
import { bungieUrl } from '@/lib/api/bungie'
import type { DestinyReport } from '@/lib/api/types'
import { platformLabel } from '@/lib/platform'
import { parseApiDate, formatRelative, formatDateTime } from '@/lib/formatting/dates'

const props = defineProps<{
  report: DestinyReport
  refreshing: boolean
  queueAccessPending?: boolean
  signInRequired?: boolean
}>()

const emit = defineEmits<{ refresh: [] }>()

const backgroundUrl = computed(() => bungieUrl(props.report.mostUsedEmblems[0]?.backgroundUrl))

const updatedAt = computed(() =>
  parseApiDate(props.report.lastCrawledAtUtc ?? props.report.crawledAt),
)

const params = computed(() => ({
  membershipTypeId: props.report.platformId,
  membershipId: props.report.playerMembershipId,
}))

const storyPreviewRoute = computed(() =>
  import.meta.env.DEV
    ? {
        name: 'story-preview' as const,
        params: {
          membershipTypeId: props.report.platformId,
          membershipId: props.report.playerMembershipId,
        },
      }
    : undefined,
)
</script>

<template>
  <header class="masthead">
    <div
      class="masthead-art"
      :style="backgroundUrl ? { backgroundImage: `url(${backgroundUrl})` } : undefined"
      aria-hidden="true"
    />
    <div class="masthead-scrim" aria-hidden="true" />

    <div class="container masthead-content">
      <div class="masthead-identity">
        <h1 class="masthead-name display">
          {{ report.displayName
          }}<span class="masthead-code">#{{ String(report.displayCode).padStart(4, '0') }}</span>
        </h1>
        <p class="masthead-meta">
          {{ platformLabel(report.platformId) }}
          <template v-if="updatedAt">
            ·
            <span :title="formatDateTime(updatedAt)">Updated {{ formatRelative(updatedAt) }}</span>
          </template>
        </p>
      </div>
      <div class="masthead-actions">
        <AppButton v-if="storyPreviewRoute" size="sm" :to="storyPreviewRoute">
          View story
        </AppButton>
        <AppButton size="sm" :disabled="refreshing || queueAccessPending" @click="emit('refresh')">
          {{
            refreshing
              ? 'Refreshing…'
              : queueAccessPending
                ? 'Checking access…'
                : signInRequired
                  ? 'Sign in to refresh'
                  : 'Refresh report'
          }}
        </AppButton>
      </div>
    </div>

    <nav class="masthead-nav" aria-label="Report sections">
      <div class="container masthead-tabs">
        <RouterLink
          class="tab"
          :to="{ name: 'report-overview', params }"
          exact-active-class="tab--active"
        >
          Overview
        </RouterLink>
        <RouterLink
          class="tab"
          :to="{ name: 'report-competitive', params }"
          active-class="tab--active"
        >
          Competitive
        </RouterLink>
        <RouterLink class="tab" :to="{ name: 'report-endgame', params }" active-class="tab--active">
          Endgame
        </RouterLink>
        <RouterLink class="tab" :to="{ name: 'report-combat', params }" active-class="tab--active">
          Combat
        </RouterLink>
        <RouterLink
          class="tab"
          :to="{ name: 'report-activities', params }"
          active-class="tab--active"
        >
          Activities
        </RouterLink>
      </div>
    </nav>
  </header>
</template>

<style scoped>
.masthead {
  position: relative;
  border-bottom: 1px solid var(--color-border);
  isolation: isolate;
}

.masthead-art {
  position: absolute;
  inset: 0;
  background-size: cover;
  background-position: center 20%;
  z-index: -2;
}

/* Readability scrim: emblem art stays visible but text always wins. */
.masthead-scrim {
  position: absolute;
  inset: 0;
  z-index: -1;
  background:
    linear-gradient(
      to right,
      rgb(var(--color-bg-rgb) / 0.92),
      rgb(var(--color-bg-rgb) / 0.55) 55%,
      rgb(var(--color-bg-rgb) / 0.75)
    ),
    linear-gradient(to top, var(--color-bg), transparent 40%);
}

.masthead-content {
  display: flex;
  align-items: flex-end;
  justify-content: space-between;
  gap: var(--space-4);
  padding-top: var(--space-8);
  padding-bottom: var(--space-4);
  flex-wrap: wrap;
}

.masthead-actions {
  display: flex;
  gap: var(--space-2);
}

.masthead-name {
  font-size: clamp(var(--text-xl), 5vw, var(--text-2xl));
  font-weight: 700;
  text-shadow: 0 1px 8px rgb(0 0 0 / 0.5);
}

.masthead-code {
  color: var(--color-text-secondary);
  font-weight: 400;
}

.masthead-meta {
  margin-top: var(--space-1);
  font-size: var(--text-sm);
  color: var(--color-text-secondary);
  text-shadow: 0 1px 6px rgb(0 0 0 / 0.6);
}

.masthead-nav {
  background: rgb(var(--color-bg-rgb) / 0.75);
  backdrop-filter: blur(4px);
  border-top: 1px solid rgb(255 255 255 / 0.06);
  overflow-x: auto;
  scrollbar-width: none;
}

.masthead-nav::-webkit-scrollbar {
  display: none;
}

.masthead-tabs {
  display: flex;
  gap: var(--space-5);
}

.tab {
  padding: var(--space-3) 0;
  font-size: var(--text-sm);
  font-weight: 550;
  color: var(--color-text-secondary);
  border-bottom: 2px solid transparent;
  margin-bottom: -1px;
  white-space: nowrap;
}

.tab:hover {
  color: var(--color-text);
}

.tab--active {
  color: var(--color-text);
  border-bottom-color: var(--color-accent);
}
</style>
