import * as React from "react";
import { cn } from "@/lib/utils";

/**
 * Terminal-window card: three dots, a centred `aethera://path` title, an
 * optional right-hand label (index, status), and the signature offset plate.
 * For boot, install commands, live logs and empty states. Not for ordinary
 * data cards.
 */
export function TerminalCard({
  path,
  label,
  plate = true,
  className,
  bodyClassName,
  children,
  ...props
}: Omit<React.ComponentProps<"section">, "title"> & {
  /** Path after `aethera://`, e.g. `boot` or `servers/new`. */
  path: string;
  /** Right-aligned label: `01`, `VERIFIED`, ... */
  label?: React.ReactNode;
  /** Draw the offset plate behind the card (reserves its 18px of space). */
  plate?: boolean;
  bodyClassName?: string;
}) {
  return (
    <section
      data-slot="terminal-card"
      aria-label={`aethera://${path}`}
      className={cn(
        "ae-terminal",
        plate ? "mr-[var(--ae-plate-offset-x)] mb-[var(--ae-plate-offset-y)]" : "before:hidden",
        className,
      )}
      {...props}
    >
      <div className="ae-terminal__bar">
        <span className="ae-terminal__dots" aria-hidden="true">
          <i />
          <i />
          <i />
        </span>
        <span className="ae-terminal__title">aethera://{path}</span>
        <span className="ae-terminal__index">{label}</span>
      </div>
      <div className={cn("ae-terminal__body", bodyClassName)}>{children}</div>
    </section>
  );
}
