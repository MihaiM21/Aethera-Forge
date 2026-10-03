import * as React from "react";
import { cn } from "@/lib/utils";

/** Keycap shown next to shortcuts (`Ctrl` `K`). */
export function Kbd({ className, ...props }: React.ComponentProps<"kbd">) {
  return <kbd className={cn("ae-kbd", className)} {...props} />;
}
