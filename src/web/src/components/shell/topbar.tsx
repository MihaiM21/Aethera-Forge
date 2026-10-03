"use client";

import * as React from "react";
import { usePathname } from "next/navigation";
import { MenuIcon, PanelLeftIcon, SearchIcon } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Kbd } from "@/components/ui/kbd";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { ThemeToggle } from "./theme-toggle";

const subscribeNever = () => () => {};

function Breadcrumbs() {
  const pathname = usePathname() ?? "/";
  const parts = pathname.split("/").filter(Boolean);
  return (
    <nav aria-label="Breadcrumb" className="hidden min-w-0 md:block">
      <ol className="flex items-center gap-1.5 font-mono text-xs text-muted-foreground">
        <li>aethera</li>
        {parts.map((p, i) => (
          <li key={`${i}-${p}`} className="flex items-center gap-1.5 truncate">
            <span aria-hidden="true">/</span>
            <span
              className={i === parts.length - 1 ? "truncate text-foreground" : undefined}
              aria-current={i === parts.length - 1 ? "page" : undefined}
            >
              {decodeURIComponent(p)}
            </span>
          </li>
        ))}
      </ol>
    </nav>
  );
}

export function TopBar({
  onOpenPalette,
  onToggleSidebar,
  onOpenMobileNav,
}: {
  onOpenPalette: () => void;
  onToggleSidebar: () => void;
  onOpenMobileNav: () => void;
}) {
  const mac = React.useSyncExternalStore(
    subscribeNever,
    () => /mac|iphone|ipad/i.test(navigator.platform || navigator.userAgent),
    () => false,
  );

  return (
    <header
      data-slot="top-bar"
      className="grid h-[var(--ae-topbar-height)] shrink-0 grid-cols-[auto_minmax(0,1fr)_auto] md:grid-cols-[1fr_minmax(0,560px)_1fr] items-center gap-3 border-b border-sidebar-border bg-background px-2"
    >
      <div className="flex min-w-0 items-center gap-2">
        {/* Below lg the sidebar lives in a sheet; below md that sheet also replaces the rail. */}
        <Button
          variant="ghost"
          size="icon"
          className="lg:hidden"
          onClick={onOpenMobileNav}
          aria-label="Open navigation"
        >
          <MenuIcon aria-hidden="true" />
        </Button>
        <Tooltip>
          <TooltipTrigger asChild>
            <Button
              variant="ghost"
              size="icon"
              className="hidden lg:inline-flex"
              onClick={onToggleSidebar}
              aria-label="Toggle sidebar"
            >
              <PanelLeftIcon aria-hidden="true" />
            </Button>
          </TooltipTrigger>
          <TooltipContent>
            Toggle sidebar <Kbd>Ctrl</Kbd>
            <Kbd>B</Kbd>
          </TooltipContent>
        </Tooltip>
        <Breadcrumbs />
      </div>

      <button
        type="button"
        onClick={onOpenPalette}
        aria-label="Search or run command"
        aria-keyshortcuts="Control+K Meta+K"
        className="focus-ring flex h-8 w-full items-center gap-2 border border-border bg-popover px-2.5 text-left text-sm text-muted-foreground transition-colors hover:border-border-strong"
      >
        <SearchIcon className="size-3.5 shrink-0" aria-hidden="true" />
        <span className="flex-1 truncate">Search or run command…</span>
        <span className="hidden items-center gap-1 sm:flex">
          <Kbd>{mac ? "⌘" : "Ctrl"}</Kbd>
          <Kbd>K</Kbd>
        </span>
      </button>

      <div className="flex items-center justify-end gap-1">
        <ThemeToggle />
      </div>
    </header>
  );
}
