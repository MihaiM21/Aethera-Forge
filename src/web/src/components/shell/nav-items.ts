import {
  ActivityIcon,
  BoxesIcon,
  ContainerIcon,
  FolderKanbanIcon,
  GlobeIcon,
  KeyRoundIcon,
  LayersIcon,
  LayoutDashboardIcon,
  RocketIcon,
  ServerIcon,
  SettingsIcon,
  type LucideIcon,
} from "lucide-react";

export type NavItem = {
  href: string;
  label: string;
  icon: LucideIcon;
  /** Second key of the `g <key>` chord. */
  chord?: string;
  /** Sentence for placeholder pages and the palette. */
  description: string;
  /** Pinned to the bottom of the rail instead of the main list. */
  pinned?: boolean;
};

/** Main navigation from spec section 37. Order is the display order. */
export const NAV_ITEMS: NavItem[] = [
  {
    href: "/dashboard",
    label: "Dashboard",
    icon: LayoutDashboardIcon,
    chord: "d",
    description: "Overview of the whole installation: servers, applications, deployments and jobs.",
  },
  {
    href: "/projects",
    label: "Projects",
    icon: FolderKanbanIcon,
    chord: "p",
    description: "Group applications, services and environments by project.",
  },
  {
    href: "/applications",
    label: "Applications",
    icon: BoxesIcon,
    chord: "a",
    description: "Everything you deploy: source, build method, environment, domains and health.",
  },
  {
    href: "/services",
    label: "Services",
    icon: LayersIcon,
    description: "Databases, caches and other managed services running next to your apps.",
  },
  {
    href: "/servers",
    label: "Servers",
    icon: ServerIcon,
    chord: "s",
    description: "Machines running the Aethera agent, with live CPU, memory and disk usage.",
  },
  {
    href: "/deployments",
    label: "Deployments",
    icon: RocketIcon,
    description: "Every deploy with its pipeline, logs and result, newest first.",
  },
  {
    href: "/domains",
    label: "Domains",
    icon: GlobeIcon,
    description: "Hostnames, routing and certificates for your applications.",
  },
  {
    href: "/registries",
    label: "Registries",
    icon: ContainerIcon,
    description: "Container registries used to pull and push images.",
  },
  {
    href: "/secrets",
    label: "Secrets",
    icon: KeyRoundIcon,
    description: "Encrypted variables and credentials, masked in the UI and redacted from logs.",
  },
  {
    href: "/monitoring",
    label: "Monitoring",
    icon: ActivityIcon,
    description: "Resource usage, health checks and alerts across servers and applications.",
  },
  {
    href: "/settings",
    label: "Settings",
    icon: SettingsIcon,
    chord: undefined,
    description: "Account, organization, members, API tokens and platform preferences.",
    pinned: true,
  },
];

export const MAIN_NAV = NAV_ITEMS.filter((i) => !i.pinned);
export const PINNED_NAV = NAV_ITEMS.filter((i) => i.pinned);

export function isActive(pathname: string | null, href: string): boolean {
  if (!pathname) return false;
  return pathname === href || pathname.startsWith(`${href}/`);
}

export function findNavItem(pathname: string | null): NavItem | undefined {
  return NAV_ITEMS.find((i) => isActive(pathname, i.href));
}
