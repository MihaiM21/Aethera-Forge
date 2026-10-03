"use client";

import * as React from "react";
import { useRouter } from "next/navigation";
import { DotGrid } from "@/components/aethera/dot-grid";
import {
  Sheet,
  SheetContent,
  SheetDescription,
  SheetTitle,
} from "@/components/ui/sheet";
import { readStorage, writeStorage } from "@/lib/storage";
import { CommandPalette } from "./command-palette";
import { NAV_ITEMS } from "./nav-items";
import { BrandMark, Rail } from "./rail";
import { SidebarContent } from "./sidebar";
import { StatusBar } from "./status-bar";
import { TopBar } from "./topbar";

const SIDEBAR_KEY = "aethera-sidebar";

function isTyping(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false;
  return (
    target.isContentEditable ||
    ["INPUT", "TEXTAREA", "SELECT"].includes(target.tagName) ||
    target.getAttribute("role") === "textbox"
  );
}

/**
 * Global keyboard shortcuts: Ctrl/Cmd+K palette, `/` palette (when not typing),
 * Ctrl/Cmd+B sidebar, `g` then d/p/a/s to jump (disabled while typing).
 */
function useShortcuts(handlers: {
  openPalette: () => void;
  toggleSidebar: () => void;
  go: (href: string) => void;
}) {
  const ref = React.useRef(handlers);
  React.useEffect(() => {
    ref.current = handlers;
  });

  React.useEffect(() => {
    let chord = false;
    let timer: number | undefined;
    const clear = () => {
      chord = false;
      window.clearTimeout(timer);
    };

    function onKey(e: KeyboardEvent) {
      const mod = e.ctrlKey || e.metaKey;
      const key = e.key.toLowerCase();
      if (mod && key === "k") {
        e.preventDefault();
        ref.current.openPalette();
        return clear();
      }
      if (mod && key === "b") {
        e.preventDefault();
        ref.current.toggleSidebar();
        return clear();
      }
      if (mod || e.altKey || isTyping(e.target)) return clear();
      if (e.target instanceof HTMLElement && e.target.closest("[role=dialog]")) return clear();

      if (chord) {
        const item = NAV_ITEMS.find((i) => i.chord === key);
        clear();
        if (item) {
          e.preventDefault();
          ref.current.go(item.href);
        }
        return;
      }
      if (key === "/") {
        e.preventDefault();
        ref.current.openPalette();
      } else if (key === "g") {
        chord = true;
        timer = window.setTimeout(clear, 1000);
      }
    }

    window.addEventListener("keydown", onKey);
    return () => {
      window.removeEventListener("keydown", onKey);
      window.clearTimeout(timer);
    };
  }, []);
}

/**
 * The authenticated application frame: icon rail, collapsible sidebar, top
 * bar (search / palette / theme), dotted-grid content canvas and status bar.
 * Below `md` the rail is replaced by a sheet; below `lg` the sidebar is in
 * that sheet too.
 */
export function AppShell({ children }: { children: React.ReactNode }) {
  const router = useRouter();
  const [paletteOpen, setPaletteOpen] = React.useState(false);
  const [mobileOpen, setMobileOpen] = React.useState(false);
  const [sidebarOpen, setSidebarOpen] = React.useState<boolean>(() => {
    // Only runs on the client: the shell is mounted after the session check.
    const saved = readStorage(SIDEBAR_KEY);
    if (saved === "open") return true;
    if (saved === "closed") return false;
    return typeof window !== "undefined" && window.innerWidth >= 1280;
  });

  const toggleSidebar = React.useCallback(() => {
    // Below lg the sidebar is a sheet, so the shortcut opens that instead.
    if (window.matchMedia("(min-width: 1024px)").matches) {
      setSidebarOpen((v) => {
        writeStorage(SIDEBAR_KEY, v ? "closed" : "open");
        return !v;
      });
    } else {
      setMobileOpen((v) => !v);
    }
  }, []);

  useShortcuts({
    openPalette: () => setPaletteOpen(true),
    toggleSidebar,
    go: (href) => router.push(href),
  });

  return (
    <div className="flex h-dvh flex-col bg-background">
      <a
        href="#main"
        className="focus-ring sr-only z-[var(--ae-z-toast)] bg-primary px-3 py-2 text-primary-foreground focus:not-sr-only focus:fixed focus:top-2 focus:left-2"
      >
        Skip to content
      </a>

      <div className="flex min-h-0 flex-1">
        <Rail className="hidden md:flex" />

        <div className="flex min-w-0 flex-1 flex-col">
          <TopBar
            onOpenPalette={() => setPaletteOpen(true)}
            onToggleSidebar={toggleSidebar}
            onOpenMobileNav={() => setMobileOpen(true)}
          />
          <div className="flex min-h-0 flex-1">
            {sidebarOpen && (
              <aside
                aria-label="Sidebar"
                className="hidden w-[var(--ae-sidebar-width)] shrink-0 border-r border-sidebar-border bg-sidebar lg:block"
              >
                <SidebarContent />
              </aside>
            )}
            <DotGrid className="flex min-w-0 flex-1 flex-col">
              <main
                id="main"
                tabIndex={-1}
                className="min-h-0 flex-1 overflow-y-auto px-4 py-6 outline-none sm:px-[var(--ae-content-padding)]"
              >
                <div className="mx-auto w-full max-w-[var(--ae-content-max)]">{children}</div>
              </main>
            </DotGrid>
          </div>
        </div>
      </div>

      <StatusBar />

      <Sheet open={mobileOpen} onOpenChange={setMobileOpen}>
        <SheetContent side="left">
          <SheetTitle>Navigation</SheetTitle>
          <SheetDescription>Main navigation and account</SheetDescription>
          <div className="flex items-center gap-2 border-b border-sidebar-border p-2 md:hidden">
            <BrandMark />
            <span className="font-mono text-xs tracking-wider text-muted-foreground uppercase">
              Aethera
            </span>
          </div>
          <SidebarContent
            className="min-h-0 flex-1"
            onNavigate={() => setMobileOpen(false)}
          />
        </SheetContent>
      </Sheet>

      <CommandPalette
        open={paletteOpen}
        onOpenChange={setPaletteOpen}
        onToggleSidebar={toggleSidebar}
      />
    </div>
  );
}
