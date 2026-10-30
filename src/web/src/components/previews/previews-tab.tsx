"use client";

/** Application > Previews: pull request previews, their settings and status (R2.3, ADR 0012). */
export function PreviewsTab({ applicationId }: { applicationId: string }) {
  return (
    <p className="text-sm text-muted-foreground" data-application-id={applicationId}>
      Pull request previews are coming in this release.
    </p>
  );
}
