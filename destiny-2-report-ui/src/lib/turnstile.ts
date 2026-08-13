type TurnstileTokenProvider = () => Promise<string>

let provider: TurnstileTokenProvider | null = null
let providerWaiters: Array<(value: TurnstileTokenProvider) => void> = []
let requestTail: Promise<void> = Promise.resolve()

export function registerTurnstileProvider(nextProvider: TurnstileTokenProvider): () => void {
  provider = nextProvider
  for (const resolve of providerWaiters) resolve(nextProvider)
  providerWaiters = []

  return () => {
    if (provider === nextProvider) provider = null
  }
}

export function withTurnstileToken<T>(operation: (token: string) => Promise<T>): Promise<T> {
  const run = async () => {
    const activeProvider = provider ?? (await waitForProvider())
    return operation(await activeProvider())
  }
  const request = requestTail.then(run, run)
  requestTail = request.then(
    () => undefined,
    () => undefined,
  )
  return request
}

function waitForProvider(): Promise<TurnstileTokenProvider> {
  return new Promise((resolve) => providerWaiters.push(resolve))
}
