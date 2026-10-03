# ADR 0005: Web routing and static serving

- Status: accepted
- Date: 2026-10-03
- Scope: how the Next.js static export (`src/web`) is routed, how detail pages with ids work, what the .NET API must do to serve `out/`, and the dev workflow
- Spec: sections 37, 39, 40, 41, 59 of `docs/idea/01_AppIdea.md`
- Related: [ADR 0003](./0003-api-conventions.md) (same-origin API, cookie sessions, CSRF), `docs/design/tokens.md` (shell)

## Context

The web UI is a Next.js App Router app built with `output: 'export'` and served by the .NET API on the same origin (no Node in production, no middleware, route handlers or server actions). Consequences:

- Auth checks happen in the browser (`AuthGuard`, `/auth/me`).
- A dynamic route such as `/applications/[id]` cannot be pre-rendered per id, because ids are created at runtime. With `generateStaticParams` Next only emits the ids it is given.
- The server must map URLs to exported files itself; a plain static file handler does not do that for `/login` (file `login.html`) or for `/projects/<any id>`.

## Decision

1. **`trailingSlash: false`**. Every route is exported as `<route>.html` (`login.html`, `dashboard.html`), never `<route>/index.html`. Canonical URLs have no trailing slash.
2. **One exported shell per dynamic route**, built with `generateStaticParams()` returning the single placeholder id `_` and `dynamicParams = false`. Example: `src/web/src/app/(app)/projects/[id]/page.tsx` emits `out/projects/_.html`. Nested routes emit `applications/_/deployments/_.html`.
3. **The browser reads the real id from `window.location`** (`useResourceId()` in `src/lib/use-resource-id.ts`), never from `params`, which is always `_`. Components must treat `null` (placeholder / server render) as "loading".
4. **The API maps `/applications/{anything}` to `applications/_.html`** with the template rules below. The URL in the address bar stays `/applications/{id}`; the shell hydrates and fetches the resource from `/api/v1`.
5. **Links to detail pages are plain anchors** (`DetailLink`), not `next/link`: the ids are not pre-rendered, so a document navigation lets the API apply the mapping. Links between pre-rendered pages use `next/link` normally.
6. **404**: `app/not-found.tsx` is exported as `404.html`; the API serves it with status 404 for unknown browser routes.

Not chosen: a client-side catch-all (`[[...slug]]`) router, hash routing (`/#/applications/1`), and query-string ids (`/applications?id=1`). They work without server cooperation but give up clean URLs, per-route code splitting and (for hash routes) server-side deep-link validation. Rewriting to a placeholder shell keeps the Next route tree, metadata and bundles per page, at the cost of the small rule set below.

## URL to file rules (the API must implement exactly this)

`root` = the export directory (`out/`, deployed as `wwwroot`). Apply to `GET` and `HEAD` only, after the API's own routes (`/api/*`, `/hubs/*`, OpenAPI/docs) have had their chance. A reference implementation with tests is `src/web/scripts/export-routing.mjs` (`pnpm test` covers it; `node scripts/serve-export.mjs` serves `out/` with it).

Let `P` be the request path without the query string.

