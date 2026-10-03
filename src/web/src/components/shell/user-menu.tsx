"use client";

import * as React from "react";
import { useRouter } from "next/navigation";
import { LogOutIcon, SettingsIcon } from "lucide-react";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { useAuth } from "@/lib/auth/auth-context";

export function initials(name: string | undefined): string {
  const parts = (name ?? "").trim().split(/\s+/).filter(Boolean);
  if (parts.length === 0) return "?";
  const first = parts[0][0] ?? "";
  const last = parts.length > 1 ? (parts[parts.length - 1][0] ?? "") : "";
  return (first + last).toUpperCase();
}

/** Round avatar button (avatars are one of the two allowed circles) + menu. */
export function UserMenu({ side = "right" }: { side?: "right" | "bottom" }) {
  const { me, logout } = useAuth();
  const router = useRouter();

  async function onLogout() {
    await logout();
    router.replace("/login");
  }

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button
          variant="ghost"
          size="icon"
          aria-label={`Account menu for ${me?.user.displayName ?? "user"}`}
          className="rounded-full"
        >
          <span className="flex size-6 items-center justify-center rounded-full border border-border-strong bg-muted font-mono text-2xs">
            {initials(me?.user.displayName)}
          </span>
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent side={side} align="end" className="w-60">
        <DropdownMenuLabel className="flex flex-col gap-0.5 normal-case tracking-normal">
          <span className="text-sm font-medium text-foreground">{me?.user.displayName}</span>
          <span className="font-mono text-xs font-normal text-muted-foreground">
            {me?.user.email}
          </span>
          <span className="font-mono text-2xs font-normal tracking-wider text-lime uppercase">
            {me?.role} · {me?.organization.name}
          </span>
        </DropdownMenuLabel>
        <DropdownMenuSeparator />
        <DropdownMenuItem onSelect={() => router.push("/settings")}>
          <SettingsIcon aria-hidden="true" />
          Settings
        </DropdownMenuItem>
        <DropdownMenuItem onSelect={() => void onLogout()}>
          <LogOutIcon aria-hidden="true" />
          Log out
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
