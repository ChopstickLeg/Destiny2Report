import { mount } from '@vue/test-utils'
import { ref } from 'vue'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import GenerationExperience from '../GenerationExperience.vue'

const { submitAndWatch, watchQueue } = vi.hoisted(() => ({
  submitAndWatch: vi.fn<() => Promise<void>>(),
  watchQueue: vi.fn<() => Promise<void>>(),
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
  })

  it('quietly re-submits a queued profile before watching it', () => {
    mount(GenerationExperience, {
      props: {
        identity: { membershipTypeId: 3, membershipId: '4611686018487421905' },
        initialState: 'queued',
        playerName: null,
        crawlError: '',
      },
    })

    expect(submitAndWatch).toHaveBeenCalledExactlyOnceWith({
      suppressCooldownError: true,
      watchOnSuppressedCooldown: true,
    })
    expect(watchQueue).not.toHaveBeenCalled()
  })

  it('only watches a running profile without submitting it again', () => {
    mount(GenerationExperience, {
      props: {
        identity: { membershipTypeId: 3, membershipId: '4611686018487421905' },
        initialState: 'running',
        playerName: null,
        crawlError: '',
      },
    })

    expect(watchQueue).toHaveBeenCalledOnce()
    expect(submitAndWatch).not.toHaveBeenCalled()
  })

  it('starts a missing report when navigation requested generation', () => {
    mount(GenerationExperience, {
      props: {
        identity: { membershipTypeId: 3, membershipId: '4611686018487421905' },
        initialState: 'missing',
        playerName: null,
        crawlError: '',
        autoStart: true,
      },
    })

    expect(submitAndWatch).toHaveBeenCalledOnce()
    expect(watchQueue).not.toHaveBeenCalled()
  })

  it('does not automatically retry a failed report', () => {
    mount(GenerationExperience, {
      props: {
        identity: { membershipTypeId: 3, membershipId: '4611686018487421905' },
        initialState: 'failed',
        playerName: null,
        crawlError: 'Bungie was unavailable',
        autoStart: true,
      },
    })

    expect(submitAndWatch).not.toHaveBeenCalled()
    expect(watchQueue).not.toHaveBeenCalled()
  })
})
