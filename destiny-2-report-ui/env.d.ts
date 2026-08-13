/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** API base path; defaults to same-origin `/api`. */
  readonly VITE_API_BASE_URL?: string
  /** Bungie application OAuth client id (public). */
  readonly VITE_BUNGIE_CLIENT_ID?: string
  /** Bungie authorization endpoint override. */
  readonly VITE_BUNGIE_AUTHORIZE_URL?: string
  /** Public Cloudflare Turnstile widget sitekey. */
  readonly VITE_TURNSTILE_SITE_KEY?: string
  /** Dev-only proxy target for /api (see vite.config.ts). */
  readonly VITE_DEV_API_PROXY?: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}
