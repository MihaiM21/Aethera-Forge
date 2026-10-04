/**
 * Document navigation to a page whose id is not pre-rendered (`/deployments/{id}`, `/applications/{id}`): `next/router`
 * would look for payload files that do not exist for that id, so the API must map the URL to the exported shell
 * (docs/architecture/0005-web-routing.md). Same reason as `DetailLink`, for navigation after an action.
 */
export function navigateTo(path: string): void {
  window.location.assign(path);
}
