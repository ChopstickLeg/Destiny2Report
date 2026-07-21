self.addEventListener('push', (event) => {
  let message = {}
  try {
    message = event.data ? event.data.json() : {}
  } catch {
    message = {}
  }

  const title = message.title || 'Destiny 2 Report'
  const icon = new URL('favicon.svg', self.registration.scope).href
  const options = {
    body: message.body || 'Your report is ready.',
    icon,
    badge: icon,
    tag: message.tag || 'destiny-report-ready',
    data: { url: message.url || '/' },
  }

  event.waitUntil(self.registration.showNotification(title, options))
})

self.addEventListener('notificationclick', (event) => {
  event.notification.close()
  const destination = new URL(event.notification.data?.url || '/', self.location.origin).href

  event.waitUntil(
    self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then((clients) => {
      const existing = clients.find((client) => client.url === destination)
      if (existing) return existing.focus()
      return self.clients.openWindow(destination)
    }),
  )
})
