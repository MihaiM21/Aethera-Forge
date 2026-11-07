import * as React from "react";
import { DotGrid } from "@/components/aethera/dot-grid";
import { InsecureConnectionHint } from "./insecure-connection-hint";
import { LogoMark } from "./logo-mark";
import { ThemeToggle } from "./theme-toggle";

/** Full-screen frame for /login and /setup: grid canvas, drifting lime dots, theme toggle. */
export function AuthScreen({ children }: { children: React.ReactNode }) {
  return (
    <DotGrid fade particles className="min-h-dvh overflow-hidden">
      <div className="absolute top-3 right-3 z-10">
        <ThemeToggle />
      </div>
      <main className="mx-auto flex min-h-dvh w-full max-w-lg flex-col justify-center px-6 py-12">
        <div className="mb-6 flex items-center gap-3">
          <LogoMark />
          <span className="font-mono text-xs tracking-wider text-muted-foreground uppercase">
            Aethera Forge
          </span>
        </div>
        {children}
        <InsecureConnectionHint />
      </main>
    </DotGrid>
  );
}
