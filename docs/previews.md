# Pull request previews

Every pull request (GitHub) or merge request (GitLab) of an application can get its own live copy, with its own URL, posted on the pull request and removed when it is closed. This is Coolify's preview deployments, with forks off by default and GitLab supported. The design is [ADR 0012](architecture/0012-multi-server-mesh-previews.md).

A preview is built from the pull request's branch at its head commit and runs next to the application: its own containers, its own named volumes (empty at first), its own route. It never changes the application's status, its live deployment or its rollback points.

## Set up

1. **A wildcard domain or a public IP on the server.** The preview URL is `pr-<number>-<name>.<base>`, where `<name>` is the first label of the application's automatic host (or its slug) and `<base>` is what automatic domains use: the server's wildcard domain, else `<ip>.sslip.io`. Point `*.apps.example.com` at the server and set it as the server's wildcard domain (Servers > the server > Settings), or rely on sslip.io. A server with neither cannot host previews: the deployment fails with `preview.no_domain` and says why. HTTPS is used when the proxy has an ACME email (Settings > System).
2. **The application's webhook.** Applications > the application > Settings > Webhook > Set up, and add the webhook to the repository:
   - **GitHub**: Settings > Webhooks > Add webhook. Payload URL `https://<your Aethera>/webhooks/git/<id>`, content type `application/json`, the secret Aethera showed, and the events **Pull requests** (and **Pushes**, if the application auto-deploys). One webhook serves both.
   - **GitLab**: Settings > Webhooks. URL as above, **Secret token** the one Aethera showed, triggers **Merge request events** (and **Push events**).
3. **Turn previews on**: Applications > the application > Previews > "Deploy a preview for every pull request" > Save. Only applications built from a git repository have previews.

Only pull requests whose **target branch is the application's branch** get a preview. Deliveries are checked like pushes (signature or token) and are idempotent by delivery id; each one shows in the webhook's deliveries with what Aethera did (`Accepted`, or `Ignored` with the reason).

| Event | What happens |
|---|---|
| opened, reopened | the preview is created (or revived) and deployed |
| new commits (GitHub `synchronize`, GitLab `update` with new commits) | the preview is deployed again; the old container is replaced after the new one is healthy |
| closed, merged | the preview is torn down; this also works when previews were turned off meanwhile |
| anything else (labels, review requests, a new title) | ignored |

## Pull requests from forks

> **Off by default, and for a reason.** A pull request from a fork brings code written by someone else. A preview builds and runs it **with the application's environment variables and secrets**, on your server. If previews of forks are on, anyone who can open a pull request on the repository can read those secrets with a one-line change to the build. Turn it on only for repositories where every contributor is someone you trust, and give the application preview-only variables (below) with harmless values.

"Also deploy pull requests from forks" on the Previews tab is **administrator only**. Without it a fork's pull request is recorded as `Ignored` ("the pull request comes from a fork"). A fork is a pull request whose head repository is not the base repository. Even with forks allowed, a fork on another git host than the application's repository is ignored, so the application's git credential is never sent elsewhere.

Other defaults that keep a preview away from production, whoever wrote the code:

- the preview has **no published host ports**, none of the application's **custom container labels**, and none of its domains;
- named volumes are the preview's own (`<slug>-pr-<n>-...`): it never mounts the application's data, nor a volume of another workload;
- a **writable host path** (bind mount) of the application is not mounted in the preview; a read-only one is;
- build layers are cached per pull request, never shared with the application's cache;
- on the shared environment network the preview answers to its own name only, never to the application's.

## Preview variables

In the environment tab, a variable marked **Preview only** is used by previews and replaces a regular variable with the same name there. A regular deployment never sees it. Use them for a staging database, a sandbox payment key, a different public URL. Variables without the flag are shared by the application and its previews. Secrets can be preview-only too.

## Comments on the pull request

When the application's git source uses a **token credential** (Settings > Git credentials, kind token, with the right to write comments: `repo` or `pull_requests: write` on GitHub, `api` on GitLab), Aethera posts one comment per pull request and **edits it** instead of adding new ones: "building and deploying", then the URL and commit when it runs, or the step it failed at, and "removed" when the preview goes. If the comment is deleted, it is created again. Without a token credential (public repository, deploy key, basic auth) nothing is posted. A comment that could not be posted never fails the deployment; the reason is shown on the preview (`comment: ...`) and the token is never part of it. GitHub Enterprise (`https://<host>/api/v3`) and self-hosted GitLab (`https://<host>/api/v4`) are supported; Aethera only calls public hosts.

## Limits and cleanup

- **At most 10 previews run at once** per application (Previews > "Previews at once", 1 to 100). Further pull requests are recorded as *Waiting for a slot* and deployed, oldest first, as soon as a preview is removed. A push to a running preview never waits.
- **Closing or merging** the pull request queues `preview.teardown`: the containers (a compose project is taken down with its volumes), the named volumes and the images of the preview are removed from its server. It is safe to run again, and the application's own containers and volumes are never touched.
- **Remove** by hand (the bin button, or `DELETE /applications/{id}/previews/{number}`) does the same while the pull request stays open; its next push creates the preview again.
- **Every day** `preview.sweep` removes previews with no event for **14 days** (a closed event that never arrived), previews of deleted applications, retries teardowns stuck for over an hour and deploys the waiting previews for which a slot is free.
- Deleting the application removes its previews' containers with it; the sweep takes care of their volumes and images.

## Where previews run

By default on the application's server. "Server" on the Previews tab picks another one (for example the build box), for previews created afterwards. A preview keeps the server it was created on until it is removed.

## API

| | |
|---|---|
| `GET /applications/{id}/previews` | the previews (`includeRemoved=true` for history) |
| `GET /applications/{id}/previews/{number}` | one preview |
| `GET` / `PUT /applications/{id}/previews/settings` | `enabled`, `allowForks` (administrators), `serverId`, `maxActive` |
| `POST /applications/{id}/previews/{number}/redeploy` | deploy the preview again at its last commit |
| `DELETE /applications/{id}/previews/{number}` | remove it |
| `GET /applications/{id}/deployments?previewId=` | the deployments of one preview; the application's own list never contains them |

Preview deployments have the trigger `pullRequest` and a `previewId`; they are never rollback points. Notifications for them say "Preview #n of ..." and use alerts of their own, so a failing preview does not open (or a succeeding one resolve) the application's alert.
