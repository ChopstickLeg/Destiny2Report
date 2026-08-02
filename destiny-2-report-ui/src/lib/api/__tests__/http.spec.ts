import { describe, expect, it } from 'vitest'
import { ApiError, formatRetryAfter, getErrorMessage, parseApiJson } from '../http'

describe('parseApiJson', () => {
  it('preserves 64-bit membership ids as strings', () => {
    const raw = '{"membershipId":4611686018467284386,"displayName":"Guardian"}'
    const parsed = parseApiJson<{ membershipId: string; displayName: string }>(raw)
    expect(parsed.membershipId).toBe('4611686018467284386')
    expect(parsed.displayName).toBe('Guardian')
  })

  it('preserves playerMembershipId, ownerMembershipId, and instanceId', () => {
    const raw =
      '{"playerMembershipId":4611686018467284386,"ownerMembershipId":4611686018400000001,"instanceId":15989725103}'
    const parsed = parseApiJson<Record<string, string>>(raw)
    expect(parsed.playerMembershipId).toBe('4611686018467284386')
    expect(parsed.ownerMembershipId).toBe('4611686018400000001')
    expect(parsed.instanceId).toBe('15989725103')
  })

  it('leaves other numeric fields untouched', () => {
    const raw = '{"kills":123,"membershipType":3}'
    const parsed = parseApiJson<{ kills: number; membershipType: number }>(raw)
    expect(parsed.kills).toBe(123)
    expect(parsed.membershipType).toBe(3)
  })

  it('handles ids nested in arrays', () => {
    const raw = '[{"membershipId":4611686018467284386},{"membershipId":4611686018400000001}]'
    const parsed = parseApiJson<Array<{ membershipId: string }>>(raw)
    expect(parsed.map((entry) => entry.membershipId)).toEqual([
      '4611686018467284386',
      '4611686018400000001',
    ])
  })
})

describe('rate-limit errors', () => {
  it('formats Retry-After for every rate-limit error message', () => {
    const error = new ApiError(429, { code: 'rate_limited' }, 3_661)

    expect(getErrorMessage(error, 'fallback')).toBe(
      'Too many requests right now. Try again in 1 hour 2 minutes.',
    )
  })

  it('formats short retry windows without rounding them to a minute', () => {
    expect(formatRetryAfter(1)).toBe('1 second')
    expect(formatRetryAfter(45)).toBe('45 seconds')
  })
})
