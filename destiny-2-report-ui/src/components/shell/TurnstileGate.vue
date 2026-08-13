<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { registerTurnstileProvider } from '@/lib/turnstile'

const TURNSTILE_SCRIPT = 'https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit'
const DEVELOPMENT_SITE_KEY = '1x00000000000000000000AA'
const siteKey =
  import.meta.env.VITE_TURNSTILE_SITE_KEY ?? (import.meta.env.DEV ? DEVELOPMENT_SITE_KEY : '')

interface TurnstileApi {
  render(container: HTMLElement, options: Record<string, unknown>): string
  execute(widgetId: string): void
  reset(widgetId: string): void
  remove(widgetId: string): void
}

declare global {
  interface Window {
    turnstile?: TurnstileApi
  }
}

const container = ref<HTMLElement | null>(null)
const interactionRequired = ref(false)
let widgetId: string | null = null
let hasExecuted = false
let pending:
  | {
      resolve: (token: string) => void
      reject: (error: Error) => void
    }
  | undefined
let markMounted!: () => void
const mounted = new Promise<void>((resolve) => {
  markMounted = resolve
})

const unregister = registerTurnstileProvider(executeChallenge)

onMounted(markMounted)
onBeforeUnmount(() => {
  unregister()
  pending?.reject(new Error('Security verification was interrupted.'))
  pending = undefined
  if (widgetId && window.turnstile) window.turnstile.remove(widgetId)
})

async function executeChallenge(): Promise<string> {
  await mounted
  if (!siteKey) {
    throw new Error('Security verification is not configured. Please try again later.')
  }

  const turnstile = await loadTurnstile()
  if (!container.value) throw new Error('Security verification is unavailable.')
  if (!widgetId) {
    widgetId = turnstile.render(container.value, {
      sitekey: siteKey,
      action: 'queue_report',
      appearance: 'interaction-only',
      execution: 'execute',
      theme: 'auto',
      retry: 'auto',
      'refresh-expired': 'manual',
      callback: (token: string) => settleSuccess(token),
      'error-callback': () => settleFailure('Security verification failed. Please try again.'),
      'expired-callback': () => settleFailure('Security verification expired. Please try again.'),
      'timeout-callback': () => settleFailure('Security verification timed out. Please try again.'),
      'before-interactive-callback': () => {
        interactionRequired.value = true
      },
      'after-interactive-callback': () => {
        interactionRequired.value = false
      },
    })
  }

  return new Promise<string>((resolve, reject) => {
    pending = { resolve, reject }
    try {
      if (hasExecuted) turnstile.reset(widgetId!)
      hasExecuted = true
      turnstile.execute(widgetId!)
    } catch {
      settleFailure('Security verification could not start. Please try again.')
    }
  })
}

function settleSuccess(token: string) {
  const current = pending
  pending = undefined
  interactionRequired.value = false
  current?.resolve(token)
}

function settleFailure(message: string) {
  const current = pending
  pending = undefined
  interactionRequired.value = false
  current?.reject(new Error(message))
}

let scriptPromise: Promise<TurnstileApi> | null = null
function loadTurnstile(): Promise<TurnstileApi> {
  if (window.turnstile) return Promise.resolve(window.turnstile)
  if (scriptPromise) return scriptPromise

  scriptPromise = new Promise<TurnstileApi>((resolve, reject) => {
    const existing = document.querySelector<HTMLScriptElement>('script[data-d2r-turnstile]')
    const script = existing ?? document.createElement('script')
    const loaded = () => {
      if (window.turnstile) resolve(window.turnstile)
      else reject(new Error('Security verification did not load correctly.'))
    }
    script.addEventListener('load', loaded, { once: true })
    script.addEventListener(
      'error',
      () => reject(new Error('Security verification could not be loaded.')),
      { once: true },
    )
    if (!existing) {
      script.src = TURNSTILE_SCRIPT
      script.async = true
      script.defer = true
      script.dataset.d2rTurnstile = ''
      document.head.appendChild(script)
    }
  }).catch((error) => {
    scriptPromise = null
    throw error
  })
  return scriptPromise
}
</script>

<template>
  <div
    class="turnstile-shell"
    :class="{ 'turnstile-shell--interactive': interactionRequired }"
    aria-live="polite"
  >
    <div ref="container" />
  </div>
</template>

<style scoped>
.turnstile-shell {
  position: fixed;
  right: var(--space-4);
  bottom: var(--space-4);
  z-index: 1000;
  pointer-events: none;
}

.turnstile-shell--interactive {
  pointer-events: auto;
}
</style>
