# Aethera design tokens

Status: accepted (WP0.2a). Implementation lives in [`tokens.css`](./tokens.css); this document explains the system and is the reference for every UI work package.

The look is derived from the ContextOS / Chord screenshots in `docs/idea/ui inspiration/`: near-black canvas, one lime accent, hairline borders, square corners, monospace "system voice" for labels and status, dotted-grid canvas, terminal-window cards. It must work equally well in light and dark (`docs/idea/01_UI.md`).

## 1. Principles

1. **One accent.** Lime (`#b8f26b`) is the only brand colour. Everything else is neutral or a status colour.
2. **Information density without noise.** 14px body text, 1px hairlines, almost no shadows. Depth comes from borders and surface steps (`bg < surface < raised`).
3. **Square.** Radius is 0-2px everywhere. The only round things are status dots and avatars.
4. **Mono is the system voice.** IDs, hashes, timestamps, indices (`01`), eyebrow labels, logs and `[ ok ]` status lines are monospace. Prose and headings are sans.
5. **Colour is never the only signal.** Status always pairs colour with an icon or text label.
6. **Both themes are first-class.** Every token has a light and a dark value; light is not an inversion hack.

## 2. Colour

Dark is the default (`:root`). Light is `html[data-theme="light"]`. The toggle offers light / dark / system; "system" is resolved in JS (no `prefers-color-scheme` block in the CSS, so server-rendered pages never flash the wrong theme once `data-theme` is set from a cookie/`localStorage` in an inline script).

### 2.1 Surfaces and text

| Role | shadcn token | Dark | Light |
|---|---|---|---|
| Page background | `--background` | `#0b0d0d` | `#f6f7f4` |
| Surface (cards, panels) | `--card` | `#111414` | `#fbfbf9` |
| Raised (popovers, menus, inputs bg) | `--popover` | `#161a19` | `#ffffff` |
| Hairline border | `--border` | `#1f2523` | `#dcdfd8` |
| Strong border (hover, dividers) | `--ae-border-strong` | `#2f3835` | `#c3c8c0` |
| Form-control border | `--input` | `#5e6b64` | `#7d8580` |
| Text | `--foreground` | `#e6e8e6` | `#0f1412` |
| Muted text | `--muted-foreground` | `#8a918d` | `#555d58` |
| Muted surface | `--muted` / `--secondary` | `#161a19` | `#eceee9` |
| Hover / selected surface | `--accent` (shadcn meaning!) | `#1b211f` | `#e9ece5` |
| Sidebar | `--sidebar` | `#0e1111` | `#f1f3ee` |

> **shadcn gotcha.** In shadcn/ui, `--accent` is the *neutral hover/selected surface*, not the brand colour. The brand lime is `--primary` (alias `--ae-accent`). Do not "fix" this.

### 2.2 Brand accent

| Role | Token | Dark | Light |
|---|---|---|---|
| Solid fill (primary button, active marker) | `--primary` / `--ae-accent` | `#b8f26b` | `#7fc41e` |
| Fill hover | `--primary-hover` / `--ae-accent-hover` | `#c8f78a` | `#6fb015` |
| Text on the fill | `--primary-foreground` | `#0b0d0d` | `#0b0d0d` |
| Lime used as **text/icon** (links, eyebrows, indices) | `--ae-accent-text` | `#b8f26b` | `#3b6a07` |
| Focus ring | `--ring` | `#b8f26b` | `#3b6a07` |
| Soft tint (selected row, highlight) | `--primary-soft` | 12 % lime over bg | 20 % lime over bg |

In light mode the lime fill is darkened (`#7fc41e`) but still carries *dark* text, matching the screenshots' lime buttons with black labels. Lime must never be used as text on a light surface (2.0:1); use `--ae-accent-text`.

Hover is *lighter* in dark and *darker* in light, so the change is always visible.

### 2.3 Status

| Meaning | Token | Dark | Light | Used for |
|---|---|---|---|---|
| Success | `--success` | `#3fcf8e` | `#157a45` | running, healthy, succeeded, online |
| Warning | `--warning` | `#f5b942` | `#8a5a00` | degraded, deploying slowly, high usage, stderr in logs |
| Danger | `--danger` (= `--destructive`) | `#ff6b6b` | `#c4302b` | failed, offline, unhealthy, destructive actions |
| Info | `--info` | `#5cc8e8` | `#0b6f8a` | building, queued-active, hints |
| Neutral | `--muted-foreground` | | | stopped, queued, unknown |

