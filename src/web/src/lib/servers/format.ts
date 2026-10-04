/** Pure formatting helpers. No derivation of API state lives here. */

export function toNum(v: unknown): number | null {
  if (v === null || v === undefined || v === "") return null;
  const n = typeof v === "number" ? v : Number(v);
  return Number.isFinite(n) ? n : null;
}

export function toDate(v: unknown): Date | null {
  if (typeof v !== "string" && typeof v !== "number" && !(v instanceof Date)) return null;
  const d = new Date(v);
  return Number.isNaN(d.getTime()) ? null : d;
}

const UNITS = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];

export function formatBytes(value: unknown, digits = 1): string {
  const n = toNum(value);
  if (n === null) return "-";
  if (n === 0) return "0 B";
  const i = Math.min(UNITS.length - 1, Math.max(0, Math.floor(Math.log(Math.abs(n)) / Math.log(1024))));
  const v = n / 1024 ** i;
  return `${i === 0 ? v : v.toFixed(v >= 100 ? 0 : digits)} ${UNITS[i]}`;
}

export function formatRate(bytesPerSecond: unknown): string {
  const n = toNum(bytesPerSecond);
  return n === null ? "-" : `${formatBytes(n)}/s`;
}

export function formatPercent(value: unknown, digits = 0): string {
  const n = toNum(value);
  return n === null ? "-" : `${n.toFixed(digits)}%`;
}

/** `5s`, `12m`, `3h`, `2d`. */
export function formatAge(from: unknown, now: Date = new Date()): string {
  const d = toDate(from);
  if (!d) return "-";
  const s = Math.max(0, Math.round((now.getTime() - d.getTime()) / 1000));
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m`;
  const h = Math.floor(m / 60);
  if (h < 48) return `${h}h`;
  return `${Math.floor(h / 24)}d`;
}

/** `12m ago`, or `-` when there is no timestamp. */
export function formatAgo(from: unknown, now: Date = new Date()): string {
  const d = toDate(from);
  return d ? `${formatAge(d, now)} ago` : "-";
}

/** `m:ss` / `h:mm:ss` until `expiresAt`; `0:00` once passed. */
export function formatCountdown(expiresAt: unknown, now: Date = new Date()): string {
  const d = toDate(expiresAt);
  if (!d) return "-";
  const total = Math.max(0, Math.ceil((d.getTime() - now.getTime()) / 1000));
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const ss = String(total % 60).padStart(2, "0");
  return h > 0 ? `${h}:${String(m).padStart(2, "0")}:${ss}` : `${m}:${ss}`;
}

export function formatDateTime(v: unknown): string {
  const d = toDate(v);
  return d ? d.toLocaleString(undefined, { dateStyle: "medium", timeStyle: "medium" }) : "-";
}

/** Percentage 0..100 of used/total, or null if either is unknown or total is 0. */
export function ratioPercent(used: unknown, total: unknown): number | null {
  const u = toNum(used);
  const t = toNum(total);
  if (u === null || t === null || t <= 0) return null;
  return Math.min(100, Math.max(0, (u / t) * 100));
}
