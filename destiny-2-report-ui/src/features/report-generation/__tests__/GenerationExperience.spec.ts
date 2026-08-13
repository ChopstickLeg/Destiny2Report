import { flushPromises, mount } from '@vue/test-utils'
import { ref } from 'vue'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import GenerationExperience from '../GenerationExperience.vue'

const { submitAndWatch, watchQueue, fetchQueuePolicy, session } = vi.hoisted(() => ({
  submitAndWatch: vi.fn<() => Promise<void>>(),
  watchQueue: vi.fn<() => Promise<void>>(),
  fetchQueuePolicy: vi.fn<() => Promise<{ authenticationRequired: boolean }>>(),
  session: {
    status: 'signed-in',
    isSignedIn: true,
    beginSignIn: vi.fn<(returnTo: string) => void>(),
  },
}))

vi.mock('vue-router', () => ({
  useRoute: () => ({ fullPath: '/report/3/4611686018487421905' }),
}))

vi.mock('@/lib/api/reports', () => ({ fetchQueuePolicy }))

vi.mock('@/stores/session', () => ({
  useSessionStore: () => session,
}))

vi.mock('../useQueueWatcher', () => ({
  useQueueWatcher: () => ({
    phase: ref('idle'),
    latest: ref(null),
    submitError: ref(null),
    reconnectAttempt: ref(0),
    startedAt: ref(null),
    submitAndWatch,
    watch: watchQueue,
  }),
}))

describe('GenerationExperience', () => {
  beforeEach(() => {
    submitAndWatch.mockReset()
    watchQueue.mockReset()
    fetchQueuePolicy.mockReset()
    fetchQueuePolicy.mockResolvedValue({ authenticationRequired: false })
    session.status = 'signed-in'
    session.isSignedIn = true
    session.beginSignIn.mockReset()
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  it('quietly re-submits a queued profile before watching it', async () => {
    mount(GenerationExperience, {
      props: {
        identity: { membershipTypeId: 3, membershipId: '4611686018487421905' },
        initialState: 'queued',
        playerName: null,
        crawlError: '',
      },
    })

    await flushPromises()

    expect(submitAndWatch).toHaveBeenCalledExactlyOnceWith({
      suppressCooldownError: true,
      watchOnSuppressedCooldown: true,
    })
    expect(watchQueue).not.toHaveBeenCalled()
  })

  it('only watches a running profile without submitting it again', async () => {
    mount(GenerationExperience, {
      props: {
        identity: { membershipTypeId: 3, membershipId: '4611686018487421905' },
        initialState: 'running',
        playerName: null,
        crawlError: '',
      },
    })

    await flushPromises()

    expect(watchQueue).toHaveBeenCalledOnce()
    expect(submitAndWatch).not.toHaveBeenCalled()
  })

  it('starts a missing report when navigation requested generation', async () => {
    mount(GenerationExperience, {
      props: {
        identity: { membershipTypeId: 3, membershipId: '4611686018487421905' },
        initialState: 'missing',
        playerName: null,
        crawlError: '',
        autoStart: true,
      },
    })

    await flushPromises()

    expect(submitAndWatch).toHaveBeenCalledOnce()
    expect(watchQueue).not.toHaveBeenCalled()
  })

  it('does not automatically retry a failed report', async () => {
    mount(GenerationExperience, {
      props: {
        identity: { membershipTypeId: 3, membershipId: '4611686018487421905' },
        initialState: 'failed',
        playerName: null,
        crawlError: 'Bungie was unavailable',
        autoStart: true,
      },
    })

    await flushPromises()

    expect(submitAndWatch).not.toHaveBeenCalled()
    expect(watchQueue).not.toHaveBeenCalled()
  })

  it('explains the sign-in requirement instead of auto-queueing', async () => {
    fetchQueuePolicy.mockResolvedValue({ authenticationRequired: true })
    session.isSignedIn = false

    const wrapper = mount(GenerationExperience, {
      props: {
        identity: { membershipTypeId: 3, membershipId: '4611686018487421905' },
        initialState: 'missing',
        playerName: null,
        crawlError: '',
        autoStart: true,
      },
    })

    await flushPromises()

    expect(wrapper.text()).toContain('must sign in with Bungie')
    expect(wrapper.text()).toContain('Sign in with Bungie')
    expect(submitAndWatch).not.toHaveBeenCalled()
  })

  it('disables generation until queue policy discovery completes', async () => {
    let resolvePolicy!: (policy: { authenticationRequired: boolean }) => void
    fetchQueuePolicy.mockReturnValue(
      new Promise((resolve) => {
        resolvePolicy = resolve
      }),
    )

    const wrapper = mount(GenerationExperience, {
      props: {
        identity: { membershipTypeId: 3, membershipId: '4611686018487421905' },
        initialState: 'missing',
        playerName: null,
        crawlError: '',
        autoStart: true,
      },
    })

    const button = wrapper.get('button')
    expect(button.attributes('disabled')).toBeDefined()
    expect(button.text()).toContain('Checking queue access')
    await button.trigger('click')
    expect(submitAndWatch).not.toHaveBeenCalled()

    resolvePolicy({ authenticationRequired: false })
    await flushPromises()
    expect(submitAndWatch).toHaveBeenCalledOnce()
  })

  it('does not submit while the browser session is still resolving', async () => {
    session.status = 'resolving'

    const wrapper = mount(GenerationExperience, {
      props: {
        identity: { membershipTypeId: 3, membershipId: '4611686018487421905' },
        initialState: 'missing',
        playerName: null,
        crawlError: '',
        autoStart: true,
      },
    })

    await flushPromises()
    const button = wrapper.get('button')
    expect(button.attributes('disabled')).toBeDefined()
    expect(button.text()).toContain('Checking queue access')
    expect(submitAndWatch).not.toHaveBeenCalled()
  })

  it('offers a manual retry after bounded queue-policy retries fail', async () => {
    vi.useFakeTimers()
    fetchQueuePolicy.mockRejectedValue(new Error('network unavailable'))

    const wrapper = mount(GenerationExperience, {
      props: {
        identity: { membershipTypeId: 3, membershipId: '4611686018487421905' },
        initialState: 'missing',
        playerName: null,
        crawlError: '',
        autoStart: true,
      },
    })

    await vi.runAllTimersAsync()
    await flushPromises()

    expect(fetchQueuePolicy).toHaveBeenCalledTimes(3)
    expect(wrapper.text()).toContain("Queue access couldn't be verified")
    const retryButton = wrapper.get('button')
    expect(retryButton.text()).toContain('Retry queue access check')
    expect(retryButton.attributes('disabled')).toBeUndefined()
    expect(submitAndWatch).not.toHaveBeenCalled()

    fetchQueuePolicy.mockResolvedValue({ authenticationRequired: false })
    await retryButton.trigger('click')
    await flushPromises()

    expect(fetchQueuePolicy).toHaveBeenCalledTimes(4)
    expect(submitAndWatch).toHaveBeenCalledOnce()
  })
})
