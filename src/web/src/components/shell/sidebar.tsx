"use client";

import * as React from "react";
import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { LogOutIcon, SearchIcon } from "lucide-react";
import { Eyebrow } from "@/components/aethera/eyebrow";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { useAuth } from "@/lib/auth/auth-context";
import { cn } from "@/lib/utils";
import { NAV_ITEMS, isActive } from "./nav-items";

/**
 * Sidebar content: organization header, filter and the labelled navigation.
 * Used both by the desktop sidebar and the mobile sheet. The contextual trees
 * (project tree, server list...) land with their own work packages.
 */
export function SidebarContent({
  onNavigate,
  className,
}: {
  /** Called after a navigation click, so the mobile sheet can close. */
  onNavigate?: () => void;
  className?: string;
}) {
  const pathname = usePathname();
  const router = useRouter();
  const { me, logout } = useAuth();
  const [filter, setFilter] = React.useState("");
  const filterId = React.useId();

  const q = filter.trim().toLowerCase();
  const items = q ? NAV_ITEMS.filter((i) => i.label.toLowerCase().includes(q)) : NAV_ITEMS;

  async function onLogout() {
    onNavigate?.();
    await logout();
    router.replace("/login");
  }

  return (
    <div className={cn("flex h-full min-h-0 flex-col", className)}>
      <div className="flex flex-col gap-3 border-b border-sidebar-border p-3">
        <div className="flex flex-col gap-1">
          <Eyebrow muted className="before:w-3">
            Workspace
          </Eyebrow>
          <p className="truncate text-md font-medium tracking-subheading">
            {me?.organization.name ?? "Aethera"}
          </p>
        </div>
        <div className="relative">
          <label htmlFor={filterId} className="sr-only">
            Filter navigation
          </label>
          <SearchIcon
            className="pointer-events-none absolute top-1/2 left-2.5 size-3.5 -translate-y-1/2 text-muted-foreground"
            aria-hidden="true"
          />
          <Input
            id={filterId}
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
            placeholder="Filter…"
            className="h-8 pl-8 text-sm"
            autoComplete="off"
          />
        </div>
      </div>

      <nav aria-label="Sections" className="min-h-0 flex-1 overflow-y-auto p-2">
        <ul className="flex flex-col gap-0.5">
          {items.map((item) => {
            const active = isActive(pathname, item.href);
            const Icon = item.icon;
            return (
              <li key={item.href}>
                <Link
                  href={item.href}
                  onClick={onNavigate}
                  aria-current={active ? "page" : undefined}
                  className={cn(
                    "focus-ring relative flex h-8 items-center gap-2.5 px-2.5 text-sm text-muted-foreground transition-colors duration-[var(--ae-duration-fast)]",
                    "hover:bg-sidebar-accent hover:text-foreground",
                    active &&
                      "bg-sidebar-accent text-foreground before:absolute before:inset-y-0 before:left-0 before:w-0.5 before:bg-primary",
                  )}
                >
                  <Icon
                    className={cn("size-4 shrink-0", active && "text-lime")}
                    strokeWidth={1.5}
                    aria-hidden="true"
                  />
                  <span className="truncate">{item.label}</span>
                </Link>
              </li>
            );
          })}
          {items.length === 0 && (
            <li className="px-2.5 py-3 font-mono text-xs text-muted-foreground">No matches</li>
          )}
        </ul>
      </nav>

      <div className="flex items-center justify-between gap-2 border-t border-sidebar-border p-3">
        <div className="min-w-0">
          <p className="truncate text-sm">{me?.user.displayName}</p>
          <p className="truncate font-mono text-2xs tracking-wider text-muted-foreground uppercase">
            {me?.role}
          </p>
        </div>
        <Button
          variant="ghost"
          size="icon-sm"
          onClick={() => void onLogout()}
          aria-label="Log out"
        >
          <LogOutIcon aria-hidden="true" />
        </Button>
      </div>
    </div>
  );
}
