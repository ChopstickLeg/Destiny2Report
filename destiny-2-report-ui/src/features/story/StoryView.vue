<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import { RouterLink, useRoute, useRouter } from 'vue-router'
import { useQuery } from '@tanstack/vue-query'
import AppButton from '@/components/base/AppButton.vue'
import SkeletonBlock from '@/components/base/SkeletonBlock.vue'
import ErrorState from '@/components/base/ErrorState.vue'
import { bungieUrl } from '@/lib/api/bungie'
import { completeYearsSince, formatDate, parseApiDate } from '@/lib/formatting/dates'
import {
  fetchReport,
  fetchStoryVisualAssets,
  fetchWeapons,
  createStoryShare,
  reportKeys,
  resolveStoryShare,
  type ReportIdentity,
} from '@/lib/api/reports'
import { useSessionStore } from '@/stores/session'
import { buildStorySlides } from './selectors'

const session = useSessionStore()
const route = useRoute()
const router = useRouter()
const isSharedStory = computed(() => route.name === 'shared-story')
const isLocalPreview = computed(
  () => import.meta.env.DEV && route.name === 'story-preview',
)
const hasRouteIdentity = computed(() => isSharedStory.value || isLocalPreview.value)
const shareToken = computed(() => String(route.params.shareToken ?? ''))

const storyShareQuery = useQuery({
  queryKey: computed(() => reportKeys.storyShare(shareToken.value)),
  queryFn: ({ signal }) => resolveStoryShare(shareToken.value, signal),
  enabled: isSharedStory,
  retry: false,
})

const identity = computed<ReportIdentity | null>(() => {
  if (isSharedStory.value) {
    return storyShareQuery.data.value ?? null
  }

  if (isLocalPreview.value) {
    const membershipTypeId = Number(route.params.membershipTypeId)
    const membershipId = String(route.params.membershipId ?? '')
    if (Number.isInteger(membershipTypeId) && membershipTypeId > 0 && /^\d+$/.test(membershipId)) {
      return { membershipTypeId, membershipId }
    }
    return null
  }

  const membership = session.activeMembership
  if (!membership) return null
  return {
    membershipTypeId: membership.membershipType,
    membershipId: membership.membershipId,
  }
})

const reportQuery = useQuery({
  queryKey: computed(() =>
    identity.value ? reportKeys.report(identity.value) : (['report', 'none'] as const),
  ),
  queryFn: ({ signal }) => fetchReport(identity.value!, signal),
  enabled: computed(() => identity.value !== null),
  staleTime: 5 * 60_000,
})

const weaponsQuery = useQuery({
  queryKey: computed(() =>
    identity.value
      ? reportKeys.weapons(identity.value, 'PvE')
      : (['report', 'none', 'weapons'] as const),
  ),
  queryFn: ({ signal }) => fetchWeapons(identity.value!, 'PvE', signal),
  enabled: computed(() => identity.value !== null),
  staleTime: 5 * 60_000,
  retry: false,
})

const storyAssetsQuery = useQuery({
  queryKey: reportKeys.storyAssets(),
  queryFn: ({ signal }) => fetchStoryVisualAssets(signal),
  staleTime: 24 * 60 * 60_000,
  retry: false,
})

const report = computed(() => reportQuery.data.value ?? null)
const reportReady = computed(
  () =>
    report.value !== null &&
    (report.value.crawlState === 'completed' || report.value.lastCrawledAtUtc !== null),
)
const slides = computed(() =>
  reportReady.value && report.value
    ? buildStorySlides(
        report.value,
        weaponsQuery.data.value ?? null,
        storyAssetsQuery.data.value ?? null,
      )
    : [],
)
const coverEmblem = computed(() => bungieUrl(report.value?.mostUsedEmblems[0]?.backgroundUrl))
const firstActivityDate = computed(() => parseApiDate(report.value?.firstActivityAtUtc))
const firstActivityLabel = computed(() =>
  firstActivityDate.value ? formatDate(firstActivityDate.value) : null,
)
const yearsPlayed = computed(() => {
  const firstActivity = firstActivityDate.value
  if (!firstActivity) return null
  return completeYearsSince(firstActivity)
})
const yearsPlayedLabel = computed(() => {
  if (yearsPlayed.value === null) return null
  if (yearsPlayed.value < 1) return 'Your first year in the making'
  return `${yearsPlayed.value} ${yearsPlayed.value === 1 ? 'year' : 'years'} in the making`
})

const reportRoute = computed(() =>
  identity.value
    ? {
        name: 'report-overview' as const,
        params: {
          membershipTypeId: identity.value.membershipTypeId,
          membershipId: identity.value.membershipId,
        },
      }
    : null,
)

const resolving = computed(
  () =>
    !hasRouteIdentity.value &&
    (session.status === 'unknown' || session.status === 'resolving'),
)

// Cover + selected highlights + closing reflection.
const currentIndex = ref(0)
const totalPanels = computed(() => slides.value.length + 2)
const isCover = computed(() => currentIndex.value === 0)
const isClosing = computed(() => currentIndex.value === totalPanels.value - 1)
const currentSlide = computed(() =>
  currentIndex.value > 0 && !isClosing.value ? slides.value[currentIndex.value - 1] : null,
)
const pantheonGroups = computed(() => {
  const items = currentSlide.value?.layout === 'pantheon-gallery' ? currentSlide.value.items ?? [] : []
  const groups = ['Pantheon 1.0', 'Pantheon 2.0']
    .map((label) => ({ label, items: items.filter((item) => item.group === label) }))
    .filter((group) => group.items.length > 0)

  return groups.length > 1 ? groups : [{ label: '', items }]
})
const storyPanel = ref<HTMLElement | null>(null)
const pointerStart = ref<number | null>(null)

function panelLabel(index: number): string {
  if (index === 0) return 'Introduction'
  if (index === totalPanels.value - 1) return 'Closing reflection'
  return slides.value[index - 1]?.eyebrow ?? `Story card ${index}`
}

function goTo(index: number, moveFocus = true) {
  currentIndex.value = Math.max(0, Math.min(index, totalPanels.value - 1))
  if (moveFocus) void nextTick(() => storyPanel.value?.focus())
}

function next() {
  goTo(currentIndex.value + 1)
}

function previous() {
  goTo(currentIndex.value - 1)
}

function onKeydown(event: KeyboardEvent) {
  const target = event.target as HTMLElement | null
  const isControl = target?.closest('button, a, input, textarea, select')
  if (event.key === 'ArrowRight' || event.key === 'PageDown' || (event.key === ' ' && !isControl)) {
    event.preventDefault()
    next()
  } else if (event.key === 'ArrowLeft' || event.key === 'PageUp') {
    event.preventDefault()
    previous()
  } else if (event.key === 'Home') {
    event.preventDefault()
    goTo(0)
  } else if (event.key === 'End') {
    event.preventDefault()
    goTo(totalPanels.value - 1)
  }
}

