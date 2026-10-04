"use client";

import * as React from "react";
import { useRouter } from "next/navigation";
import {
  CornerDownLeftIcon,
  LogOutIcon,
  MoonIcon,
  PanelLeftIcon,
  PlusIcon,
  ServerIcon,
  SunIcon,
} from "lucide-react";
import {
  CommandDialog,
  CommandEmpty,
  CommandGroup,
  CommandInput,
  CommandItem,
  CommandList,
  CommandShortcut,
} from "@/components/ui/command";
import { Kbd } from "@/components/ui/kbd";
import { useTheme } from "@/components/theme/theme-provider";
import { useAuth } from "@/lib/auth/auth-context";
import { usePaletteServers } from "@/lib/servers/use-palette-servers";
import { NAV_ITEMS } from "./nav-items";

/** Case-insensitive substring match; `>` prefix is handled by the caller. */
function filter(value: string, search: string, keywords?: string[]): number {
  const q = search.replace(/^>\s*/, "").trim().toLowerCase();
  if (!q) return 1;
  const hay = `${value} ${(keywords ?? []).join(" ")}`.toLowerCase();
  return hay.includes(q) ? 1 : 0;
}

export function CommandPalette({
  open,
  onOpenChange,
  onToggleSidebar,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onToggleSidebar: () => void;
}) {
  const router = useRouter();
  const { toggle, resolved } = useTheme();
  const { logout } = useAuth();
  const [search, setSearch] = React.useState("");
  const servers = usePaletteServers(open);

  const actionsOnly = search.startsWith(">");

  function run(fn: () => void | Promise<void>) {
    onOpenChange(false);
    // Let the dialog close (and release focus) before navigating.
    window.setTimeout(() => void fn(), 0);
  }

  return (
    <CommandDialog
      open={open}
      onOpenChange={(next) => {
        if (!next) setSearch("");
        onOpenChange(next);
      }}
      commandProps={{ filter }}
    >
      <CommandInput
        value={search}
        onValueChange={setSearch}
        placeholder="Search or run command…  (type > for actions)"
        aria-label="Search or run command"
      />
      <CommandList>
        <CommandEmpty>No results</CommandEmpty>
        {!actionsOnly && (
          <CommandGroup heading="Navigate">
            {NAV_ITEMS.map((item) => {
              const Icon = item.icon;
              return (
                <CommandItem
                  key={item.href}
                  value={`go ${item.label}`}
                  keywords={[item.href, item.description]}
                  onSelect={() => run(() => router.push(item.href))}
                >
                  <Icon aria-hidden="true" />
                  <span>{item.label}</span>
                  {item.chord && (
                    <CommandShortcut className="flex gap-1">
                      <Kbd>g</Kbd>
                      <Kbd>{item.chord}</Kbd>
                    </CommandShortcut>
                  )}
                </CommandItem>
              );
            })}
          </CommandGroup>
        )}
        {!actionsOnly && servers.length > 0 && (
          <CommandGroup heading="Servers">
            {servers.map((s) => (
              <CommandItem
                key={s.id}
                value={`server ${s.name}`}
                keywords={[s.host, ...s.roles, "machine", "host"]}
                // Detail ids are not pre-rendered: use a document navigation (ADR 0005).
                onSelect={() => run(() => {
                  // eslint-disable-next-line @next/next/no-location-assign-relative-destination -- ADR 0005: detail ids are not pre-rendered, so a document navigation is required
                  window.location.href = `/servers/${encodeURIComponent(s.id)}`;
                })
              }
              >
                <ServerIcon aria-hidden="true" />
                <span>{s.name}</span>
                <CommandShortcut className="font-mono">{s.host}</CommandShortcut>
              </CommandItem>
            ))}
          </CommandGroup>
        )}
        <CommandGroup heading="Actions">
          <CommandItem
            value="action add server"
            keywords={["new server", "connect", "install agent", "join"]}
            onSelect={() => run(() => router.push("/servers/new"))}
          >
            <PlusIcon aria-hidden="true" />
            <span>Add server</span>
          </CommandItem>
          <CommandItem
            value="action toggle theme"
            keywords={["dark", "light", "appearance"]}
            onSelect={() => run(toggle)}
          >
            {resolved === "dark" ? <SunIcon aria-hidden="true" /> : <MoonIcon aria-hidden="true" />}
            <span>Toggle theme</span>
            <CommandShortcut>{resolved === "dark" ? "→ light" : "→ dark"}</CommandShortcut>
          </CommandItem>
          <CommandItem
            value="action toggle sidebar"
            keywords={["navigation", "panel"]}
            onSelect={() => run(onToggleSidebar)}
          >
            <PanelLeftIcon aria-hidden="true" />
            <span>Toggle sidebar</span>
            <CommandShortcut className="flex gap-1">
              <Kbd>Ctrl</Kbd>
              <Kbd>B</Kbd>
            </CommandShortcut>
          </CommandItem>
          <CommandItem
            value="action log out"
            keywords={["sign out", "logout"]}
            onSelect={() =>
              run(async () => {
                await logout();
                router.replace("/login");
              })
            }
          >
            <LogOutIcon aria-hidden="true" />
            <span>Log out</span>
          </CommandItem>
        </CommandGroup>
      </CommandList>
      <div className="flex items-center gap-3 border-t border-border px-3 py-2 font-mono text-2xs text-muted-foreground">
        <span className="flex items-center gap-1">
          <Kbd>↑</Kbd>
          <Kbd>↓</Kbd> navigate
        </span>
        <span className="flex items-center gap-1">
          <Kbd>
            <CornerDownLeftIcon className="size-3" aria-label="Enter" />
          </Kbd>{" "}
          run
        </span>
        <span className="flex items-center gap-1">
          <Kbd>Esc</Kbd> close
        </span>
      </div>
    </CommandDialog>
  );
}
