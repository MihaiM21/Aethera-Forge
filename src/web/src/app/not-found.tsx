import Link from "next/link";
import { TerminalCard } from "@/components/aethera/terminal-card";
import { AuthScreen } from "@/components/shell/auth-screen";
import { Button } from "@/components/ui/button";

// Exported as `404.html`; the API serves it (with status 404) for any URL that
// matches neither a file nor a known route (docs/architecture/0005-web-routing.md).
export default function NotFound() {
  return (
    <AuthScreen>
      <TerminalCard path="404" label="!!">
        <p className="text-danger">[fail] route not found</p>
        <p className="mb-4 text-muted-foreground">
          Nothing is served at this address.
        </p>
        <Button asChild>
          <Link href="/dashboard">Go to dashboard</Link>
        </Button>
      </TerminalCard>
    </AuthScreen>
  );
}