function onPointerDown(event: PointerEvent) {
  pointerStart.value = event.clientX
}

function onPointerUp(event: PointerEvent) {
  if (pointerStart.value === null) return
  const distance = event.clientX - pointerStart.value
  pointerStart.value = null
  if (Math.abs(distance) < 60) return
  if (distance < 0) next()
  else previous()
}

function hideBrokenImage(event: Event) {
  ;(event.currentTarget as HTMLImageElement).hidden = true
}

const shared = ref(false)

async function shareStory() {
  if (!report.value || !identity.value) return

  try {
    const token = isSharedStory.value
      ? shareToken.value
      : (await createStoryShare(identity.value)).token
    const shareLocation = router.resolve({
      name: 'shared-story',
      params: { shareToken: token },
    })
    const url = new URL(shareLocation.href, window.location.origin).href
    const shareData = {
      title: `${report.value.displayName}'s Destiny 2 story`,
      text: `Look back at ${report.value.displayName}'s Destiny 2 story on D2Report.`,
      url,
    }

    if (navigator.share) await navigator.share(shareData)
    else await navigator.clipboard.writeText(url)
    shared.value = true
    setTimeout(() => (shared.value = false), 2_500)
  } catch {
    // Dismissed share sheets and unavailable clipboards leave the actions usable.
  }
}

function signIn() {
  session.beginSignIn('/me/story')
}

onMounted(() => window.addEventListener('keydown', onKeydown))
onBeforeUnmount(() => window.removeEventListener('keydown', onKeydown))
</script>

