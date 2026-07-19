import '@fontsource-variable/inter/index.css'
import '@fontsource-variable/space-grotesk/index.css'
import '@/styles/tokens.css'
import '@/styles/base.css'

import { createApp } from 'vue'
import { createPinia } from 'pinia'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'

import App from './App.vue'
import router from './router'
import { isApiError } from '@/lib/api/http'
import { canUseWebPush, ensurePushServiceWorker } from '@/lib/push-service-worker'

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 60_000,
      refetchOnWindowFocus: false,
      retry: (failureCount, error) => {
        // 4xx responses are deliberate answers, not transient faults.
        if (isApiError(error) && error.status >= 400 && error.status < 500) return false
        return failureCount < 2
      },
    },
  },
})

createApp(App).use(createPinia()).use(router).use(VueQueryPlugin, { queryClient }).mount('#app')

if (canUseWebPush()) {
  void ensurePushServiceWorker().catch(() => {
    // The opt-in control surfaces registration errors if the user chooses notifications.
  })
}
