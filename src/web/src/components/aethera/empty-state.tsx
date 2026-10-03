import * as React from "react";
import { cn } from "@/lib/utils";
import { Eyebrow } from "./eyebrow";

/** Centered empty state: lime outline icon, mono label, title, text, action. */
export function EmptyState({
  icon: Icon,
  label,
  title,
  description,
  action,
  className,
  ...props
}: Omit<React.ComponentProps<"div">, "title"> & {
  icon?: React.ElementType;
  label?: string;
  title: React.ReactNode;
  description?: React.ReactNode;
  action?: React.ReactNode;
}) {
  return (
    <div
      data-slot="empty-state"
      className={cn(
        "flex flex-col items-center justify-center gap-3 px-6 py-12 text-center",
        className,
      )}
      {...props}
    >
      {Icon && (
        <Icon className="size-8 text-lime" strokeWidth={1.5} aria-hidden="true" />
      )}
      {label && <Eyebrow muted className="before:hidden">{label}</Eyebrow>}
      <h2 className="text-xl font-medium tracking-heading">{title}</h2>
      {description && (
        <p className="max-w-sm text-sm text-muted-foreground">{description}</p>
      )}
      {action && <div className="mt-2 flex flex-wrap items-center justify-center gap-2">{action}</div>}
    </div>
  );
}