<template>
  <div class="story">
    <div
      v-if="
        resolving ||
        (isSharedStory && storyShareQuery.isPending.value) ||
        ((session.isSignedIn || hasRouteIdentity) &&
          (reportQuery.isPending.value || weaponsQuery.isPending.value))
      "
      class="container story-loading"
    >
      <SkeletonBlock height="3rem" width="20rem" />
      <SkeletonBlock height="28rem" radius="var(--radius-md)" />
    </div>

    <div v-else-if="!session.isSignedIn && !hasRouteIdentity" class="container story-gate">
      <h1 class="gate-title display">Your Story</h1>
      <p class="gate-copy">
        The clears, people, and strange little details that made your Destiny 2 history yours, told
        one moment at a time. Sign in with Bungie to begin.
      </p>
      <AppButton v-if="session.signInAvailable" variant="primary" @click="signIn">
        Sign in with Bungie
      </AppButton>
    </div>

    <div v-else-if="isSharedStory && storyShareQuery.isError.value" class="container story-gate">
      <ErrorState
        :error="storyShareQuery.error.value"
        context="This story link is invalid or no longer available"
        @retry="storyShareQuery.refetch()"
      />
    </div>

    <div v-else-if="!identity" class="container story-gate">
      <h1 class="gate-title display">Your Story</h1>
      <p class="gate-copy">No Destiny membership could be resolved for your Bungie account.</p>
    </div>

    <div v-else-if="reportQuery.isError.value" class="container story-gate">
      <ErrorState
        :error="reportQuery.error.value"
        context="Couldn't load your report"
        @retry="reportQuery.refetch()"
      />
    </div>

    <div v-else-if="!reportReady" class="container story-gate">
      <h1 class="gate-title display">Your Story needs a report first</h1>
      <p class="gate-copy">
        Your story is built from your generated report. Create it once, then come back. It only takes
        a few minutes.
      </p>
      <AppButton v-if="reportRoute" variant="primary" :to="reportRoute">
        Generate my report
      </AppButton>
    </div>

    <div v-else-if="report" class="story-experience container">
      <div class="story-topbar">
        <p class="story-brand">
          D2Report
          <span>/ {{ isLocalPreview ? 'Local preview' : isSharedStory ? 'Guardian Story' : 'Your Story' }}</span>
        </p>
        <RouterLink v-if="reportRoute" class="story-exit" :to="reportRoute">
          Exit to report
        </RouterLink>
      </div>

      <div class="story-progress" role="tablist" aria-label="Story progress">
        <button
          v-for="(_, index) in totalPanels"
          :key="index"
          class="progress-step"
          :class="{ 'progress-step--complete': index <= currentIndex }"
          role="tab"
          type="button"
          :aria-selected="index === currentIndex"
          :aria-label="`${index + 1} of ${totalPanels}: ${panelLabel(index)}`"
          @click="goTo(index, false)"
        >
          <span />
        </button>
      </div>

      <div
        ref="storyPanel"
        class="story-stage"
        :class="{ 'story-stage--cover': isCover, 'story-stage--closing': isClosing }"
        tabindex="-1"
        aria-live="polite"
        @pointerdown="onPointerDown"
        @pointerup="onPointerUp"
      >
        <Transition name="story-card" mode="out-in">
          <section v-if="isCover" key="cover" class="panel panel--cover">
            <img
              v-if="coverEmblem"
              class="cover-art"
              :src="coverEmblem"
              alt=""
              @error="hideBrokenImage"
            />
            <div class="cover-scrim" aria-hidden="true" />
            <div class="cover-content">
              <p class="panel-eyebrow">A reflection on your time in Destiny 2</p>
              <h1 class="cover-name display">
                {{ report.displayName
                }}<span class="cover-code">#{{ String(report.displayCode).padStart(4, '0') }}</span>
              </h1>
              <p v-if="firstActivityLabel" class="cover-history">
                <span>Your journey began</span>
                <strong class="display">{{ firstActivityLabel }}</strong>
                <span>{{ yearsPlayedLabel }}</span>
              </p>
              <AppButton variant="primary" @click="next">Begin my story</AppButton>
              <p class="interaction-hint">
                Use the controls, arrow keys, or swipe to move through.
              </p>
            </div>
          </section>

          <section
            v-else-if="currentSlide"
            :key="currentSlide.key"
            class="panel panel--highlight"
            :class="[`panel--${currentSlide.layout}`, `panel--tone-${currentSlide.tone}`]"
          >
            <template v-if="currentSlide.layout === 'achievement-list'">
              <div class="layout-copy">
                <p class="panel-eyebrow">{{ currentSlide.eyebrow }}</p>
                <h2 class="highlight-title display">{{ currentSlide.title }}</h2>
                <p class="highlight-value display">{{ currentSlide.value }}</p>
                <p class="highlight-body">{{ currentSlide.body }}</p>
                <p v-if="currentSlide.detail" class="highlight-detail">{{ currentSlide.detail }}</p>
              </div>
              <div class="achievement-symbol">
                <img
                  v-if="bungieUrl(currentSlide.iconUrl)"
                  :src="bungieUrl(currentSlide.iconUrl)!"
                  alt=""
                  @error="hideBrokenImage"
                />
              </div>
            </template>

            <template v-else-if="currentSlide.layout === 'contest-gallery'">
              <header class="contest-heading">
                <p class="panel-eyebrow">{{ currentSlide.eyebrow }}</p>
                <h2 class="highlight-title display">{{ currentSlide.title }}</h2>
                <p class="highlight-value display">{{ currentSlide.value }}</p>
                <p class="highlight-body">{{ currentSlide.body }}</p>
                <p class="contest-note">{{ currentSlide.detail }}</p>
              </header>
              <ol
                class="contest-emblems"
                :class="{
                  'contest-emblems--single': currentSlide.items?.length === 1,
                  'contest-emblems--dense': (currentSlide.items?.length ?? 0) > 4,
                  'contest-emblems--crowded': (currentSlide.items?.length ?? 0) > 8,
                }"
              >
                <li v-for="item in currentSlide.items" :key="item.label">
                  <figure>
                    <img
                      v-if="bungieUrl(item.imageUrl)"
                      :src="bungieUrl(item.imageUrl)!"
                      :alt="item.value"
                      @error="hideBrokenImage"
                    />
                  </figure>
                  <div>
                    <strong>{{ item.label }}</strong>
                    <span class="emblem-name">{{ item.value }}</span>
                  </div>
                </li>
              </ol>
            </template>

            <template v-else-if="currentSlide.layout === 'pantheon-gallery'">
              <header class="pantheon-heading">
                <div>
                  <p class="panel-eyebrow">{{ currentSlide.eyebrow }}</p>
                  <h2 class="highlight-title display">{{ currentSlide.title }}</h2>
                  <p class="highlight-body">{{ currentSlide.body }}</p>
                </div>
                <div class="pantheon-total">
                  <strong class="display">{{ currentSlide.value.split(' ')[0] }}</strong>
                  <span>{{ currentSlide.value.split(' ').slice(1).join(' ') }}</span>
                </div>
              </header>
              <div
                class="pantheon-groups"
                :class="{ 'pantheon-groups--split': pantheonGroups.length > 1 }"
              >
                <section v-for="group in pantheonGroups" :key="group.label || 'pantheon'">
                  <p v-if="group.label" class="pantheon-era">
                    <strong>{{ group.label }}</strong>
                    <span>{{ group.items.length }} completed</span>
                  </p>
                  <ol class="pantheon-emblems">
                    <li v-for="item in group.items" :key="item.label">
                      <figure>
                        <img
                          v-if="bungieUrl(item.imageUrl)"
                          :src="bungieUrl(item.imageUrl)!"
                          :alt="item.value"
                          @error="hideBrokenImage"
                        />
                      </figure>
                      <div>
                        <strong>{{ item.label.replace(/^(?:The )?Pantheon: /, '') }}</strong>
                        <span>{{ item.value }}</span>
                      </div>
                    </li>
                  </ol>
                </section>
              </div>
              <p class="pantheon-note">{{ currentSlide.detail }}</p>
            </template>

            <template v-else-if="currentSlide.layout === 'seal-gallery'">
              <div class="seal-heading">
                <p class="panel-eyebrow">{{ currentSlide.eyebrow }}</p>
                <h2 class="highlight-title display">{{ currentSlide.title }}</h2>
                <p class="highlight-value display">{{ currentSlide.value }}</p>
                <p class="highlight-body">{{ currentSlide.body }}</p>
              </div>
              <div class="seal-grid">
                <figure v-for="image in currentSlide.imageUrls" :key="image.url">
                  <img :src="bungieUrl(image.url)!" :alt="image.alt" @error="hideBrokenImage" />
                </figure>
              </div>
            </template>

            <template v-else-if="currentSlide.layout === 'split-tally'">
              <header class="tally-heading">
                <p class="panel-eyebrow">{{ currentSlide.eyebrow }}</p>
                <h2 class="highlight-title display">{{ currentSlide.title }}</h2>
                <p class="highlight-body">{{ currentSlide.detail }}</p>
              </header>
              <div class="tally-grid">
                <article
                  v-for="stat in currentSlide.stats"
                  :key="stat.label"
                  :style="{ '--tally-share': `${(stat.share ?? 0) * 100}%` }"
                >
                  <div class="tally-ring">
                    <strong class="display">{{ stat.value }}</strong>
                    <span>{{ stat.label }}</span>
                  </div>
                  <p class="tally-share">{{ Math.round((stat.share ?? 0) * 100) }}% of clears</p>
                </article>
              </div>
              <p class="tally-total display">{{ currentSlide.value }}</p>
            </template>

            <template v-else-if="currentSlide.layout === 'sherpa-spotlight'">
              <div class="sherpa-number">
                <img v-if="bungieUrl(currentSlide.iconUrl)" :src="bungieUrl(currentSlide.iconUrl)!" alt="" @error="hideBrokenImage" />
                <strong class="display">{{ currentSlide.value.split(' ')[0] }}</strong>
                <span>Guardians guided</span>
              </div>
              <div class="sherpa-copy">
                <p class="panel-eyebrow">{{ currentSlide.eyebrow }}</p>
                <h2 class="highlight-title display">{{ currentSlide.title }}</h2>
                <p class="highlight-body">{{ currentSlide.body }}</p>
                <ol class="mini-ranking">
                  <li v-for="item in currentSlide.items" :key="item.label"><span>{{ item.label }}</span><strong>{{ item.value }}</strong></li>
                </ol>
              </div>
            </template>

            <template v-else-if="currentSlide.layout === 'class-breakdown'">
              <header class="breakdown-heading">
                <p class="panel-eyebrow">{{ currentSlide.eyebrow }}</p>
                <p class="time-total display">{{ currentSlide.value }}</p>
                <h2 class="highlight-title display">{{ currentSlide.title }}</h2>
              </header>
              <div class="class-bars">
                <article v-for="stat in currentSlide.stats" :key="stat.label">
                  <img v-if="bungieUrl(stat.iconUrl)" :src="bungieUrl(stat.iconUrl)!" alt="" @error="hideBrokenImage" />
                  <div><p><strong>{{ stat.label }}</strong><span>{{ stat.value }}</span></p><i><b :style="{ width: `${(stat.share ?? 0) * 100}%`, backgroundColor: stat.color }" /></i></div>
                </article>
                <p class="breakdown-note">{{ currentSlide.detail }}</p>
              </div>
            </template>

            <template v-else-if="currentSlide.layout === 'weapon-leaderboard'">
              <header class="leaderboard-heading">
                <p class="panel-eyebrow">{{ currentSlide.eyebrow }}</p>
                <h2 class="highlight-title display">{{ currentSlide.title }}</h2>
                <p>{{ currentSlide.detail }}</p>
              </header>
              <ol class="weapon-ranking">
                <li v-for="(item, index) in currentSlide.items" :key="item.label">
                  <span class="rank">{{ String(index + 1).padStart(2, '0') }}</span>
                  <img v-if="bungieUrl(item.imageUrl)" :src="bungieUrl(item.imageUrl)!" :alt="item.label" @error="hideBrokenImage" />
                  <strong>{{ item.label }}</strong><span class="kills tnum">{{ item.value }} kills</span>
                </li>
              </ol>
            </template>

            <template v-else-if="currentSlide.layout === 'teammate-profile'">
              <div class="teammate-emblem">
                <img v-if="bungieUrl(currentSlide.imageUrl)" :src="bungieUrl(currentSlide.imageUrl)!" :alt="currentSlide.imageAlt ?? ''" @error="hideBrokenImage" />
              </div>
              <div class="teammate-copy">
                <p class="panel-eyebrow">{{ currentSlide.eyebrow }}</p>
                <h2 class="highlight-title display">{{ currentSlide.title }}</h2>
                <p class="highlight-value display">{{ currentSlide.value }}</p>
                <p class="highlight-body">{{ currentSlide.body }}</p>
              </div>
            </template>

            <template v-else-if="currentSlide.layout === 'match-scoreboard'">
              <header class="score-heading">
                <img v-if="bungieUrl(currentSlide.iconUrl)" :src="bungieUrl(currentSlide.iconUrl)!" alt="" @error="hideBrokenImage" />
                <div><p class="panel-eyebrow">{{ currentSlide.eyebrow }}</p><h2 class="highlight-title display">{{ currentSlide.title }}</h2></div>
              </header>
              <div class="score-grid">
                <article v-for="stat in currentSlide.stats" :key="stat.label"><span>{{ stat.label }}</span><strong class="display">{{ stat.value }}</strong></article>
              </div>
              <p class="score-note">{{ currentSlide.detail }}</p>
            </template>

            <template v-else-if="currentSlide.layout === 'emblem-banner'">
              <img v-if="bungieUrl(currentSlide.imageUrl)" class="emblem-backdrop" :src="bungieUrl(currentSlide.imageUrl)!" :alt="currentSlide.imageAlt ?? ''" @error="hideBrokenImage" />
              <div class="emblem-scrim" />
              <div class="emblem-copy"><p class="panel-eyebrow">{{ currentSlide.eyebrow }}</p><h2 class="highlight-title display">{{ currentSlide.value }}</h2><p>{{ currentSlide.body }}</p></div>
            </template>

            <template v-else>
              <div class="personality-copy">
                <img v-if="bungieUrl(currentSlide.iconUrl)" :src="bungieUrl(currentSlide.iconUrl)!" alt="" @error="hideBrokenImage" />
                <p class="panel-eyebrow">{{ currentSlide.eyebrow }}</p>
                <p class="personality-value display">{{ currentSlide.value }}</p>
                <h2 class="highlight-title display">{{ currentSlide.title }}</h2>
                <p class="highlight-body">{{ currentSlide.body }}</p>
              </div>
            </template>
          </section>

          <section v-else key="closing" class="panel panel--closing">
            <img
              v-if="coverEmblem"
              class="closing-art"
              :src="coverEmblem"
              alt=""
              @error="hideBrokenImage"
            />
            <div class="closing-scrim" aria-hidden="true" />
            <p class="panel-eyebrow">For now</p>
            <h2 class="closing-title display">That’s the Guardian you became.</h2>
            <p class="closing-copy">
              The rare clears matter. So do the people who stayed, the places you returned to, and
              the small rituals between battles. Your story is still being written.
            </p>
            <div class="closing-actions">
              <AppButton variant="primary" @click="shareStory">
                {{ shared ? 'Story link copied' : 'Share my story' }}
              </AppButton>
              <AppButton v-if="reportRoute" variant="secondary" :to="reportRoute">
                Explore the full report
              </AppButton>
              <AppButton variant="ghost" @click="goTo(0)">Watch again</AppButton>
            </div>
          </section>
        </Transition>
      </div>

      <nav class="story-controls" aria-label="Story controls">
        <button class="story-control" type="button" :disabled="isCover" @click="previous">
          <svg viewBox="0 0 20 20" aria-hidden="true"><path d="m12.5 4-6 6 6 6" /></svg>
          Previous
        </button>
        <p class="story-count tnum">{{ currentIndex + 1 }} / {{ totalPanels }}</p>
        <button class="story-control" type="button" :disabled="isClosing" @click="next">
          Next
          <svg viewBox="0 0 20 20" aria-hidden="true"><path d="m7.5 4 6 6-6 6" /></svg>
        </button>
      </nav>
    </div>
  </div>
