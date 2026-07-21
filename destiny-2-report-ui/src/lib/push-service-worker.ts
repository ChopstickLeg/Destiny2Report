let registrationPromise: Promise<ServiceWorkerRegistration> | null = null

export function canUseWebPush(): boolean {
  return (
    window.isSecureContext &&
    'serviceWorker' in navigator &&
    'PushManager' in window &&
    'Notification' in window
  )
}

export function ensurePushServiceWorker(): Promise<ServiceWorkerRegistration> {
  if (!canUseWebPush()) {
    return Promise.reject(new Error('Web Push is not supported by this browser.'))
  }

  registrationPromise ??= navigator.serviceWorker.register(
    `${import.meta.env.BASE_URL}push-service-worker.js`,
    { scope: import.meta.env.BASE_URL },
  )
  return registrationPromise
}

export function urlBase64ToUint8Array(value: string): Uint8Array<ArrayBuffer> {
  const padding = '='.repeat((4 - (value.length % 4)) % 4)
  const base64 = (value + padding).replace(/-/g, '+').replace(/_/g, '/')
  const raw = window.atob(base64)
  const output = new Uint8Array(new ArrayBuffer(raw.length))

  for (let index = 0; index < raw.length; index += 1) {
    output[index] = raw.charCodeAt(index)
  }

  return output
}

export function subscriptionUsesKey(
  subscription: PushSubscription,
  publicKey: Uint8Array<ArrayBuffer>,
): boolean {
  const current = subscription.options.applicationServerKey
  if (!current) return false
  const bytes = new Uint8Array(current)
  return bytes.length === publicKey.length && bytes.every((value, index) => value === publicKey[index])
}
