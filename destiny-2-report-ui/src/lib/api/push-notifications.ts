import type { ReportIdentity } from './reports'
import { apiFetch } from './http'

export interface PushNotificationConfig {
  enabled: boolean
  publicKey: string | null
}

interface PushSubscriptionKeys {
  p256dh: string
  auth: string
}

interface PushSubscriptionBody extends ReportIdentity {
  endpoint: string
  keys: PushSubscriptionKeys
}

interface PushSubscriptionIdentityBody extends ReportIdentity {
  endpoint: string
}

export function fetchPushNotificationConfig(signal?: AbortSignal): Promise<PushNotificationConfig> {
  return apiFetch('/push-notifications/config', { signal })
}

export function registerReportPushSubscription(
  identity: ReportIdentity,
  subscription: PushSubscription,
): Promise<void> {
  const serialized = subscription.toJSON()
  const keys = serialized.keys
  if (!serialized.endpoint || !keys?.p256dh || !keys.auth) {
    return Promise.reject(new Error('The browser returned an incomplete push subscription.'))
  }

  const body: PushSubscriptionBody = {
    ...identity,
    endpoint: serialized.endpoint,
    keys: { p256dh: keys.p256dh, auth: keys.auth },
  }
  return apiFetch('/push-notifications/subscriptions', { method: 'POST', body })
}

export function fetchReportPushSubscriptionStatus(
  identity: ReportIdentity,
  endpoint: string,
): Promise<{ registered: boolean }> {
  const body: PushSubscriptionIdentityBody = { ...identity, endpoint }
  return apiFetch('/push-notifications/subscriptions/status', { method: 'POST', body })
}

export function removeReportPushSubscription(
  identity: ReportIdentity,
  endpoint: string,
): Promise<void> {
  const body: PushSubscriptionIdentityBody = { ...identity, endpoint }
  return apiFetch('/push-notifications/subscriptions/remove', { method: 'POST', body })
}