Each has `-foreground` (text on a solid fill) and `-soft` (tinted badge/banner background; the label stays the status colour). Domain mapping:

| Domain state | Tone | Dot |
|---|---|---|
| Application/Container running + healthy | success | pulse |
| Deploying / building | info | pulse |
| Queued | neutral | static |
| Failed / Agent unavailable / Server unavailable | danger | static |
| Docker unavailable / unhealthy / degraded | warning | static |
| Stopped / Cancelled | neutral | static (hollow) |

### 2.4 Chart series

Eight series, in order of use. Lime is series 1 so single-series charts match the brand. Light values are darkened for >= 3:1 on the page and surface.

| # | Dark | Light | Hue |
|---|---|---|---|
| 1 | `#b8f26b` | `#5b9b0b` | lime |
| 2 | `#5cc8e8` | `#0e86a6` | cyan |
| 3 | `#f5b942` | `#b06f00` | amber |
| 4 | `#a78bfa` | `#7156d6` | violet |
| 5 | `#f472b6` | `#c2347e` | pink |
| 6 | `#2dd4bf` | `#0f8f7d` | teal |
| 7 | `#fb923c` | `#c2540a` | orange |
| 8 | `#94a3b8` | `#5f6f82` | slate |

Charts: 1.5px lines, no area fill or <= 12 % fill, grid in `--ae-chart-grid`, axis labels mono 11px `--ae-chart-axis`. Multi-series charts must distinguish series by more than hue (direct labels, or alternate dash patterns for series 5-8). CPU/RAM/disk gauges use series 1, switch to `--warning` at >= 80 % and `--danger` at >= 92 %.

### 2.5 Contrast (WCAG 2.2, computed)

Ratios were computed with the WCAG relative-luminance formula; any checker reproduces them. Text needs >= 4.5:1 (AA), UI components and graphics >= 3:1.

**Dark**

| Pair | Ratio | Req. |
|---|---|---|
| `foreground` on `background` / `card` / `popover` | 15.8 / 15.0 / 14.3 | 4.5 ok |
| `muted-foreground` on `background` / `card` / `popover` | 6.05 / 5.75 / 5.45 | 4.5 ok |
| `muted-foreground` on `--accent` hover surface | 5.07 | 4.5 ok |
| `primary` (lime text) on `background` / `card` / `popover` | 14.8 / 14.1 / 13.3 | 4.5 ok |
| `primary-foreground` on `primary` / `primary-hover` | 14.8 / 15.9 | 4.5 ok |
| `success` / `warning` / `danger` / `info` on `background` | 9.8 / 11.0 / 7.0 / 10.1 | 4.5 ok |
| same on `card` | 9.3 / 10.5 / 6.7 / 9.6 | 4.5 ok |
| status text on its own `-soft` tint | 7.8 / 8.6 / 5.9 / 8.0 | 4.5 ok |
| `destructive-foreground` on `destructive` | 7.0 | 4.5 ok |
| `--input` border on `background` / `card` | 3.5 / 3.3 | 3 ok |
| chart series 1-8 on `background` | 14.8 / 10.1 / 11.0 / 7.2 / 7.4 / 10.5 / 8.6 / 7.6 | 3 ok |
| `--border` hairline on `background` | 1.25 | decorative, exempt |

**Light**

| Pair | Ratio | Req. |
|---|---|---|
| `foreground` on `background` / `card` / `popover` | 17.3 / 17.9 / 18.6 | 4.5 ok |
| `muted-foreground` on `background` / `card` / `popover` | 6.3 / 6.6 / 6.8 | 4.5 ok |
| `muted-foreground` on `--muted` / `--accent` / `--sidebar` | 5.85 / 5.69 / 6.08 | 4.5 ok |
| `ae-accent-text` (dark lime text) on `background` / `card` / `popover` | 6.0 / 6.2 / 6.5 | 4.5 ok |
| `ae-accent-text` on `primary-soft` | 5.25 | 4.5 ok |
| `primary-foreground` on `primary` / `primary-hover` | 9.1 / 7.3 | 4.5 ok |
| `success` / `warning` / `danger` / `info` on `background` | 5.0 / 5.5 / 5.1 / 5.3 | 4.5 ok |
| same on `popover` (white) | 5.4 / 5.9 / 5.5 / 5.8 | 4.5 ok |
| status text on its own `-soft` tint (6 % mix) | 4.6 / 5.1 / 4.7 / 4.9 | 4.5 ok |
| `destructive-foreground` (white) on `destructive` | 5.5 | 4.5 ok |
| `--input` border on `background` / `card` / `popover` | 3.5 / 3.6 / 3.8 | 3 ok |
| `--ring` on `background` | 6.0 | 3 ok |
| chart series 1-8 on `background` | 3.2 / 3.9 / 3.8 / 4.9 / 4.8 / 3.7 / 4.3 / 4.8 | 3 ok |
| raw lime `#7fc41e` as **text** on `background` | 2.0 | FAIL, never do this |

