import * as React from "react";
import { cn } from "@/lib/utils";

/** Deterministic so server and client render the same markup. */
const PARTICLES = [
  { left: 6, d: 26, delay: 0 },
  { left: 14, d: 34, delay: 9 },
  { left: 23, d: 29, delay: 3 },
  { left: 31, d: 38, delay: 14 },
  { left: 42, d: 27, delay: 6 },
  { left: 50, d: 33, delay: 18 },
  { left: 58, d: 30, delay: 1 },
  { left: 67, d: 36, delay: 11 },
  { left: 75, d: 28, delay: 5 },
  { left: 83, d: 32, delay: 16 },
  { left: 90, d: 25, delay: 8 },
  { left: 96, d: 35, delay: 12 },
];

/**
 * The dotted-grid canvas. Renders the grid on an absolutely positioned layer
 * behind its children, so `fade` (edge mask) never dims the content. Never
 * animates; `particles` adds the login-screen lime drift (disabled under
 * reduced motion).
 */
export function DotGrid({
  fade = false,
  particles = false,
  className,
  children,
  ...props
}: React.ComponentProps<"div"> & { fade?: boolean; particles?: boolean }) {
  return (
    <div
      data-slot="dot-grid"
      className={cn("relative isolate", className)}
      {...props}
    >
      <div
        aria-hidden="true"
        className={cn("ae-grid-bg pointer-events-none absolute inset-0 -z-10", fade && "ae-grid-bg--fade")}
      />
      {particles && (
        <div
          aria-hidden="true"
          className="pointer-events-none absolute inset-0 -z-10 overflow-hidden"
        >
          {PARTICLES.map((p, i) => (
            <span
              key={i}
              className="ae-particle"
              style={
                {
                  left: `${p.left}%`,
                  "--d": `${p.d}s`,
                  "--delay": `-${p.delay}s`,
                } as React.CSSProperties
              }
            />
          ))}
        </div>
      )}
      {children}
    </div>
  );
}