</template>

<style scoped>
.story-loading {
  padding-top: var(--space-7);
  display: flex;
  flex-direction: column;
  gap: var(--space-4);
}

.story-gate {
  padding-top: var(--space-8);
  max-width: 36rem;
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: var(--space-4);
}

.gate-title {
  font-size: var(--text-2xl);
}

.gate-copy {
  color: var(--color-text-secondary);
}

.story-experience {
  --story-accent: var(--color-accent);
  --story-accent-soft: rgb(215 172 75 / 0.13);
  padding-top: var(--space-4);
}

.story-topbar,
.story-controls {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.story-brand {
  font-size: var(--text-sm);
  font-weight: 650;
}

.story-brand span,
.story-exit {
  color: var(--color-text-muted);
  font-weight: 400;
}

.story-exit {
  font-size: var(--text-xs);
}

.story-progress {
  display: flex;
  gap: var(--space-1);
  margin: var(--space-3) 0;
}

.progress-step {
  flex: 1;
  height: 0.75rem;
  display: flex;
  align-items: center;
}

.progress-step span {
  width: 100%;
  height: 2px;
  background: var(--color-border-strong);
  transition:
    background-color var(--transition-medium),
    height var(--transition-fast);
}

.progress-step:hover span,
.progress-step[aria-selected='true'] span {
  height: 4px;
}

.progress-step--complete span {
  background: var(--story-accent);
}

.story-stage {
  min-height: min(45rem, calc(100dvh - 12rem));
  position: relative;
  overflow: hidden;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-md);
  color: var(--color-text);
  background: var(--color-surface);
  box-shadow: var(--shadow-raised);
  touch-action: pan-y;
}