Soft-tint ratios use an sRGB approximation of the `color-mix(in oklab, ...)` result; re-check if the mix percentage is changed. Re-run the check whenever a colour token changes; WP1.4 should add an automated test that parses `tokens.css` and asserts the pairs above.

## 3. Typography

Fonts: **Geist Sans** (UI, headings) and **Geist Mono** (system voice, code, logs), self-hosted via `next/font` (`geist` package) so the static export has no runtime font requests. Fallback stacks are in `--font-sans` / `--font-mono`.

| Token | Size | Use |
|---|---|---|
| `--ae-text-2xs` | 11px | mono eyebrows, `01` indices, kbd, table column labels |
| `--ae-text-xs` | 12px | captions, meta, log lines, status bar |
| `--ae-text-sm` | 13px | dense controls, tables, sidebar items |
| `--ae-text-base` | 14px | **default body** |
| `--ae-text-md` | 16px | marketing/login body, inputs on touch |
| `--ae-text-lg` / `xl` | 18 / 20px | card titles, section heads |
| `--ae-text-2xl` / `3xl` | 24 / 32px | page titles |
| `--ae-text-4xl` | 48px | empty-state / login headline |
| `--ae-text-display` | clamp(40px, 6vw, 72px) | hero, boot/login only |

Rules:
- Headings: weight 500, tight tracking: display `-0.05em` (line-height 1.05), page titles `-0.03em` (1.2), card titles `-0.015em`. Body and UI text tracking 0.
- Eyebrow: Geist Mono, 11px, uppercase, `letter-spacing: 0.14em`, lime (`--ae-accent-text`), preceded by a 24px hairline: `-- DEPLOYMENTS`. A `--muted` variant exists for secondary eyebrows.
- A heading may end in one accented phrase in lime (`Make the invisible <em>operational.</em>`). Use at most once per screen, only on marketing-style surfaces (login, empty states, dashboard hero), never on dense data pages.
- Numeric data in tables and metrics uses `font-variant-numeric: tabular-nums`.
- Body line-height 1.5; tables and logs 1.4.
- Minimum text size: 11px, and only for mono uppercase labels; nothing else below 12px.

## 4. Spacing, radius, borders, elevation

- **Spacing**: 4px base: 0, 2, 4, 8, 12, 16, 20, 24, 32, 40, 48, 64, 96 (`--ae-space-*`). Page content padding 24px (16px under 640px); card padding 16-24px; dense table cell padding 8px 12px.
- **Radius**: `--radius: 2px`. Scale: none 0, sm 0, md 1px, lg 2px. Full circles only for dots/avatars. shadcn derives `--radius-sm: calc(var(--radius) - 4px)`, which would be negative, so override the derived radii explicitly in the Tailwind theme (see section 8).
- **Borders**: 1px solid `--border` for everything (cards, inputs, table rules, panel separators). `--ae-border-strong` for hover. The active nav marker and focus ring are 2px.
- **Elevation**: none by default. Popovers/menus/palette get `--ae-shadow-popover` plus a 1px border. The only "depth" motif is the offset plate (4.4).
- **Focus**: `box-shadow: 0 0 0 2px var(--background), 0 0 0 4px var(--ring)` (`--ae-focus-ring`) on `:focus-visible`; never remove outlines without replacement.
- **Hit targets**: 32px minimum height for dense desktop controls, 44px on touch (`@media (pointer: coarse)`).

## 5. Motion

