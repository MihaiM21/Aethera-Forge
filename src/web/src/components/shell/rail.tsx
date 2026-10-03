"use client";

import * as React from "react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { Kbd } from "@/components/ui/kbd";
import { cn } from "@/lib/utils";
import { MAIN_NAV, PINNED_NAV, isActive, type NavItem } from "./nav-items";
import { UserMenu } from "./user-menu";

export function BrandMark({ className }: { className?: string }) {
  return (
    <Link
      href="/dashboard"
      aria-label="Aethera home"
      className={cn(
        "focus-ring flex size-8 items-center justify-center border border-primary font-mono text-sm font-semibold text-lime",
        className,
      )}
    >
      A
    </Link>
  );
}

function RailLink({ item, active }: { item: NavItem; active: boolean }) {
  const Icon = item.icon;
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <Link
          href={item.href}
          aria-label={item.label}
          aria-current={active ? "page" : undefined}
          className={cn(
            "focus-ring relative flex size-8 items-center justify-center text-muted-foreground transition-colors duration-[var(--ae-duration-fast)]",
            "hover:bg-sidebar-accent hover:text-foreground",
            active &&
              "bg-sidebar-accent text-lime before:absolute before:inset-y-0 before:-left-2 before:w-0.5 before:bg-primary",
          )}
        >
          <Icon className="size-5" strokeWidth={1.5} aria-hidden="true" />
        </Link>
      </TooltipTrigger>
      <TooltipContent side="right">
        {item.label}
        {item.chord && (
          <span className="flex gap-1">
            <Kbd>g</Kbd>
            <Kbd>{item.chord}</Kbd>
          </span>
        )}
      </TooltipContent>
    </Tooltip>
  );
}

/** 48px icon rail. Active section: 2px lime bar on the left edge + lime icon. */
export function Rail({ className }: { className?: string }) {
  const pathname = usePathname();
  return (
    <div
      data-slot="rail"
      className={cn(
        "w-[var(--ae-rail-width)] shrink-0 flex-col items-center gap-1 border-r border-sidebar-border bg-sidebar py-2",
        className,
      )}
    >
      <BrandMark className="mb-2" />
      <nav aria-label="Primary" className="flex flex-1 flex-col items-center gap-1">
        {MAIN_NAV.map((item) => (
          <RailLink key={item.href} item={item} active={isActive(pathname, item.href)} />
        ))}
      </nav>
      <nav aria-label="Account" className="flex flex-col items-center gap-1">
        {PINNED_NAV.map((item) => (
          <RailLink key={item.href} item={item} active={isActive(pathname, item.href)} />
        ))}
        <UserMenu side="right" />
      </nav>
    </div>
  );
}
