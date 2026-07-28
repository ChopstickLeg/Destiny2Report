# Destiny 2 Report UI

The Vue 3 frontend for Destiny 2 Report. It searches Bungie players, renders stored
player reports, drives live report generation over Server-Sent Events, and offers a
signed-in "Your Story" retrospective. The product and architecture plan lives in
[`UI_PLAN.md`](./UI_PLAN.md).

## Stack

- Vue 3 + TypeScript + Vite, Vue Router
- [TanStack Query](https://tanstack.com/query) for all server state (caching,
  cancellation, retries, invalidation after crawl completion)
- Pinia only for durable client state (the Bungie session)
- Project-owned chart components (`src/components/charts/`) for ranked and split
  bars; no chart framework
- A small token-based design system (`src/styles/tokens.css`); no UI framework

## Development

```sh
npm install
npm run dev
```

The dev server proxies `/api` to the ASP.NET Core backend so the browser sees a
single origin (the intended production shape). By default it targets
`http://localhost:5063` (the API's `launchSettings.json` HTTP profile); override
with `VITE_DEV_API_PROXY` in `.env.local`.

Report-completion notifications use the browser Push API and a service worker.
The backend only advertises the opt-in control when all three `WebPush` settings
are configured. Generate a VAPID key pair once, keep the private key server-side,
and add the values to the workspace `.env`:

```sh
dotnet run --project Destiny2Report.API -- --generate-vapid-keys
```

Set `WEB_PUSH_SUBJECT` to a `mailto:` address or an HTTPS URL that identifies the
site operator. The same VAPID key pair must be retained across deployments or
existing browser subscriptions will stop working.

### Environment variables

Copy `.env.example` to `.env.local` and adjust as needed:

| Variable | Purpose | Default |
| --- | --- | --- |
| `VITE_API_BASE_URL` | API base path used by the fetch client | `/api` (same origin) |
| `VITE_DEV_API_PROXY` | Dev-only proxy target for `/api` | `http://localhost:5063` |
| `VITE_BUNGIE_CLIENT_ID` | Public Bungie OAuth client id; sign-in is hidden when unset | Not set |
| `VITE_BUNGIE_AUTHORIZE_URL` | Bungie authorization endpoint override | `https://www.bungie.net/en/OAuth/Authorize` |
| `DEV_HTTPS_PFX_PATH` | Optional localhost PFX used to serve Vite over trusted HTTPS | Not set |
| `DEV_HTTPS_PFX_PASSWORD` | Password for `DEV_HTTPS_PFX_PATH`; kept server-side by Vite | Not set |

During local development, the UI also falls back to `BUNGIE_CLIENT_ID` from the
workspace root `.env`. This keeps the public client id aligned with the backend
without exposing `BUNGIE_CLIENT_SECRET` to browser code.

### Auth model (prototype)

The backend currently returns Bungie tokens directly. The UI keeps the access
token in `sessionStorage`, validates an OAuth `state` value itself, and
deliberately discards the refresh token. See `src/stores/session.ts` for the
documented tradeoff and the intended backend-for-frontend end state.

### Commands

```sh
npm run dev         # dev server with /api proxy
npm run build       # type-check + production build
npm run test:unit   # Vitest (watch mode; `npx vitest run` for CI)
npm run lint        # oxlint + eslint
npm run format      # prettier over src/
```

## Source layout

```
src/
  components/
    base/       # buttons, skeletons, empty/error states, segmented control
    charts/     # BarList, SplitBar (accessible, project-owned)
    shell/      # header, global search, account menu, footer
  features/     # behavior-organized: player-search, report-overview,
                # report-generation, combat, activities, auth, story, status
  lib/
    api/        # fetch client, transport types, SSE parser, Bungie URLs
    formatting/ # .NET TimeSpan parsing, numbers, dates
  stores/       # Pinia session store
  styles/       # design tokens + base styles
  test/         # deterministic report fixtures
```

Notes worth knowing before touching data code:

- **64-bit IDs:** Destiny membership and instance IDs exceed
  `Number.MAX_SAFE_INTEGER`; the fetch client rewrites those fields to strings
  before `JSON.parse` (`src/lib/api/http.ts`).
- **Durations:** `TimeSpan` values arrive as `[-][d.]hh:mm:ss[.fffffff]` strings
  and are parsed once into seconds (`src/lib/formatting/duration.ts`).
- **Rates:** `winRate`/`clearRate` are backend-rounded fractions of 1 and are
  multiplied by 100 exactly once (`src/lib/formatting/numbers.ts`).
- **Editorial rules:** "Your Story" highlights and the at-a-glance strip are pure,
  unit-tested selector functions (`src/features/story/selectors.ts`,
  `src/features/report-overview/report-view.ts`).
