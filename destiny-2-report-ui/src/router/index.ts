import { createRouter, createWebHistory, type RouteLocationNormalized } from 'vue-router'
import { useSessionStore } from '@/stores/session'

const APP_NAME = 'Destiny 2 Report'

const router = createRouter({
  history: createWebHistory(import.meta.env.BASE_URL),
  scrollBehavior(to, from, savedPosition) {
    if (savedPosition) return savedPosition
    // Filter changes rewrite the query on the same screen; don't jump.
    if (to.path === from.path) return undefined
    return { top: 0 }
  },
  routes: [
    {
      path: '/',
      name: 'home',
      component: () => import('@/features/player-search/HomeView.vue'),
      meta: { title: 'Search' },
    },
    {
      path: '/search',
      name: 'search',
      component: () => import('@/features/player-search/SearchResultsView.vue'),
      meta: { title: 'Search' },
    },
    {
      path: '/report/:membershipTypeId(\\d+)/:membershipId(\\d+)',
      component: () => import('@/features/report-overview/ReportLayout.vue'),
      children: [
        {
          path: '',
          name: 'report-overview',
          component: () => import('@/features/report-overview/OverviewView.vue'),
          meta: { title: 'Report' },
        },
        {
          path: 'combat',
          name: 'report-combat',
          component: () => import('@/features/combat/CombatView.vue'),
          meta: { title: 'Combat' },
        },
        {
          path: 'competitive',
          name: 'report-competitive',
          component: () => import('@/features/report-overview/CompetitiveView.vue'),
          meta: { title: 'Competitive' },
        },
        {
          path: 'endgame',
          name: 'report-endgame',
          component: () => import('@/features/report-overview/EndgameView.vue'),
          meta: { title: 'Endgame' },
        },
        {
          path: 'activities',
          name: 'report-activities',
          component: () => import('@/features/activities/ActivitiesView.vue'),
          meta: { title: 'Activities' },
        },
      ],
    },
    {
      path: '/auth/callback',
      name: 'auth-callback',
      component: () => import('@/features/auth/AuthCallbackView.vue'),
      meta: { title: 'Signing in' },
    },
    {
      path: '/me',
      name: 'me',
      component: () => import('@/features/auth/MeView.vue'),
      meta: { title: 'My report' },
    },
    {
      path: '/me/story',
      name: 'story',
      component: () => import('@/features/story/StoryView.vue'),
      meta: { title: 'Your Story' },
    },
    {
      path: '/story/:shareToken',
      name: 'shared-story',
      component: () => import('@/features/story/StoryView.vue'),
      meta: { title: 'Guardian Story' },
    },
    ...(import.meta.env.DEV
      ? [
          {
            path: '/dev/story/:membershipTypeId(\\d+)/:membershipId(\\d+)',
            name: 'story-preview',
            component: () => import('@/features/story/StoryView.vue'),
            meta: { title: 'Story preview' },
          },
        ]
      : []),
    {
      path: '/faq',
      name: 'faq',
      component: () => import('@/features/faq/FaqView.vue'),
      meta: { title: 'Frequently asked questions' },
    },
    {
      path: '/leaderboards',
      name: 'leaderboards',
      component: () => import('@/features/leaderboards/LeaderboardsView.vue'),
      meta: { title: 'Leaderboards' },
    },
    {
      path: '/admin',
      name: 'admin',
      component: () => import('@/features/admin/AdminView.vue'),
      meta: { title: 'Crawl operations' },
      async beforeEnter() {
        const session = useSessionStore()
        await session.bootstrap()
        return session.isAdmin ? true : { name: 'home' }
      },
    },
    {
      path: '/:pathMatch(.*)*',
      name: 'not-found',
      component: () => import('@/features/NotFoundView.vue'),
      meta: { title: 'Not found' },
    },
  ],
})

router.afterEach((to: RouteLocationNormalized) => {
  const title = to.meta.title as string | undefined
  document.title = title ? `${title} · ${APP_NAME}` : APP_NAME
})

export default router
