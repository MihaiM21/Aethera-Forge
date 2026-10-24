/** Schedule helpers: presets and a plain-words description for the cron expressions the presets produce. */

export type SchedulePreset = { key: string; label: string; cron: string };

export const PRESETS: readonly SchedulePreset[] = [
  { key: "minute", label: "Every minute", cron: "* * * * *" },
  { key: "hourly", label: "Hourly", cron: "0 * * * *" },
  { key: "daily", label: "Daily at 03:00", cron: "0 3 * * *" },
  { key: "weekly", label: "Weekly (Sunday 03:00)", cron: "0 3 * * 0" },
];

export const CUSTOM = "custom";

/** The preset whose expression this is, or `custom`. */
export function presetKeyOf(cron: string): string {
  const normalized = cron.trim().replace(/\s+/g, " ");
  return PRESETS.find((p) => p.cron === normalized)?.key ?? CUSTOM;
}

const DAYS = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

const pad = (n: string) => n.padStart(2, "0");

/**
 * The schedule in words for the common shapes (every N minutes, hourly, daily, weekly, monthly); the cron expression itself otherwise.
 * Never wrong: anything it does not recognise is returned unchanged.
 */
export function describeSchedule(cron: string): string {
  const parts = cron.trim().split(/\s+/);
  if (cron.trim().startsWith("@")) return { "@hourly": "Every hour", "@daily": "Every day at 00:00", "@midnight": "Every day at 00:00", "@weekly": "Every Sunday at 00:00", "@monthly": "On the 1st of every month at 00:00", "@yearly": "Every year on January 1st", "@annually": "Every year on January 1st" }[cron.trim()] ?? cron;
  if (parts.length !== 5) return cron;
  const [min, hour, dom, month, dow] = parts;
  const num = /^\d+$/;
  if (month !== "*") return cron;
  if (min === "*" && hour === "*" && dom === "*" && dow === "*") return "Every minute";
  const step = /^\*\/(\d+)$/.exec(min);
  if (step && hour === "*" && dom === "*" && dow === "*") return `Every ${step[1]} minutes`;
  if (num.test(min) && hour === "*" && dom === "*" && dow === "*") return min === "0" ? "Every hour" : `Every hour at minute ${Number(min)}`;
  if (num.test(min) && num.test(hour)) {
    const at = `${pad(hour)}:${pad(min)}`;
    if (dom === "*" && dow === "*") return `Every day at ${at}`;
    if (dom === "*" && num.test(dow) && Number(dow) <= 7) return `Every ${DAYS[Number(dow) % 7]} at ${at}`;
    if (num.test(dom) && dow === "*") return `On day ${Number(dom)} of every month at ${at}`;
  }
  return cron;
}

/** The browser's IANA time zone, `UTC` when it cannot be told. */
export function browserTimeZone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC";
  } catch {
    return "UTC";
  }
}

/** `1 h`, `90 s`, `5 min`: the timeout as people say it. */
export function formatTimeout(seconds: number): string {
  if (seconds % 3600 === 0) return `${seconds / 3600} h`;
  if (seconds % 60 === 0) return `${seconds / 60} min`;
  return `${seconds} s`;
}