.panel--tone-gold {
  --story-accent: #e2b84f;
  --story-accent-soft: rgb(226 184 79 / 0.13);
}

.panel--tone-solar {
  --story-accent: #e06c3b;
  --story-accent-soft: rgb(224 108 59 / 0.13);
}

.panel--tone-arc {
  --story-accent: #65b5df;
  --story-accent-soft: rgb(101 181 223 / 0.13);
}

.panel--tone-void {
  --story-accent: #a78bdb;
  --story-accent-soft: rgb(167 139 219 / 0.13);
}

.panel--tone-neutral {
  --story-accent: var(--color-text-secondary);
  --story-accent-soft: rgb(192 181 165 / 0.1);
}

.panel {
  min-height: inherit;
}

.panel--cover {
  position: relative;
  display: flex;
  align-items: flex-end;
  isolation: isolate;
}

.cover-art {
  position: absolute;
  inset: 0;
  z-index: -2;
  width: 100%;
  height: 100%;
  object-fit: cover;
}

.cover-art[hidden],
.highlight-art img[hidden],
.highlight-icon img[hidden],
.closing-art[hidden] {
  display: none;
}

.cover-scrim {
  position: absolute;
  inset: 0;
  z-index: -1;
  background: linear-gradient(
    90deg,
    rgb(8 6 7 / 0.95) 10%,
    rgb(8 6 7 / 0.58) 62%,
    rgb(8 6 7 / 0.25)
  );
}

.cover-content {
  width: min(42rem, 100%);
  padding: clamp(var(--space-5), 7vw, var(--space-8));
}

.panel-eyebrow {
  color: var(--story-accent);
  font-size: var(--text-xs);
  font-weight: 650;
  letter-spacing: 0.1em;
  text-transform: uppercase;
}

.cover-name {
  margin: var(--space-2) 0 var(--space-3);
  font-size: clamp(2.5rem, 7vw, 4.75rem);
  line-height: 0.95;
  overflow-wrap: anywhere;
}

.cover-code {
  display: block;
  margin-top: var(--space-2);
  color: var(--color-text-secondary);
  font-size: 0.35em;
  font-weight: 400;
}

.cover-history {
  display: grid;
  gap: 0.2rem;
  max-width: 33rem;
  margin-bottom: var(--space-5);
  color: var(--color-text-secondary);
  font-size: var(--text-sm);
  letter-spacing: 0.035em;
}

.cover-history strong {
  color: var(--story-accent);
  font-size: clamp(1.55rem, 3.5vw, 2.35rem);
  font-weight: 600;
  letter-spacing: -0.025em;
  line-height: 1.05;
}

.interaction-hint {
  margin-top: var(--space-3);
  color: var(--color-text-muted);
  font-size: var(--text-xs);
}

.panel--highlight {
  padding: clamp(var(--space-5), 6vw, var(--space-8));
}

.highlight-title {
  max-width: 38rem;
  margin-top: var(--space-2);
  font-size: clamp(2rem, 5vw, 4.25rem);
  line-height: 1;
}

.highlight-value {
  margin-top: var(--space-5);
  color: var(--story-accent);
  font-size: clamp(1.75rem, 4vw, 3.25rem);
  font-weight: 700;
  line-height: 1.05;
  overflow-wrap: anywhere;
}

.highlight-body {
  max-width: 36rem;
  margin-top: var(--space-3);
  color: var(--color-text-secondary);
  font-size: var(--text-md);
}

.highlight-detail {
  max-width: 36rem;
  margin-top: var(--space-4);
  padding-top: var(--space-4);
  border-top: 1px solid var(--color-border);
  color: var(--color-text-muted);
  font-size: var(--text-sm);
}

.layout-copy {
  align-self: center;
}

.panel--achievement-list {
  display: grid;
  grid-template-columns: minmax(0, 1.25fr) minmax(15rem, 0.75fr);
  gap: var(--space-7);
}

.achievement-symbol {
  display: grid;
  place-items: center;
  border-radius: 50%;
  background: radial-gradient(circle, var(--story-accent-soft), transparent 68%);
}

.achievement-symbol img {
  width: min(70%, 15rem);
  filter: drop-shadow(0 1rem 2rem rgb(0 0 0 / 0.45));
}

.panel--contest-gallery {
  display: grid;
  grid-template-columns: minmax(15rem, 0.72fr) minmax(0, 1.28fr);
  gap: clamp(var(--space-5), 5vw, var(--space-8));
  align-items: center;
  background:
    radial-gradient(circle at 88% 12%, rgb(255 255 255 / 0.04), transparent 38%),
    var(--color-surface);
}

.contest-heading {
  position: relative;
  z-index: 1;
}

.contest-heading .highlight-title {
  font-size: clamp(2.25rem, 4.5vw, 4rem);
}

.contest-heading .highlight-value {
  font-size: clamp(1.5rem, 3vw, 2.5rem);
}

.contest-note,
.leaderboard-heading > p:last-child,
.score-note,
.breakdown-note {
  display: block;
  margin-top: var(--space-3);
  color: var(--color-text-muted);
  font-size: var(--text-sm);
}

.contest-emblems {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: var(--space-3);
  align-content: center;
  list-style: none;
}

.contest-emblems li {
  min-width: 0;
  display: grid;
  grid-template-columns: 4.5rem minmax(0, 1fr);
  gap: var(--space-3);
  align-items: center;
  padding: var(--space-3);
  border: 1px solid rgb(255 255 255 / 0.1);
  background: rgb(12 12 14 / 0.72);
  box-shadow: 0 0.75rem 2rem rgb(0 0 0 / 0.22);
}

.contest-emblems figure {
  display: grid;
  place-items: center;
  aspect-ratio: 1;
  overflow: hidden;
  border: 1px solid rgb(255 255 255 / 0.12);
  background: #0c0d10;
}

.contest-emblems img {
  width: 100%;
  height: 100%;
  object-fit: cover;
}

.contest-emblems strong,
.contest-emblems span {
  display: block;
}

.contest-emblems strong {
  line-height: 1.1;
}

.contest-emblems span {
  margin-top: 0.35rem;
  color: var(--color-text-muted);
  font-size: var(--text-xs);
}

.contest-emblems--single {
  grid-template-columns: minmax(17rem, 24rem);
  justify-content: center;
}

.contest-emblems--single li {
  grid-template-columns: 9rem minmax(0, 1fr);
  padding: var(--space-4);
}

.contest-emblems--dense {
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: var(--space-2);
}

