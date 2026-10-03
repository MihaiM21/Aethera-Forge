import * as React from "react";

/**
 * Link to a dynamic detail page (`/projects/{id}`, `/applications/{id}`...).
 *
 * Detail ids are not pre-rendered (docs/architecture/0005-web-routing.md), so
 * `next/link` would try to prefetch / client-navigate to payload files that do
 * not exist for that id. A plain anchor does a normal document navigation and
 * the API maps the URL to the exported shell (`projects/_.html`).
 */
export function DetailLink(props: React.ComponentProps<"a">) {
  return <a {...props} />;
}
