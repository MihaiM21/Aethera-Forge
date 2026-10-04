"use client";

import * as React from "react";
import { ArrowDownToLineIcon, DownloadIcon, SearchIcon } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { cn } from "@/lib/utils";

export type LogChunk = {
  /** Strictly increasing per stream; used as the React key and for de-duplication. */
  id: number;
  text: string;
  stream?: "stdout" | "stderr";
  timestamp?: string;
};

export type LogRow = { key: string; n: number; text: string; stream: "stdout" | "stderr"; timestamp?: string };

/**
 * Chunks -> display lines. A chunk is whatever the agent flushed, so a line may span chunks; consecutive chunks of
 * the same stream are joined before splitting, and a trailing partial line stays visible.
 */
export function chunksToRows(chunks: LogChunk[]): LogRow[] {
  const rows: LogRow[] = [];
  let carry = "";
  let carryStream: "stdout" | "stderr" = "stdout";
  let carryId = 0;
  let carryTs: string | undefined;
  const push = (text: string, stream: "stdout" | "stderr", id: number, ts?: string) =>
    rows.push({ key: `${id}:${rows.length}`, n: rows.length + 1, text: text.replace(/\r$/, ""), stream, timestamp: ts });
  for (const c of chunks) {
    const stream = c.stream ?? "stdout";
    if (carry && stream !== carryStream) {
      push(carry, carryStream, carryId, carryTs);
      carry = "";
    }
    if (!carry) {
      carryStream = stream;
      carryId = c.id;
      carryTs = c.timestamp;
    }
    const parts = (carry + c.text).split("\n");
    carry = parts.pop() ?? "";
    for (const p of parts) push(p, stream, carryId, carryTs);
    carryId = c.id;
    carryTs = c.timestamp;
  }
  if (carry) push(carry, carryStream, carryId, carryTs);
  return rows;
}

export type LogFilter = { query: string; stderrOnly: boolean; onlyMatches: boolean };

export function filterRows(rows: LogRow[], f: LogFilter): LogRow[] {
  const q = f.query.trim().toLowerCase();
  return rows.filter((r) => {
    if (f.stderrOnly && r.stream !== "stderr") return false;
    if (q && f.onlyMatches && !r.text.toLowerCase().includes(q)) return false;
    return true;
  });
}

function Highlight({ text, query }: { text: string; query: string }) {
  const q = query.trim();
  if (!q) return <>{text}</>;
  const lower = text.toLowerCase();
  const needle = q.toLowerCase();
  const out: React.ReactNode[] = [];
  let from = 0;
  for (let i = lower.indexOf(needle); i !== -1; i = lower.indexOf(needle, from)) {
    if (i > from) out.push(text.slice(from, i));
    out.push(<mark key={i}>{text.slice(i, i + needle.length)}</mark>);
    from = i + needle.length;
  }
  out.push(text.slice(from));
  return <>{out}</>;
}

/**
 * Dense mono log view with search, a stderr filter, follow-the-tail and a download link. It only renders what it is
 * given; fetching (polling, live stream) belongs to the caller.
 */
export function LogViewer({
  chunks,
  status,
  downloadHref,
  downloadName,
  emptyText = "No log output yet.",
  maxHeight = "28rem",
  className,
  label = "Log output",
  showTimestamps = false,
}: {
  chunks: LogChunk[];
  /** Shown in the toolbar, e.g. `live`, `ended`. */
  status?: React.ReactNode;
  downloadHref?: string;
  downloadName?: string;
  emptyText?: string;
  maxHeight?: string;
  className?: string;
  label?: string;
  showTimestamps?: boolean;
}) {
  const [query, setQuery] = React.useState("");
  const [stderrOnly, setStderrOnly] = React.useState(false);
  const [onlyMatches, setOnlyMatches] = React.useState(true);
  const [follow, setFollow] = React.useState(true);
  const boxRef = React.useRef<HTMLDivElement>(null);

  const rows = React.useMemo(() => chunksToRows(chunks), [chunks]);
  const visible = React.useMemo(() => filterRows(rows, { query, stderrOnly, onlyMatches }), [rows, query, stderrOnly, onlyMatches]);
  const matches = React.useMemo(() => {
    const q = query.trim().toLowerCase();
    return q ? rows.filter((r) => r.text.toLowerCase().includes(q)).length : 0;
  }, [rows, query]);

  React.useEffect(() => {
    const el = boxRef.current;
    if (follow && el) el.scrollTop = el.scrollHeight;
  }, [visible.length, follow]);

  function onScroll() {
    const el = boxRef.current;
    if (!el) return;
    const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 24;
    setFollow((f) => (f === atBottom ? f : atBottom));
  }

  return (
    <div className={cn("border border-border bg-[var(--ae-terminal-bg)]", className)}>
      <div className="flex flex-wrap items-center gap-2 border-b border-border px-3 py-2">
        <div className="relative min-w-40 flex-1 sm:max-w-xs">
          <label htmlFor="log-search" className="sr-only">
            Search the log
          </label>
          <SearchIcon className="pointer-events-none absolute top-1/2 left-2 size-3.5 -translate-y-1/2 text-muted-foreground" aria-hidden="true" />
          <Input id="log-search" type="search" placeholder="Search…" value={query} onChange={(e) => setQuery(e.target.value)} className="h-7 pl-7 text-xs" />
        </div>
        {query.trim() && (
          <span className="font-mono text-2xs text-muted-foreground" aria-live="polite">
            {matches} {matches === 1 ? "match" : "matches"}
          </span>
        )}
        <label className="flex items-center gap-1.5 font-mono text-2xs text-muted-foreground">
          <input type="checkbox" checked={onlyMatches} onChange={(e) => setOnlyMatches(e.target.checked)} /> only matches
        </label>
        <label className="flex items-center gap-1.5 font-mono text-2xs text-muted-foreground">
          <input type="checkbox" checked={stderrOnly} onChange={(e) => setStderrOnly(e.target.checked)} /> stderr only
        </label>
        <span className="ml-auto flex items-center gap-2">
          {status && <span className="font-mono text-2xs text-muted-foreground">{status}</span>}
          {!follow && (
            <Button size="sm" variant="outline" onClick={() => setFollow(true)}>
              <ArrowDownToLineIcon aria-hidden="true" /> Follow
            </Button>
          )}
          {downloadHref && (
            <Button size="sm" variant="outline" asChild>
              <a href={downloadHref} download={downloadName}>
                <DownloadIcon aria-hidden="true" /> Download
              </a>
            </Button>
          )}
        </span>
      </div>
      <div
        ref={boxRef}
        onScroll={onScroll}
        role="log"
        aria-label={label}
        tabIndex={0}
        className="ae-log focus-ring overflow-auto px-3 py-2"
        style={{ maxHeight }}
      >
        {visible.length === 0 ? (
          <p className="py-6 text-center text-muted-foreground">{rows.length === 0 ? emptyText : "No lines match the filter."}</p>
        ) : (
          visible.map((r) => (
            <div key={r.key} data-stream={r.stream} className="flex gap-3">
              <span className="w-10 shrink-0 text-right text-muted-foreground select-none">{r.n}</span>
              {showTimestamps && r.timestamp && <span className="ae-log__ts shrink-0">{new Date(r.timestamp).toLocaleTimeString()}</span>}
              <span className="min-w-0 flex-1">
                <Highlight text={r.text} query={query} />
              </span>
            </div>
          ))
        )}
      </div>
    </div>
  );
}
