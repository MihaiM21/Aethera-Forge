import * as React from "react";
import { cn } from "@/lib/utils";

/** Loading placeholder. The shimmer stops under prefers-reduced-motion. */
export function Skeleton({ className, ...props }: React.ComponentProps<"div">) {
  return (
    <div
      data-slot="skeleton"
      aria-hidden="true"
      className={cn(
        "bg-muted bg-[linear-gradient(90deg,transparent,var(--accent),transparent)] bg-[length:200%_100%]",
        "animate-ae-shimmer motion-reduce:animate-none",
        className,
      )}
      {...props}
    />
  );
}
