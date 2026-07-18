<script setup lang="ts">
import { computed } from 'vue'
import { RouterLink, useRoute } from 'vue-router'
import GlobalSearch from './GlobalSearch.vue'
import AccountControl from './AccountControl.vue'

const route = useRoute()
// The home page presents its own primary search; avoid two competing fields.
const showSearch = computed(() => route.name !== 'home' && route.name !== 'search')
</script>

<template>
  <header class="header">
    <div class="container header-row">
      <RouterLink class="wordmark" :to="{ name: 'home' }">
        <img class="mark" src="/favicon.svg" alt="" />
        <span class="wordmark-text display">Destiny 2 Report</span>
      </RouterLink>

      <div v-if="showSearch" class="header-search">
        <GlobalSearch />
      </div>

      <AccountControl class="account-control" />
    </div>
  </header>
</template>

<style scoped>
.header {
  width: 100%;
  border-bottom: 1px solid var(--color-border);
  background: var(--color-bg);
}

.header-row {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(12rem, 24rem) minmax(0, 1fr);
  align-items: center;
  gap: var(--space-4);
  max-width: none;
  min-height: 3.5rem;
  padding-block: var(--space-2);
}

.wordmark {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  justify-self: start;
  color: var(--color-text);
}

.wordmark:hover {
  color: var(--color-text);
}

.mark {
  width: 1.375rem;
  height: 1.375rem;
  flex: none;
}

.wordmark-text {
  font-size: var(--text-md);
  font-weight: 600;
  white-space: nowrap;
}

.header-search {
  grid-column: 2;
  width: 100%;
}

.account-control {
  grid-column: 3;
  justify-self: end;
}

/* Mobile: search drops to its own full-width row beneath the wordmark. */
@media (max-width: 40rem) {
  .header-row {
    grid-template-columns: minmax(0, 1fr) auto;
  }

  .header-search {
    grid-column: 1 / -1;
    grid-row: 2;
    padding-bottom: var(--space-2);
  }

  .account-control {
    grid-column: 2;
  }
}
</style>
