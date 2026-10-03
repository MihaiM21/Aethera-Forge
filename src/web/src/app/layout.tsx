import type { Metadata } from "next";
import { GeistMono } from "geist/font/mono";
import { GeistSans } from "geist/font/sans";
import { ThemeProvider } from "@/components/theme/theme-provider";
import { TooltipProvider } from "@/components/ui/tooltip";
import { Toaster } from "@/components/ui/sonner";
import { AuthProvider } from "@/lib/auth/auth-context";
import { THEME_INIT_SCRIPT } from "@/lib/theme";
import "./globals.css";

export const metadata: Metadata = {
  title: { default: "Aethera", template: "%s · Aethera" },
  description: "Self-hosted infrastructure. Simple deployments. Full control.",
};

export default function RootLayout({ children }: LayoutProps<"/">) {
  return (
    // data-theme is set by the inline script before first paint, so React must
    // not complain that it differs from the server-rendered default.
    <html
      lang="en"
      data-theme="dark"
      suppressHydrationWarning
      className={`${GeistSans.variable} ${GeistMono.variable} h-full antialiased`}
    >
      <head>
        <script dangerouslySetInnerHTML={{ __html: THEME_INIT_SCRIPT }} />
      </head>
      <body className="min-h-full">
        <ThemeProvider>
          <TooltipProvider>
            <AuthProvider>
              {children}
              <Toaster />
            </AuthProvider>
          </TooltipProvider>
        </ThemeProvider>
      </body>
    </html>
  );
}
