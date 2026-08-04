import { readFileSync } from 'node:fs'
import { fileURLToPath, URL } from 'node:url'

import { defineConfig, loadEnv } from 'vite'
import vue from '@vitejs/plugin-vue'
import vueDevTools from 'vite-plugin-vue-devtools'

import { cloudflare } from '@cloudflare/vite-plugin'

// https://vite.dev/config/
export default defineConfig(({ command, mode }) => {
  const env = loadEnv(mode, process.cwd(), '')
  const workspaceEnv = loadEnv(mode, fileURLToPath(new URL('../', import.meta.url)), '')

  // In development the UI proxies /api to the ASP.NET Core backend so the
  // browser sees a single origin (matching the intended production shape).
  // Override with VITE_DEV_API_PROXY if the API runs elsewhere.
  const apiProxyTarget = env.VITE_DEV_API_PROXY ?? 'http://localhost:5063'
  const clientId = env.VITE_BUNGIE_CLIENT_ID || workspaceEnv.BUNGIE_CLIENT_ID
  const httpsPfxPath = env.DEV_HTTPS_PFX_PATH
  const httpsPfxPassword = env.DEV_HTTPS_PFX_PASSWORD

  if (httpsPfxPath && !httpsPfxPassword) {
    throw new Error('DEV_HTTPS_PFX_PASSWORD is required when DEV_HTTPS_PFX_PATH is set.')
  }

  return {
    // During `vite dev`, let Vite's /api proxy below target the local API.
    // The Cloudflare worker also handles /api and would otherwise take
    // precedence, forwarding local requests to API_ORIGIN from wrangler.jsonc.
    plugins: [vue(), vueDevTools(), ...(command === 'build' && mode !== 'test' ? [cloudflare()] : [])],
    // For local development, reuse the public client id from the workspace's
    // ignored .env file. Only this public value is exposed; the client secret
    // remains available exclusively to the backend.
    define: clientId
      ? { 'import.meta.env.VITE_BUNGIE_CLIENT_ID': JSON.stringify(clientId) }
      : undefined,
    resolve: {
      alias: {
        '@': fileURLToPath(new URL('./src', import.meta.url)),
      },
    },
    server: {
      https: httpsPfxPath
        ? {
            pfx: readFileSync(httpsPfxPath),
            passphrase: httpsPfxPassword,
          }
        : undefined,
      port: 5173,
      strictPort: true,
      proxy: {
        '/api': {
          target: apiProxyTarget,
          changeOrigin: true,
        },
      },
    },
  }
})