| Token | Value | Use |
|---|---|---|
| `--ae-duration-instant` | 80ms | press feedback |
| `--ae-duration-fast` | 120ms | hover, colour changes |
| `--ae-duration-base` | 180ms | menus, tabs, toggles, tooltips |
| `--ae-duration-slow` | 280ms | sidebar collapse, drawers, palette |
| `--ae-duration-boot` | 420ms | stagger between boot lines |
| `--ae-ease-out` | `cubic-bezier(.16,1,.3,1)` | entering elements (default) |
| `--ae-ease-in-out` | `cubic-bezier(.65,0,.35,1)` | moving/resizing |
| `--ae-ease-in` | `cubic-bezier(.7,0,.84,0)` | leaving elements |

- Animate `opacity` and `transform` only (plus colour); never animate layout properties.
- Motion is functional: state change, entry, live status. No decorative looping animation except the pulse dot and the boot cursor.
- **Reduced motion** (`prefers-reduced-motion: reduce`): all durations collapse to ~0, the pulse ring and cursor blink stop (the static dot and a solid cursor remain), the boot screen renders its final state immediately, and the dotted grid never animates. Live charts still update, without easing.
- Live data (metrics, logs) updates in place with no entry animation.

## 6. Motifs

All have working CSS in `tokens.css` (section 5 of that file). Class names are the contract; components may wrap them.

### 6.1 Dotted-grid canvas (`.ae-grid-bg`)

```css
.ae-grid-bg {
  background-color: var(--background);
  background-image: radial-gradient(circle at center,
    var(--ae-grid-dot) 1px, transparent 1.5px);
  background-size: 24px 24px;       /* --ae-grid-size */
  background-position: center top;
}
```

`--ae-grid-dot` is `rgb(255 255 255 / .07)` dark, `rgb(15 20 18 / .10)` light: visible but never competing with content. Used on the main content canvas, the login/boot screen and empty states; **not** inside tables or forms. Add `.ae-grid-bg--fade` on an overlay to mask the grid toward the edges for hero areas. The optional "particle emission" feel from `01_UI.md`: a handful (<= 12) of 2px lime dots drifting upward at 20-40s per cycle on the login screen only, `opacity <= .5`, disabled under reduced motion; purely decorative, rendered with CSS animation, no canvas.

### 6.2 Dashed pipeline connector (`.ae-pipeline`)

The deployment lifecycle stepper (Source, Build, Image, Target Server, Container, Network, Domain, Health Check, Running), modelled on "the operating loop": a vertical list, mono index `01..09` in lime, title in sans, one line of mono detail under it, and a `1px dashed var(--ae-connector)` line joining the indices.

```html
<ol class="ae-pipeline">
  <li class="ae-pipeline__step" data-state="done">
    <span class="ae-pipeline__index">01</span>
    <div><div class="ae-pipeline__title">Source</div>
         <div class="ae-pipeline__meta">github.com/acme/api @ 3f9c2d1</div></div>
  </li>
  ...
</ol>
```

`data-state`: `pending | active | done | failed | skipped`. Active = title in lime + pulse dot; failed = index and title in `--danger` and the connector below turns solid red-dashed; skipped = muted. The failed step expands to show the failure reason and the last log lines (spec section 40).

### 6.3 Terminal-window card (`.ae-terminal`)

