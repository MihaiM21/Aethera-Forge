import * as React from "react";
import { DotGrid } from "@/components/aethera/dot-grid";
import { ThemeToggle } from "./theme-toggle";

/** Full-screen frame for /login and /setup: grid canvas, drifting lime dots, theme toggle. */
export function AuthScreen({ children }: { children: React.ReactNode }) {
  return (
    <DotGrid fade particles className="min-h-dvh overflow-hidden">
      <div className="absolute top-3 right-3 z-10">
        <ThemeToggle />
      </div>
      <main className="mx-auto flex min-h-dvh w-full max-w-lg flex-col justify-center px-6 py-12">
        {children}
      </main>
    </DotGrid>
  );
}