.contest-emblems--dense li {
  grid-template-columns: 3.5rem minmax(0, 1fr);
  gap: var(--space-2);
  padding: var(--space-2);
  font-size: var(--text-sm);
}

.contest-emblems--crowded {
  grid-template-columns: repeat(4, minmax(0, 1fr));
}

.contest-emblems--crowded li {
  grid-template-columns: 1fr;
  align-content: start;
}

.contest-emblems--crowded figure {
  width: min(100%, 4.25rem);
}

.contest-emblems--crowded strong {
  font-size: var(--text-xs);
}

.panel--pantheon-gallery {
  display: grid;
  grid-template-rows: auto 1fr auto;
  gap: var(--space-5);
  align-content: center;
  background:
    linear-gradient(135deg, rgb(226 184 79 / 0.05), transparent 42%),
    var(--color-surface);
}

.pantheon-heading {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  gap: var(--space-6);
  align-items: end;
  padding-bottom: var(--space-4);
  border-bottom: 1px solid var(--color-border);
}

.pantheon-heading .highlight-title {
  max-width: 48rem;
  font-size: clamp(2rem, 4vw, 3.75rem);
}

.pantheon-total {
  min-width: 9rem;
  padding-left: var(--space-5);
  border-left: 1px solid var(--color-border);
  text-align: right;
}

.pantheon-total strong,
.pantheon-total span {
  display: block;
}

.pantheon-total strong {
  color: var(--story-accent);
  font-size: clamp(3.5rem, 7vw, 6rem);
  line-height: 0.82;
}

.pantheon-total span {
  max-width: 9rem;
  margin-top: var(--space-2);
  color: var(--color-text-secondary);
  font-size: var(--text-xs);
  text-transform: uppercase;
  letter-spacing: 0.08em;
}

.pantheon-emblems {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(10.5rem, 1fr));
  gap: var(--space-3);
  align-content: center;
  list-style: none;
}

.pantheon-groups {
  display: grid;
  gap: var(--space-4);
  align-content: center;
}

.pantheon-groups--split {
  grid-template-columns: minmax(0, 4fr) minmax(13rem, 3fr);
  align-items: start;
}

.pantheon-era {
  display: flex;
  justify-content: space-between;
  gap: var(--space-3);
  margin-bottom: var(--space-2);
  padding-bottom: var(--space-2);
  border-bottom: 1px solid rgb(226 184 79 / 0.35);
  color: var(--story-accent);
  font-size: var(--text-xs);
  text-transform: uppercase;
  letter-spacing: 0.08em;
}

.pantheon-era span {
  color: var(--color-text-muted);
  letter-spacing: 0.04em;
}

.pantheon-emblems li {
  min-width: 0;
  display: grid;
  grid-template-rows: auto 1fr;
  gap: var(--space-3);
  padding: var(--space-3);
  border-top: 2px solid rgb(226 184 79 / 0.55);
  background: rgb(12 12 14 / 0.76);
  box-shadow: 0 0.75rem 2rem rgb(0 0 0 / 0.2);
}

.pantheon-emblems figure {
  width: 5.25rem;
  aspect-ratio: 1;
  overflow: hidden;
  border: 1px solid rgb(255 255 255 / 0.12);
  background: #0c0d10;
}

.pantheon-emblems img {
  width: 100%;
  height: 100%;
  object-fit: cover;
}

.pantheon-emblems strong,
.pantheon-emblems span {
  display: block;
}

.pantheon-emblems strong {
  font-size: var(--text-sm);
  line-height: 1.15;
}

.pantheon-emblems span {
  margin-top: 0.35rem;
  color: var(--color-text-muted);
  font-size: var(--text-xs);
}

.pantheon-note {
  color: var(--color-text-muted);
  font-size: var(--text-xs);
  text-align: right;
}

.panel--seal-gallery {
  display: grid;
  grid-template-columns: 0.9fr 1.1fr;
  gap: var(--space-7);
  align-items: center;
}

.seal-grid {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: var(--space-3);
}

.seal-grid figure {
  display: grid;
  place-items: center;
  aspect-ratio: 1;
  background: var(--story-accent-soft);
  border: 1px solid rgb(255 255 255 / 0.08);
}

.seal-grid img {
  width: 82%;
  filter: drop-shadow(0 0.75rem 1.5rem rgb(0 0 0 / 0.4));
}

.panel--split-tally {
  --story-accent: #e0a62f;
  --story-accent-soft: rgb(224 166 47 / 0.1);
  --endgame-raid: #e05a32;
  --endgame-dungeon: #3f94b5;
  display: grid;
  grid-template-columns: 0.9fr 1.1fr;
  grid-template-rows: 1fr auto;
  gap: var(--space-6) var(--space-8);
  align-items: center;
  background:
    radial-gradient(circle at 82% 28%, rgb(30 107 139 / 0.12), transparent 25rem),
    linear-gradient(125deg, rgb(151 51 25 / 0.1), transparent 46%),
    var(--color-surface);
}

.tally-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: clamp(var(--space-3), 3vw, var(--space-7)); align-items: start; }
.tally-grid article { --tally-color: var(--endgame-raid); display: grid; justify-items: center; gap: var(--space-3); min-width: 0; }
.tally-grid article:nth-child(2) { --tally-color: var(--endgame-dungeon); }
.tally-ring { position: relative; display: grid; place-items: center; align-content: center; width: clamp(8.25rem, 16vw, 12rem); max-width: 100%; aspect-ratio: 1; padding: var(--space-4); overflow: hidden; border-radius: 50%; background: conic-gradient(from 0deg, var(--tally-color) 0 var(--tally-share), rgb(242 236 225 / 0.08) var(--tally-share) 100%); isolation: isolate; filter: drop-shadow(0 1rem 1.5rem rgb(0 0 0 / 0.22)); }
.tally-grid article:nth-child(2) .tally-ring { background: conic-gradient(from 0deg, rgb(242 236 225 / 0.08) 0 calc(100% - var(--tally-share)), var(--tally-color) calc(100% - var(--tally-share)) 100%); }
.tally-ring::after { content: ''; position: absolute; inset: clamp(0.45rem, 1vw, 0.7rem); z-index: -1; border: 1px solid rgb(255 255 255 / 0.05); border-radius: inherit; background: var(--color-surface); }
.tally-ring strong { display: block; color: var(--color-text); font-size: clamp(2.35rem, 5vw, 4.5rem); line-height: 0.85; text-align: center; }
.tally-ring span { margin-top: var(--space-2); color: var(--tally-color); font-size: var(--text-xs); font-weight: 650; letter-spacing: 0.09em; text-align: center; text-transform: uppercase; }
.tally-share { color: var(--color-text-muted); font-size: var(--text-xs); font-variant-numeric: tabular-nums; letter-spacing: 0.04em; }
.tally-total { grid-column: 1 / -1; padding-top: var(--space-4); border-top: 1px solid var(--color-border); text-align: right; font-size: clamp(2rem, 5vw, 4rem); }

