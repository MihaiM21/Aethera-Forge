"use client";

import * as React from "react";
import { Toaster as Sonner, toast } from "sonner";
import { useTheme } from "@/components/theme/theme-provider";

export { toast };

/** Toasts (sonner), restyled to the square hairline look and themed from our provider. */
export function Toaster(props: React.ComponentProps<typeof Sonner>) {
  const { resolved } = useTheme();
  return (
    <Sonner
      theme={resolved}
      position="bottom-right"
      offset={{ bottom: "2rem", right: "1rem" }}
      className="toaster group"
      style={
        {
          "--normal-bg": "var(--popover)",
          "--normal-text": "var(--popover-foreground)",
          "--normal-border": "var(--ae-border-strong)",
          "--border-radius": "0px",
          "--success-bg": "var(--success-soft)",
          "--success-text": "var(--success)",
          "--success-border": "var(--success)",
          "--error-bg": "var(--danger-soft)",
          "--error-text": "var(--danger)",
          "--error-border": "var(--danger)",
          "--warning-bg": "var(--warning-soft)",
          "--warning-text": "var(--warning)",
          "--warning-border": "var(--warning)",
          "--info-bg": "var(--info-soft)",
          "--info-text": "var(--info)",
          "--info-border": "var(--info)",
          zIndex: "var(--ae-z-toast)",
        } as React.CSSProperties
      }
      toastOptions={{ classNames: { toast: "!shadow-popover !font-sans !text-sm" } }}
      {...props}
    />
  );
}
