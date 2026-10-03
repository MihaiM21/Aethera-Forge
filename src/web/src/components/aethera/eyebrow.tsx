import * as React from "react";
import { cn } from "@/lib/utils";

/** Mono uppercase micro label with a leading hairline: `── DEPLOYMENTS`. */
export function Eyebrow({
  className,
  muted = false,
  ...props
}: React.ComponentProps<"span"> & { muted?: boolean }) {
  return (
    <span
      data-slot="eyebrow"
      className={cn("ae-eyebrow", muted && "ae-eyebrow--muted", className)}
      {...props}
    />
  );
}
