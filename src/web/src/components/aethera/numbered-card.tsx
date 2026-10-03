import * as React from "react";
import { cn } from "@/lib/utils";

/**
 * Square outlined card with a lime mono index (`01`), a title and a muted
 * description at the bottom. Wrap siblings in <NumberedCardGrid> so adjacent
 * borders collapse into one line.
 */
export function NumberedCard({
  index,
  title,
  description,
  children,
  className,
  as: Comp = "div",
  ...props
}: Omit<React.ComponentProps<"div">, "title"> & {
  index: string;
  title: React.ReactNode;
  description?: React.ReactNode;
  as?: React.ElementType;
}) {
  return (
    <Comp
      data-slot="numbered-card"
      className={cn(
        "flex min-h-40 flex-col justify-between gap-6 p-5 text-card-foreground",
        "focus-ring transition-colors duration-[var(--ae-duration-fast)]",
        className,
      )}
      {...props}
    >
      <span className="ae-numbered__index">{index}</span>
      <div className="flex flex-col gap-1.5">
        {children}
        <h3 className="text-lg leading-snug font-medium tracking-subheading">{title}</h3>
        {description && (
          <p className="text-xs leading-relaxed text-muted-foreground">{description}</p>
        )}
      </div>
    </Comp>
  );
}

/** Grid whose cells share 1px borders (no double lines). */
export function NumberedCardGrid({
  className,
  columns = 4,
  ...props
}: React.ComponentProps<"div"> & { columns?: 2 | 3 | 4 }) {
  return (
    <div
      data-slot="numbered-card-grid"
      className={cn(
        "grid border-t border-l border-border bg-card/40 [&>*]:border-r [&>*]:border-b [&>*]:border-border",
        columns === 2 && "grid-cols-1 sm:grid-cols-2",
        columns === 3 && "grid-cols-1 sm:grid-cols-3",
        columns === 4 && "grid-cols-1 sm:grid-cols-2 lg:grid-cols-4",
        className,
      )}
      {...props}
    />
  );
}
