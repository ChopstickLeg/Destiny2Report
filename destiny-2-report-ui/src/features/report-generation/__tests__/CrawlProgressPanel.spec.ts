import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import type { ReportQueueStatusResponse } from '@/lib/api/types'
import CrawlProgressPanel from '../CrawlProgressPanel.vue'

function status(
  overrides: Partial<ReportQueueStatusResponse> = {},
): ReportQueueStatusResponse {
  return {
    membershipTypeId: 3,
    membershipId: '4611686018487421905',
    status: 'queued',
    streamEntryId: '1-0',
    error: null,
    position: 2,
    queueLength: 3,
    updatedAtUtc: '2026-07-25T12:00:00Z',
    progress: null,
    ...overrides,
  }
}

const staleCompletedProgress = {
  phase: 'finalizing',
  label: 'Finalizing report',
  current: 1,
  total: 1,
  startedAtUtc: '2026-07-25T11:59:00Z',
  updatedAtUtc: '2026-07-25T12:00:00Z',
}

describe('CrawlProgressPanel', () => {
  it('shows queued work as indeterminate even if stale progress is present', () => {
    const wrapper = mount(CrawlProgressPanel, {
      props: {
        latest: status({ progress: staleCompletedProgress }),
        reconnectAttempt: 0,
        startedAt: null,
      },
    })

    expect(wrapper.text()).toContain('Queued at position 2 of 3')
    expect(wrapper.text()).not.toContain('1 of 1')
    expect(wrapper.find('.progress-fill--indeterminate').exists()).toBe(true)
    expect(wrapper.find('[role="progressbar"]').attributes('aria-valuenow')).toBeUndefined()

    wrapper.unmount()
  })

  it('shows determinate counters while the crawler is running', () => {
    const wrapper = mount(CrawlProgressPanel, {
      props: {
        latest: status({ status: 'running', progress: staleCompletedProgress }),
        reconnectAttempt: 0,
        startedAt: null,
      },
    })

    expect(wrapper.text()).toContain('Finalizing report')
    expect(wrapper.text()).toContain('1 of 1')
    expect(wrapper.find('.progress-fill--indeterminate').exists()).toBe(false)
    expect(wrapper.find('[role="progressbar"]').attributes('aria-valuenow')).toBe('100')

    wrapper.unmount()
  })
})
