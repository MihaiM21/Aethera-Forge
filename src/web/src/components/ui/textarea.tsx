import * as React from "react";
import { cn } from "@/lib/utils";
import { fieldBase } from "./input";

export function Textarea({
  className,
  ...props
}: React.ComponentProps<"textarea">) {
  return (
    <textarea
      data-slot="textarea"
      className={cn(fieldBase, "min-h-20 resize-y py-2 leading-snug", className)}
      {...props}
    />
  );
}
