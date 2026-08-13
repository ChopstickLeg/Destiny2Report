import { beforeEach, describe, expect, it, vi } from 'vitest'

const { apiFetch, withTurnstileToken } = vi.hoisted(() => ({
  apiFetch: vi.fn(),
  withTurnstileToken: vi.fn(),
}))

vi.mock('../http', () => ({
  apiFetch,
  isApiError: vi.fn(),
}))
vi.mock('../../turnstile', () => ({ withTurnstileToken }))

import { queueReport } from '../reports'

describe('queueReport', () => {
  beforeEach(() => {
    apiFetch.mockReset()
    withTurnstileToken.mockReset()
  })

  it('obtains a fresh Turnstile token for the queue POST', async () => {
    withTurnstileToken.mockImplementation((operation) => operation('verified-token'))
    apiFetch.mockResolvedValue({ jobId: 'job-1' })

    await queueReport({ membershipTypeId: 3, membershipId: '4611686018463095984' })

    expect(withTurnstileToken).toHaveBeenCalledOnce()
    expect(apiFetch).toHaveBeenCalledWith('/reports/queue', {
      method: 'POST',
      body: {
        membershipTypeId: 3,
        membershipId: '4611686018463095984',
        turnstileToken: 'verified-token',
      },
    })
  })
})