.panel--sherpa-spotlight,
.panel--teammate-profile {
  display: grid;
  grid-template-columns: 0.8fr 1.2fr;
  gap: var(--space-8);
  align-items: center;
}

.sherpa-number { display: flex; min-height: 24rem; flex-direction: column; justify-content: center; align-items: center; background: var(--story-accent-soft); border-radius: 50% 50% 8% 8%; }
.sherpa-number img { width: min(34%, 7rem); margin-bottom: var(--space-3); }
.sherpa-number strong { font-size: clamp(4rem, 9vw, 8rem); line-height: 0.9; color: var(--story-accent); }
.sherpa-number span { margin-top: var(--space-3); color: var(--color-text-secondary); text-transform: uppercase; letter-spacing: 0.08em; }
.mini-ranking { margin-top: var(--space-5); border-top: 1px solid var(--color-border); }
.mini-ranking li { display: flex; justify-content: space-between; gap: var(--space-4); padding: var(--space-2) 0; border-bottom: 1px solid var(--color-border); }
.mini-ranking strong { color: var(--story-accent); }

.panel--class-breakdown { display: grid; grid-template-columns: 0.85fr 1.15fr; gap: var(--space-8); align-items: center; }
.time-total { margin: var(--space-3) 0; font-size: clamp(3rem, 8vw, 7rem); color: var(--story-accent); line-height: 0.9; }
.class-bars article { display: flex; gap: var(--space-3); align-items: center; margin-bottom: var(--space-5); }
.class-bars img { width: 3rem; height: 3rem; object-fit: contain; }
.class-bars article > div { flex: 1; }
.class-bars p { display: flex; justify-content: space-between; margin-bottom: var(--space-2); }
.class-bars p span { color: var(--color-text-secondary); }
.class-bars i { display: block; height: 0.6rem; background: var(--color-border); }
.class-bars b { display: block; height: 100%; background: var(--story-accent); }

.panel--weapon-leaderboard { display: grid; grid-template-columns: 0.8fr 1.2fr; gap: var(--space-7); align-items: center; }
.weapon-ranking { counter-reset: weapons; }
.weapon-ranking li { display: grid; grid-template-columns: 2rem 3.5rem minmax(0, 1fr) auto; gap: var(--space-3); align-items: center; min-height: 4.6rem; padding: var(--space-2); border-bottom: 1px solid var(--color-border); }
.weapon-ranking li:first-child { min-height: 6rem; background: var(--story-accent-soft); border-left: 3px solid var(--story-accent); }
.weapon-ranking img { width: 3.5rem; height: 3.5rem; object-fit: contain; }
.weapon-ranking li:first-child img { width: 4.5rem; height: 4.5rem; margin-left: -0.5rem; }
.weapon-ranking .rank { color: var(--color-text-muted); font-variant-numeric: tabular-nums; }
.weapon-ranking .kills { color: var(--story-accent); }

.teammate-emblem { display: grid; place-items: center; }
.teammate-emblem img {
  width: min(100%, 24rem);
  aspect-ratio: 1;
  object-fit: cover;
  transform: rotate(-3deg);
  box-shadow: 1.25rem 1.25rem 0 var(--story-accent-soft), 0 2rem 4rem rgb(0 0 0 / 0.35);
}

.panel--match-scoreboard { display: grid; grid-template-rows: auto 1fr auto; gap: var(--space-5); }
.score-heading { display: flex; align-items: center; gap: var(--space-5); }
.score-heading img { width: 5rem; height: 5rem; object-fit: contain; }
.score-heading .highlight-title { margin-top: var(--space-1); }
.score-grid { display: grid; grid-template-columns: repeat(3, 1fr); gap: 1px; align-self: center; background: var(--color-border); border: 1px solid var(--color-border); }
.score-grid article { padding: clamp(var(--space-4), 4vw, var(--space-7)); background: var(--color-surface); text-align: center; }
.score-grid span { color: var(--color-text-muted); text-transform: uppercase; letter-spacing: 0.08em; }
.score-grid strong { display: block; margin-top: var(--space-2); font-size: clamp(2.5rem, 6vw, 5.5rem); color: var(--story-accent); }

.panel--emblem-banner { position: relative; display: flex; align-items: flex-end; isolation: isolate; overflow: hidden; }
.emblem-backdrop { position: absolute; inset: 0; z-index: -2; width: 100%; height: 100%; object-fit: cover; }
.emblem-scrim { position: absolute; inset: 0; z-index: -1; background: linear-gradient(0deg, rgb(8 6 7 / 0.96), rgb(8 6 7 / 0.15) 75%); }
.emblem-copy { max-width: 50rem; }
.emblem-copy .highlight-title { font-size: clamp(3rem, 9vw, 7rem); }
.emblem-copy > p:last-child { margin-top: var(--space-3); color: var(--color-text-secondary); font-size: var(--text-md); }

.panel--personality-number { display: grid; place-items: center; text-align: center; }
.personality-copy { max-width: 48rem; }
.personality-copy img { width: 6rem; height: 6rem; object-fit: contain; margin-bottom: var(--space-3); }
.personality-value { font-size: clamp(6rem, 18vw, 13rem); line-height: 0.85; color: var(--story-accent); }
.personality-copy .highlight-title { margin-inline: auto; }
.personality-copy .highlight-body { margin-inline: auto; }

.seal-grid img,
.achievement-symbol img,
.sherpa-number img,
.class-bars img,
.score-heading img,
.personality-copy img {
  object-fit: contain;
  filter: drop-shadow(0 0.75rem 1.5rem rgb(0 0 0 / 0.4));
}

.panel--closing {
  position: relative;
  isolation: isolate;
  overflow: hidden;
  display: flex;
  flex-direction: column;
  justify-content: center;
  align-items: flex-start;
  padding: clamp(var(--space-5), 8vw, 7rem);
}

.closing-art {
  position: absolute;
  inset: 0;
  z-index: -2;
  width: 100%;
  height: 100%;
  object-fit: cover;
}

.closing-scrim {
  position: absolute;
  inset: 0;
  z-index: -1;
  background: linear-gradient(90deg, rgb(8 6 7 / 0.96) 5%, rgb(8 6 7 / 0.75) 58%, rgb(8 6 7 / 0.4));
}

.panel--closing > :not(.closing-art, .closing-scrim) {
  z-index: 1;
}

