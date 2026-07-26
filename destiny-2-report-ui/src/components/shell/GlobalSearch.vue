<script setup lang="ts">
import { ref } from 'vue'
import { useRouter } from 'vue-router'

withDefaults(
  defineProps<{
    /** Larger presentation for the home page. */
    size?: 'compact' | 'large'
  }>(),
  { size: 'compact' },
)

const router = useRouter()
const query = ref('')

function submit() {
  const q = query.value.trim()
  if (q.length === 0) return
  void router.push({ name: 'search', query: { q } })
}
</script>

<template>
  <form
    class="search"
    :class="`search--${size}`"
    role="search"
    aria-label="Player search"
    @submit.prevent="submit"
  >
    <svg class="search-icon" viewBox="0 0 20 20" aria-hidden="true">
      <circle cx="9" cy="9" r="6" fill="none" stroke="currentColor" stroke-width="1.5" />
      <line x1="13.5" y1="13.5" x2="18" y2="18" stroke="currentColor" stroke-width="1.5" />
    </svg>
    <input
      v-model="query"
      class="search-input"
      type="search"
      name="q"
      :placeholder="
        size === 'large' ? 'Search by Bungie name, e.g. Guardian#1234' : 'Find a Guardian'
      "
      aria-label="Search players by Bungie name"
      autocomplete="off"
      autocapitalize="off"
      spellcheck="false"
      enterkeyhint="search"
    />
  </form>
</template>

<style scoped>
.search {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  width: 100%;
  max-width: 24rem;
  padding: 0 var(--space-3);
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-md);
  transition: border-color var(--transition-fast);
}

.search:focus-within {
  border-color: var(--color-border-strong);
}

.search--large {
  max-width: 34rem;
  border-color: var(--color-border-strong);
}

.search-icon {
  width: 1rem;
  height: 1rem;
  flex: none;
  color: var(--color-text-muted);
}

.search-input {
  flex: 1;
  min-width: 0;
  height: 2.25rem;
  background: none;
  border: none;
  color: var(--color-text);
}

.search--large .search-input {
  height: 3rem;
  font-size: var(--text-md);
}

.search-input::placeholder {
  color: var(--color-text-muted);
}

.search-input:focus {
  outline: none;
}

.search-input::-webkit-search-cancel-button {
  -webkit-appearance: none;
}
</style>
