import { cn } from "@/lib/utils";

/**
 * The Aethera mark (artwork in `public/brand`, sources in `docs/assets/brand`).
 * The dark or light variant follows the theme (`data-theme` on <html>).
 */
export function LogoMark({ className }: { className?: string }) {
  return (
    <>
      {/* eslint-disable-next-line @next/next/no-img-element -- static export, SVG */}
      <img
        src="/brand/logo-mark.svg"
        alt=""
        aria-hidden="true"
        className={cn("hidden size-8 shrink-0 dark:block", className)}
      />
      {/* eslint-disable-next-line @next/next/no-img-element -- static export, SVG */}
      <img
        src="/brand/logo-mark-light.svg"
        alt=""
        aria-hidden="true"
        className={cn("block size-8 shrink-0 dark:hidden", className)}
      />
    </>
  );
}