.closing-title {
  max-width: 48rem;
  margin-top: var(--space-2);
  font-size: clamp(2.5rem, 7vw, 5rem);
  line-height: 0.98;
}

.closing-copy {
  max-width: 42rem;
  margin-top: var(--space-4);
  color: var(--color-text-secondary);
  font-size: var(--text-md);
}

.closing-actions {
  display: flex;
  flex-wrap: wrap;
  gap: var(--space-2);
  margin-top: var(--space-6);
}

.story-controls {
  padding: var(--space-3) 0;
}

.story-control {
  display: inline-flex;
  align-items: center;
  gap: var(--space-2);
  min-height: 2.5rem;
  color: var(--color-text-secondary);
  font-size: var(--text-sm);
}

.story-control:hover:not(:disabled) {
  color: var(--color-text);
}

.story-control:disabled {
  opacity: 0.3;
  cursor: default;
}

.story-control svg {
  width: 1rem;
  height: 1rem;
  fill: none;
  stroke: currentColor;
  stroke-width: 1.5;
}

.story-count {
  color: var(--color-text-muted);
  font-size: var(--text-xs);
}

.story-card-enter-active,
.story-card-leave-active {
  transition:
    opacity 180ms ease,
    transform 180ms ease;
}

.story-card-enter-from {
  opacity: 0;
  transform: translateX(1rem);
}

.story-card-leave-to {
  opacity: 0;
  transform: translateX(-1rem);
}

@media (max-width: 720px) {
  .story-stage {
    min-height: 38rem;
  }

  .panel--highlight {
    padding: var(--space-5);
    grid-template-columns: 1fr;
    grid-template-rows: auto;
  }

  .panel--achievement-list,
  .panel--sherpa-spotlight,
  .panel--class-breakdown,
  .panel--weapon-leaderboard,
  .panel--teammate-profile,
  .panel--seal-gallery,
  .panel--split-tally {
    gap: var(--space-5);
  }

  .panel--achievement-list .layout-copy,
  .sherpa-copy,
  .teammate-copy,
  .seal-heading,
  .tally-heading,
  .breakdown-heading,
  .leaderboard-heading {
    order: 1;
  }

  .achievement-symbol,
  .sherpa-number,
  .teammate-emblem,
  .seal-grid,
  .tally-grid,
  .class-bars,
  .weapon-ranking {
    order: 2;
  }

  .achievement-symbol { min-height: 10rem; }
  .achievement-symbol img { width: min(35%, 7rem); }
  .sherpa-number { min-height: 15rem; }

  .panel--contest-gallery {
    align-content: center;
    gap: var(--space-4);
  }

  .contest-heading .highlight-title { font-size: clamp(1.75rem, 8vw, 2.5rem); }
  .contest-heading .highlight-value { margin-top: var(--space-3); }
  .contest-heading .highlight-body { display: none; }
  .contest-note { margin-top: var(--space-2); }
  .contest-emblems { grid-template-columns: repeat(2, minmax(0, 1fr)); }
  .contest-emblems--single { grid-template-columns: minmax(0, 22rem); }
  .contest-emblems--single li { grid-template-columns: 6rem minmax(0, 1fr); }
  .contest-emblems--dense { grid-template-columns: repeat(3, minmax(0, 1fr)); }
  .contest-emblems--crowded { grid-template-columns: repeat(4, minmax(0, 1fr)); }
  .contest-emblems--crowded li { padding: 0.35rem; }
  .contest-emblems--crowded .emblem-name { display: none; }

  .panel--pantheon-gallery { gap: var(--space-3); }
  .pantheon-heading { gap: var(--space-3); }
  .pantheon-heading .highlight-title { font-size: clamp(1.65rem, 7.5vw, 2.35rem); }
  .pantheon-heading .highlight-body { display: none; }
  .pantheon-total { min-width: 4rem; padding-left: var(--space-3); }
  .pantheon-total strong { font-size: clamp(3rem, 14vw, 4.5rem); }
  .pantheon-total span { max-width: 5rem; }
  .pantheon-emblems { grid-template-columns: repeat(2, minmax(0, 1fr)); gap: var(--space-2); }
  .pantheon-groups--split { grid-template-columns: 1fr; gap: var(--space-3); }
  .pantheon-era { margin-bottom: var(--space-1); padding-bottom: var(--space-1); }
  .pantheon-emblems li { grid-template-columns: 3.5rem minmax(0, 1fr); grid-template-rows: auto; gap: var(--space-2); padding: var(--space-2); }
  .pantheon-emblems figure { width: 3.5rem; }
  .pantheon-emblems strong { font-size: var(--text-xs); }
  .pantheon-emblems span { font-size: 0.68rem; }
  .pantheon-note { text-align: left; }

  .seal-grid { grid-template-columns: repeat(4, 1fr); }
  .seal-grid figure:nth-child(n + 5) { display: none; }
  .tally-total { order: 3; }

  .panel--class-breakdown .time-total {
    font-size: clamp(3rem, 15vw, 5rem);
  }

  .highlight-title {
    font-size: clamp(1.8rem, 9vw, 3rem);
  }

  .highlight-value {
    margin-top: var(--space-4);
    font-size: clamp(1.5rem, 8vw, 2.5rem);
  }

  .weapon-ranking li {
    grid-template-columns: 1.25rem 2.75rem minmax(0, 1fr) auto;
    gap: var(--space-2);
    min-height: 3.65rem;
  }

  .weapon-ranking li:first-child { min-height: 4.25rem; }
  .weapon-ranking img { width: 2.75rem; height: 2.75rem; }
  .weapon-ranking li:first-child img { width: 3.25rem; height: 3.25rem; margin-left: -0.25rem; }
  .weapon-ranking .kills { font-size: var(--text-xs); }
  .panel--weapon-leaderboard { gap: var(--space-3); padding: var(--space-4); }
  .leaderboard-heading .highlight-title { font-size: clamp(1.75rem, 8vw, 2.25rem); }

  .teammate-emblem img { width: min(65%, 13rem); }

  .score-heading { align-items: flex-start; }
  .score-heading img { width: 3.5rem; height: 3.5rem; }
  .score-grid strong { font-size: clamp(2rem, 9vw, 3rem); }

  .panel--emblem-banner {
    align-items: flex-end;
  }

  .personality-value {
    font-size: clamp(6rem, 32vw, 10rem);
  }

  .cover-scrim {
    background: rgb(8 6 7 / 0.7);
  }

  .story-control {
    font-size: 0;
  }

  .story-control svg {
    width: 1.25rem;
    height: 1.25rem;
  }
}

@media (prefers-reduced-motion: reduce) {
  .story-card-enter-active,
  .story-card-leave-active {
    transition: none;
  }
}
</style>