Header bar 40px: three 6px dots (left), centred mono title `aethera://boot`, `01` index right-aligned; body is mono 12px with `line-height: 2`. An **offset plate** (the card's silhouette in `--ae-plate`, translated 18px right/down, behind the card) is the signature depth cue. Use for: boot screen, install commands ("copy this join command"), live build logs, CLI examples, empty states. Not for ordinary data cards.

### 6.4 Numbered card (`.ae-numbered`)

Square, 1px border, no fill, `01` in lime mono at the top-left, a 16-18px title and a 12-13px muted description at the bottom. Adjacent cards share borders (the double line collapses). Used for dashboard overview tiles ("01 Servers", "02 Applications") and quick-start suggestions (as in the Chord empty state).

### 6.5 Pulse status dot (`.ae-pulse`)

8px dot; a ring scales 1 to 2.8x and fades over 2s (`ae-pulse` keyframes), colour from `data-tone` (`success | warning | danger | info | idle`). Only for *live* states (running, deploying, connected); static states use a plain dot, stopped uses a hollow dot. Never more than ~20 pulsing dots per screen (list rows pulse only for the first visible page).

### 6.6 Boot-screen lines (`.ae-boot-line`)

```
[ ok ] connecting to control plane
[ ok ] loading projects
[ ok ] syncing server status
▮ ready
```

`[ ok ]` is lime semibold mono; text is `--foreground`; lines appear 420ms apart (`--i` custom property gives the index), then the blinking block cursor and `ready`. `[fail]` (red) and `[ .. ]` (muted) variants exist. The boot screen shows once per browser session after login (flag in `sessionStorage`), lasts at most ~1.5s, is skippable with any key/click, and is skipped entirely under reduced motion. It reflects real work (auth check, bootstrap queries); if the data arrives earlier it cuts short.

### 6.7 Other recurring pieces

- **Keycap** (`.ae-kbd`): mono 11px, 1px border, muted. Always shown beside shortcuts (`Ctrl K`).
- **Log viewer** (`.ae-log`): mono 12px, stderr in `--warning`, muted timestamps, search matches in `<mark>`.
- **Offset CTA pair**: primary lime button with a trailing `↗` for external links, next to an outlined secondary button with `1px` border (as in the screenshots).
- **Solid lime band**: a full-width `--primary` section with dark text, used at most once on a page, for a single high-emphasis message (login side panel, "Aethera is up to date"). Not for in-app layout.

## 7. Shell layout

```
+----+------------------+---------------------------------------------+
|    | [ Search or run command...            Ctrl K ]     (?) (o) (*)  | 40px top bar
| R  +------------------+---------------------------------------------+
| A  | SIDEBAR 264px    |                                             |
| I  |  section title   |   CONTENT  (.ae-grid-bg canvas)             |
| L  |  tree / list     |   max-width 1160px, padding 24px           |
|    |                  |                                             |
| 48 |  [bottom: User]  |                                             |
+----+------------------+---------------------------------------------+
| (*) Connected  .  3 servers  .  1 job running           v0.1.0  ... | 24px status bar
+--------------------------------------------------------------------+
```

Tokens: `--ae-rail-width 48px`, `--ae-sidebar-width 264px`, `--ae-topbar-height 40px`, `--ae-statusbar-height 24px`, `--ae-content-max 1160px`.

**Icon rail** (48px, `--sidebar` bg, 1px right border). One icon button (20px glyph, 32px hit area, tooltip with name and shortcut) per top-level section from spec section 37: Dashboard, Projects, Applications, Services, Servers, Deployments, Domains, Registries, Secrets, Monitoring; Settings and the user menu are pinned to the bottom. The active section has a 2px lime bar on the rail's left edge (as in the Chord rail) and a lime icon; hover uses `--sidebar-accent`. Icon set: lucide, 1.5px stroke. Optional count badges (failed deployments) use a 6px danger dot, not a number.

**Sidebar** (264px, collapsible to 0 with `Ctrl B`, state persisted per user in `localStorage` and wrapped in try/catch). Content is contextual to the rail selection: Projects shows the project/environment tree; Servers shows the server list with status dots; Applications shows applications grouped by project. Header row: mono uppercase section label (`PROJECTS`) and a lime "+ New" button (the lime full-width "New Chat" button pattern, but square and 32px). Below it a filter input. Items: 32px rows, 13px text, status dot at left, active row `--sidebar-accent` plus 2px lime left marker. Under 1024px the sidebar becomes an overlay drawer; under 768px the rail moves into the drawer and a hamburger appears in the top bar.

**Top bar** (40px, 1px bottom border). Centre: the command-palette trigger styled as a search input (`Search or run command...` and a `Ctrl K` keycap), max 560px. Left: breadcrumbs (mono, `/`-separated) on wide screens. Right: help, notifications (later), theme toggle (sun/moon, light/dark/system menu).

**Command palette** (`Ctrl K`, also `/` when not typing). Modal over `--ae-overlay`, 560px wide, anchored 15vh from the top, `--popover` bg with 1px border, no radius. Input at the top, grouped results below: *Navigate* (go to any section/project/app/server), *Actions* (Deploy `<app>`, Restart `<app>`, Add server, Create project, Toggle theme), *Recent*. Items show mono hints and keycaps; `Up/Down` navigate, `Enter` runs, `Esc` closes; typing `>` limits to actions. Destructive actions are never directly executable from the palette: they open the usual confirmation dialog. Implement with shadcn `Command` (cmdk).

**Status bar** (24px, mono 12px, 1px top border, `--sidebar` bg). Left: a pulse dot and connection summary `Connected . 3 servers . 1 job running`; dot tone: success = SignalR connected, warning = reconnecting, danger = control plane unreachable (this is the "Control plane unavailable" axis of spec section 44, observed from the browser). Clicking the job segment opens the jobs popover. Right: Aethera version, update-available indicator, active environment. Server-level problems (agent/Docker/server unavailable) appear as counts here (`2 degraded`) and in the sidebar, not as a dot colour for the whole bar.

**Content**: `.ae-grid-bg` canvas; pages use a header (eyebrow, title, description, primary action at right) then sections. Tables are the default for lists (dense: 36px rows, sticky header, mono for IDs/hashes). Max content width 1160px centred on large screens; data-heavy pages (logs, monitoring) may go full-width.

**Responsive**: >= 1280 full shell; 1024-1279 sidebar collapsed by default; 768-1023 rail + drawer sidebar; < 768 single column, rail moves into the drawer, tables become stacked rows, status bar condenses to the dot and one count.

**Keyboard**: `Ctrl K` palette, `Ctrl B` sidebar, `g d / g p / g a / g s` go to dashboard/projects/applications/servers (two-key chords, disabled while typing), `?` shortcut help, `Esc` closes any overlay. Everything reachable by keyboard; the focus ring is always visible.

**Accessibility**: landmarks (`nav` rail, `nav` sidebar, `main`, `footer` status bar with `role="status"` for the connection text, `aria-live="polite"`), skip-to-content link, dialogs trap focus, tooltips never carry essential information.

## 8. Using the tokens

Import `tokens.css` first in the global stylesheet. With **Tailwind CSS v4** and shadcn/ui, map the variables in an `@theme inline` block (copy-paste, then add the `--ae-*` extras you use):

```css
@import "tailwindcss";
@import "./tokens.css";          /* copied to src/web/src/styles/tokens.css */

@theme inline {
  --color-background: var(--background);
  --color-foreground: var(--foreground);
  --color-card: var(--card);
  --color-card-foreground: var(--card-foreground);
  --color-popover: var(--popover);
  --color-popover-foreground: var(--popover-foreground);
  --color-primary: var(--primary);
  --color-primary-foreground: var(--primary-foreground);
  --color-secondary: var(--secondary);
  --color-secondary-foreground: var(--secondary-foreground);
  --color-muted: var(--muted);
  --color-muted-foreground: var(--muted-foreground);
  --color-accent: var(--accent);
  --color-accent-foreground: var(--accent-foreground);
  --color-destructive: var(--destructive);
  --color-border: var(--border);
  --color-input: var(--input);
  --color-ring: var(--ring);
  --color-success: var(--success);
  --color-warning: var(--warning);
  --color-info: var(--info);
  --color-sidebar: var(--sidebar);
  --color-sidebar-border: var(--sidebar-border);
  --color-chart-1: var(--chart-1); /* ... through --chart-8 */

  --font-sans: var(--font-sans);
  --font-mono: var(--font-mono);

  /* Square corners: do NOT derive from --radius with calc() */
  --radius-sm: 0;
  --radius-md: 1px;
  --radius-lg: 2px;
  --radius-xl: 2px;
}
```

For Tailwind v3, put the same names under `theme.extend.colors` as `var(--...)` references. Rules for component authors:

- Use semantic tokens (`bg-card`, `text-muted-foreground`, `border-border`); never hard-code hex or Tailwind palette colours in components.
- For lime *text* use `text-[var(--ae-accent-text)]`, never `text-primary` (fails contrast in light mode).
- Status via `--success|warning|danger|info` and their `-soft` variants.
- Theme switch: set `data-theme` on `<html>` from an inline pre-hydration script that reads a cookie or `localStorage` (wrapped in try/catch, falling back to dark) to avoid a flash.
- A `/design` route (dev only, WP1.4) renders every token, motif and shell piece in both themes; changes to this file are reviewed against it.

## 9. Change policy

Tokens are a contract like the proto and the OpenAPI document: renaming or removing a token requires an orchestrator-approved change; adding tokens is free. Colour changes must re-run the contrast checks in section 2.5.
