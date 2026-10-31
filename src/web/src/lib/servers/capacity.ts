import { formatBytes, ratioPercent, toNum } from "./format";
import type { ServerCapacity } from "./types";

/** One meter of the capacity view: a share of the server (0..100, null when unknown) and the text behind it. */
export type CapacityMeter = { label: string; percent: number | null; detail: string };

function cores(n: number | null): string {
  return n === null ? "-" : `${Number.isInteger(n) ? n : n.toFixed(2).replace(/0+$/, "")} cores`;
}

/**
 * The meters of a server: live usage (from the latest sample) and what is committed to workloads (their reservations, else their
 * limits). Only formats what the API returned; a share it cannot compute is null and the meter shows "no data".
 */
export function capacityMeters(c: ServerCapacity): { usage: CapacityMeter[]; committed: CapacityMeter[] } {
  const usage: CapacityMeter[] = [];
  const u = c.usage;
  usage.push({
    label: "CPU",
    percent: toNum(u?.cpuPercent),
    detail: u ? `${Math.round(toNum(u.cpuPercent) ?? 0)}% of ${cores(toNum(c.cores))} in use` : "no sample yet",
  });
  usage.push({
    label: "MEM",
    percent: ratioPercent(u?.memoryUsedBytes, u?.memoryTotalBytes),
    detail: u ? `${formatBytes(u.memoryUsedBytes)} of ${formatBytes(u.memoryTotalBytes)} in use` : "no sample yet",
  });
  usage.push({
    label: "DSK",
    percent: ratioPercent(u?.diskUsedBytes, u?.diskTotalBytes),
    detail: u ? `${formatBytes(u.diskUsedBytes)} of ${formatBytes(u.diskTotalBytes)} used` : "no sample yet",
  });

  const committed: CapacityMeter[] = [
    {
      label: "CPU",
      percent: toNum(c.cores) ? ((toNum(c.committed.cpuCores) ?? 0) / (toNum(c.cores) as number)) * 100 : null,
      detail: `${cores(toNum(c.committed.cpuCores))} reserved of ${cores(toNum(c.cores))}`,
    },
    {
      label: "MEM",
      percent: ratioPercent(c.committed.memoryBytes, c.memoryBytes),
      detail: `${formatBytes(c.committed.memoryBytes)} reserved of ${formatBytes(c.memoryBytes)}`,
    },
  ];
  return { usage, committed };
}

/** What is left on the server for one workload, as text: "2.5 GiB memory and 1.5 cores unreserved". */
export function describeFree(c: ServerCapacity): string {
  const memory = toNum(c.free.memoryBytes);
  const cpu = toNum(c.free.cpuCores);
  const parts: string[] = [];
  if (memory !== null) parts.push(`${formatBytes(Math.max(0, memory))} memory`);
  if (cpu !== null) parts.push(cores(Math.max(0, cpu)));
  return parts.length === 0 ? "size not known yet" : `${parts.join(" and ")} unreserved`;
}

/**
 * Would a workload asking for this much still fit? Reservations of a workload that is already placed there must be left out by the
 * caller (`workloadReserved*`). Returns the warning, or null when it fits or the server's size is unknown.
 */
export function overCommitWarning(
  c: ServerCapacity,
  ask: { cpuCores?: number | null; memoryBytes?: number | null },
  workloadReserved: { cpuCores?: number; memoryBytes?: number } = {},
): string | null {
  const memoryFree = toNum(c.free.memoryBytes);
  const cpuFree = toNum(c.free.cpuCores);
  const memory = (memoryFree ?? 0) + (workloadReserved.memoryBytes ?? 0);
  const cpu = (cpuFree ?? 0) + (workloadReserved.cpuCores ?? 0);
  if (ask.memoryBytes && memoryFree !== null && ask.memoryBytes > memory)
    return `${c.name} has ${formatBytes(Math.max(0, memory))} of memory unreserved and this asks for ${formatBytes(ask.memoryBytes)}: it would be over-committed.`;
  if (ask.cpuCores && cpuFree !== null && ask.cpuCores > cpu)
    return `${c.name} has ${cores(Math.max(0, cpu))} unreserved and this asks for ${cores(ask.cpuCores)}: it would be over-committed.`;
  return null;
}
