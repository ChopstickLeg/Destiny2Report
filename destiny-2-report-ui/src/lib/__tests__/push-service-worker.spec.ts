import { describe, expect, it } from 'vitest'
import { subscriptionUsesKey, urlBase64ToUint8Array } from '../push-service-worker'

describe('Web Push key helpers', () => {
  it('decodes URL-safe base64 application server keys', () => {
    expect([...urlBase64ToUint8Array('AQID-v8')]).toEqual([1, 2, 3, 250, 255])
  })

  it('recognizes whether an existing subscription uses the configured key', () => {
    const applicationServerKey = Uint8Array.from([1, 2, 3]).buffer
    const subscription = {
      options: { applicationServerKey },
    } as unknown as PushSubscription

    expect(subscriptionUsesKey(subscription, Uint8Array.from([1, 2, 3]))).toBe(true)
    expect(subscriptionUsesKey(subscription, Uint8Array.from([1, 2, 4]))).toBe(false)
  })
})