1. **Reserved**: if `P` starts with `/api/` or `/hubs/`, never touch the static files; unknown API paths return the API's own `404 application/problem+json`.
2. **Sanitise**: percent-decode once. Reject (400) if the decoded path contains `\0`, `\`, or a `.` / `..` segment. Never resolve outside `root`.
3. **Root**: `/` serves `index.html` (the client redirects to `/setup`, `/login` or `/dashboard`).
4. **Exact file**: if `root/<P>` is a **file**, serve it (assets under `_next/`, `favicon.ico`, the `*.txt` RSC payload files Next fetches for client navigation). Do not use directory index lookup: `login/` is a directory of payload files next to `login.html`.
5. **Page**: else if `root/<P>.html` exists, serve it. `/login` serves `login.html`, `/projects` serves `projects.html`.
6. **Dynamic template**: else match the segments of `P` against the exported dynamic pages. Build the template list once at startup by scanning `root` for `*.html` files (excluding `index.html`, `404.html`, `_not-found.html`) whose path contains a `_` segment, e.g. `projects/_.html` -> `[projects, _]`. A template matches when it has the same number of segments, every literal segment is equal, and each `_` matches any non-empty segment. If several match, prefer the one with the fewest `_` segments. Examples:

   | Request | Served |
   |---|---|
   | `/projects/demo-project` | `projects/_.html` |
   | `/projects/0192f3c8-...` | `projects/_.html` |
   | `/applications/abc/deployments` | `applications/_/deployments.html` |
   | `/applications/abc/deployments/xyz` | `applications/_/deployments/_.html` |
   | `/projects/a/b` | no match (404) |

   The status is `200`. Do not redirect, so the address bar keeps the real id.
7. **Trailing slash**: if `P` ends with `/` (and is not `/`) and rules 4 to 6 would match without it, answer `308` to the path without the slash (keep the query string).
8. **Not found**: otherwise answer `404`. If the last segment contains a `.` (looks like a file) or the request does not accept `text/html`, send an empty or plain-text 404. Else send `404.html` as the body with status 404.
9. **Method**: other methods on static paths get `405` with `Allow: GET, HEAD`.

Headers and caching:

- `_next/static/**`: `Cache-Control: public, max-age=31536000, immutable` (file names are content-hashed).
- Every other file, notably all `*.html`: `Cache-Control: no-cache` (revalidate with ETag), so a deploy is picked up immediately.
- Content types: `.html` `text/html; charset=utf-8`, `.js` `text/javascript`, `.css` `text/css`, `.txt` `text/plain`, `.woff2` `font/woff2`, `.ico` `image/x-icon`. Serve Brotli/gzip for text assets.
- **CSP**: the API sends `default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'` on HTML. The export contains inline scripts (the theme-init snippet in `<head>` and Next's `self.__next_f.push(...)` payload scripts). A strict CSP therefore needs `script-src 'self' 'unsafe-inline'`, or hashes computed from the exported HTML at startup. `connect-src 'self'` is enough for the API and SignalR (same origin).
- Set `X-Content-Type-Options: nosniff`, `Referrer-Policy: strict-origin-when-cross-origin` and `X-Frame-Options: DENY` on HTML (`nosniff` is sent on every static response).

ASP.NET Core sketch: `UseStaticFiles()` for rules 4 and `_next` caching, then a small endpoint/middleware implementing rules 5 to 8 (a `Dictionary<string,string>` for pages and an ordered template list), registered **after** `MapControllers`/minimal API groups and `MapHub`. `app.MapFallback` alone is not sufficient because of the 404 status and file-like path rules.

## Auth in a static export

- There is no server-side redirect. `/` and `AuthGuard` decide in the browser from `GET /api/v1/auth/setup` and `GET /api/v1/auth/me`; unauthenticated users land on `/login` after one round trip.
- The session cookie (`__Host-aethera_session`) is `HttpOnly`; the UI never reads it. Unsafe requests send `X-CSRF-Token` from `GET /auth/csrf`, refreshed once on `403 auth.csrf_invalid` (`src/web/src/lib/api/client.ts`).
- HTML pages themselves are public static files and contain no data. Everything private comes from `/api/v1` and is authorised there.

## Dev workflow

- `pnpm dev` runs `next dev` on `http://localhost:3000` and proxies `/api/*` and `/hubs/*` to the API (`AETHERA_API_ORIGIN`, default `http://localhost:5033`) with Next `rewrites`, so the browser stays same-origin (cookies, CSRF, SignalR work unchanged).
- The rewrites are added **only when `NODE_ENV === 'development'`**; `next build` runs in production mode and emits no rewrites, so the export stays valid and builds with no warnings.
- **`output: 'export'` is applied to production builds only.** Verified on Next 16.3: `/api/*` rewrites are tolerated with `output: 'export'` in `next dev` (with a notice), but the detail-route rewrite (`/projects/:id` -> `/projects/_`) fails with "missing param in generateStaticParams", because dev validates the *original* URL's params. So `next.config.ts` sets `output: isDev ? undefined : 'export'`. Dev therefore runs as a normal Next server (no notice, rewrites work); `pnpm build` is the check that the app is exportable, and `pnpm verify:export` checks the output.
- The dev rewrites also mimic rule 6 for the routes listed in `DETAIL_ROOTS` in `next.config.ts` (`/projects/:id` -> `/projects/_`). **Add a new detail root there when adding one.**
- Browsers treat `http://localhost` as a secure context, so the `Secure` `__Host-` cookie works in dev.

## Adding a detail page (checklist)

1. `app/(app)/<section>/[id]/page.tsx` with `export const dynamicParams = false` and `generateStaticParams() { return [{ id: '_' }] }`; render a client component that calls `useResourceId()`.
2. Add `<section>` to `DETAIL_ROOTS` in `next.config.ts` and to the expectations in `scripts/verify-export.mjs`.
3. Link to it with `DetailLink` (plain `<a>`).
4. Nested ids work the same way (`[id]/deployments/[deploymentId]` emits `_/deployments/_.html`); take the id you need from the matching path segment.

## Consequences

- (+) Clean URLs and per-page bundles with a single self-hosted process; no Node runtime to operate.
- (+) The rule set is small, deterministic and covered by unit tests plus `pnpm verify:export`, which checks the build output against it.
- (-) The API owns a tiny piece of routing logic and must be updated in step with the template convention (it is derived from the file listing, so new detail pages need no API change).
- (-) Detail pages render a skeleton until the client has read the id, and cannot set a per-resource `<title>` at build time (set `document.title` after load).
- (-) Links to detail pages cause a full document load instead of a client transition. Acceptable at this scale; revisit with a client-side catch-all if it becomes a UX problem.

## Implementation notes (WP1.5)

The API serves the export (`Aethera.Api/Web`: `ExportRouter`, `StaticWebMiddleware`); `ExportRouterTests` carries the cases of `export-routing.test.ts`, so the C# port and the reference stay in step.

- **Root**: `Aethera:Web:Root` (environment `Aethera__Web__Root`; relative paths are relative to the content root). Default: a `wwwroot` folder next to the application; in Development, `../../../web/out` relative to the Api project if it exists. With no root, nothing is served and one information message says where it looked. A configured root that does not exist behaves the same.
- **Order**: the middleware only handles requests **no endpoint claimed**, so `/api/*`, `/hubs/*`, `/health`, `/ready`, `/metrics`, the OpenAPI document and the docs always win, and a stray `api/x.html` in the export is never served. Reserved prefixes are matched case-insensitively.
- **Startup scan**: the file table and the dynamic-template table (fewest wildcards first, then ordinal file name) are built once at start. After a new `pnpm build`, restart the API (new content-hashed file names would otherwise 404). Hidden files (dot-prefixed, except `.well-known`) are never served.
- **Status codes**: `400 request.malformed` for `\`, NUL, `.`/`..` segments and malformed percent-encoding (decoded once, strictly, like `decodeURIComponent`); `308` for a trailing slash (query kept); `404` with `404.html` only for HTML requests of path-like URLs, else a plain-text `404`; other methods on a static path are `405 request.method_not_allowed` with `Allow: GET, HEAD`; other methods on an unknown path fall through to the API's own `404`.
- **Caching**: `_next/static/**` is `public, max-age=31536000, immutable`; everything else `no-cache` with `ETag`/`Last-Modified` and `304` on `If-None-Match`. Text responses are compressed when the client accepts it.
- **Hub origin**: the UI connects to `/hubs/*` from its own origin, which is what the API's hub Origin rule requires (ADR 0003, WP1.5 notes).
