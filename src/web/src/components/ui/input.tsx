import * as React from "react";
import { cn } from "@/lib/utils";

export const fieldBase =
  "w-full min-w-0 border border-input bg-popover px-3 text-sm text-foreground " +
  "placeholder:text-muted-foreground transition-colors duration-[var(--ae-duration-fast)] " +
  "focus-visible:border-ring focus-visible:outline-none focus-visible:shadow-[var(--ae-focus-ring)] " +
  "disabled:cursor-not-allowed disabled:opacity-50 " +
  "aria-invalid:border-danger aria-invalid:focus-visible:border-danger";

export function Input({
  className,
  type = "text",
  ...props
}: React.ComponentProps<"input">) {
  return (
    <input
      data-slot="input"
      type={type}
      className={cn(
        fieldBase,
        "h-8 file:border-0 file:bg-transparent file:text-sm file:font-medium",
        className,
      )}
      {...props}
    />
  );
}
