<script setup lang="ts">
import { platformLabel } from '@/lib/platform'
import type { DestinyMembership } from '@/lib/api/types'
import { useSessionStore } from '@/stores/session'

const session = useSessionStore()
const emit = defineEmits<{ selected: [membership: DestinyMembership] }>()

function select(membership: DestinyMembership) {
  session.selectMembership(membership)
  emit('selected', membership)
}
</script>

<template>
  <section class="membership-picker" aria-labelledby="membership-picker-title">
    <p class="picker-kicker">Multiple Destiny profiles found</p>
    <h1 id="membership-picker-title" class="picker-title">Which Guardian is yours?</h1>
    <p class="picker-copy">
      Choose the platform whose history you want to use. We won’t generate a report or open Your
      Story until you make a selection.
    </p>

    <div class="membership-list">
      <button
        v-for="membership in session.selectableMemberships"
        :key="`${membership.membershipType}:${membership.membershipId}`"
        class="membership-option"
        type="button"
        @click="select(membership)"
      >
        <span class="platform-mark" aria-hidden="true">
          {{ platformLabel(membership.membershipType).slice(0, 2) }}
        </span>
        <span class="membership-copy">
          <strong>{{ platformLabel(membership.membershipType) }}</strong>
          <span>{{
            membership.displayName || membership.bungieGlobalDisplayName || 'Destiny profile'
          }}</span>
        </span>
        <span class="choose-label">Choose <span aria-hidden="true">→</span></span>
      </button>
    </div>

    <p class="picker-note">You can switch profiles later from your account menu.</p>
  </section>
</template>

<style scoped>
.membership-picker {
  width: 100%;
  max-width: 42rem;
  margin-inline: auto;
}

.picker-kicker {
  margin-bottom: var(--space-2);
  color: var(--color-accent);
  font-size: var(--text-xs);
  font-weight: 650;
  letter-spacing: 0.09em;
  text-transform: uppercase;
}

.picker-title {
  font-size: clamp(var(--text-xl), 5vw, var(--text-2xl));
  line-height: 1.1;
}

.picker-copy {
  max-width: 36rem;
  margin-top: var(--space-3);
  color: var(--color-text-secondary);
  line-height: 1.65;
}

.membership-list {
  display: grid;
  gap: var(--space-2);
  margin-top: var(--space-5);
}

.membership-option {
  display: grid;
  grid-template-columns: auto 1fr auto;
  align-items: center;
  gap: var(--space-3);
  width: 100%;
  padding: var(--space-3);
  color: var(--color-text);
  text-align: left;
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-md);
  transition:
    border-color var(--transition-fast),
    background-color var(--transition-fast);
}

.membership-option:hover,
.membership-option:focus-visible {
  background: var(--color-surface-raised);
  border-color: var(--color-accent);
}

.platform-mark {
  display: grid;
  width: 2.5rem;
  height: 2.5rem;
  place-items: center;
  color: var(--color-text-inverse);
  font-size: var(--text-xs);
  font-weight: 750;
  letter-spacing: 0.04em;
  background: var(--color-accent);
  border-radius: var(--radius-sm);
}

.membership-copy {
  display: grid;
  gap: 0.15rem;
  min-width: 0;
}

.membership-copy strong {
  font-size: var(--text-sm);
}

.membership-copy span {
  overflow: hidden;
  color: var(--color-text-muted);
  font-size: var(--text-xs);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.choose-label {
  color: var(--color-text-secondary);
  font-size: var(--text-xs);
  font-weight: 600;
}

.picker-note {
  margin-top: var(--space-3);
  color: var(--color-text-muted);
  font-size: var(--text-xs);
}

@media (max-width: 34rem) {
  .choose-label {
    position: absolute;
    width: 1px;
    height: 1px;
    overflow: hidden;
    clip: rect(0 0 0 0);
  }
}
</style>
