"use client";

import * as React from "react";
import { Dialog as SheetPrimitive } from "radix-ui";
import { XIcon } from "lucide-react";
import { cn } from "@/lib/utils";

export const Sheet = SheetPrimitive.Root;
export const SheetTrigger = SheetPrimitive.Trigger;
export const SheetClose = SheetPrimitive.Close;

/** Side drawer (mobile navigation). Same focus trap and Esc handling as Dialog. */
export function SheetContent({
  className,
  children,
  side = "left",
  ...props
}: React.ComponentProps<typeof SheetPrimitive.Content> & {
  side?: "left" | "right";
}) {
  return (
    <SheetPrimitive.Portal>
      <SheetPrimitive.Overlay
        className={cn(
          "fixed inset-0 z-[var(--ae-z-overlay)] bg-[var(--ae-overlay)]",
          "data-[state=open]:animate-in data-[state=open]:fade-in-0 data-[state=closed]:animate-out data-[state=closed]:fade-out-0",
        )}
      />
      <SheetPrimitive.Content
        data-slot="sheet-content"
        className={cn(
          "fixed inset-y-0 z-[var(--ae-z-modal)] flex w-[18rem] max-w-[85vw] flex-col border-border bg-sidebar text-sidebar-foreground shadow-popover",
          "data-[state=open]:animate-in data-[state=closed]:animate-out",
          "duration-[var(--ae-duration-slow)]",
          side === "left" &&
            "left-0 border-r data-[state=open]:slide-in-from-left data-[state=closed]:slide-out-to-left",
          side === "right" &&
            "right-0 border-l data-[state=open]:slide-in-from-right data-[state=closed]:slide-out-to-right",
          className,
        )}
        {...props}
      >
        {children}
        <SheetPrimitive.Close
          className="focus-ring absolute top-2 right-2 p-1 text-muted-foreground hover:text-foreground"
          aria-label="Close menu"
        >
          <XIcon className="size-4" />
        </SheetPrimitive.Close>
      </SheetPrimitive.Content>
    </SheetPrimitive.Portal>
  );
}

export function SheetTitle({
  className,
  ...props
}: React.ComponentProps<typeof SheetPrimitive.Title>) {
  return <SheetPrimitive.Title className={cn("sr-only", className)} {...props} />;
}

export function SheetDescription({
  className,
  ...props
}: React.ComponentProps<typeof SheetPrimitive.Description>) {
  return <SheetPrimitive.Description className={cn("sr-only", className)} {...props} />;
}
