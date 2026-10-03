import { AppShell } from "@/components/shell/app-shell";
import { AuthGuard } from "@/lib/auth/auth-guard";

export default function AppLayout({ children }: { children: React.ReactNode }) {
  return (
    <AuthGuard>
      <AppShell>{children}</AppShell>
    </AuthGuard>
  );
}
