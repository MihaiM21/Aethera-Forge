# Deploying from CI

For an application whose source is a Docker image, CI can build and push the image and then ask Aethera to deploy **exactly that tag**. Each deployment then records the image it ran, so a rollback re-runs the same image instead of "whatever the moving tag points to now". Design: [ADR 0013](architecture/0013-two-factor-and-pinned-image-deploys.md), part 2.

## The API call

```http
POST /api/v1/applications/{id}/deployments
Authorization: Bearer <token with the deploy scope (and read, to poll the result)>
Content-Type: application/json

{ "imageTag": "3f2a9c1", "reason": "ci: merge #42" }
```

`imageTag` takes:

| Form | Example |
|---|---|
| A tag | `3f2a9c1`, `v1.4.0` |
| A digest | `sha256:0123…` (64 hex characters) |
| A full reference of the application's own image | `ghcr.io/owner/app:3f2a9c1`, `ghcr.io/owner/app@sha256:…` |

The answer is `202 Accepted` with the queued deployment (its `imageTag` and `id`). Errors are `422` with a field error on `/imageTag`:

| Code | When |
|---|---|
| `image_tag.invalid` | Not a tag, digest or image reference |
| `image_tag.repository_mismatch` | A full reference of another repository. Changing the image is a configuration change, not a deploy. |
| `image_tag.not_image_source` | The application builds from git or runs a compose file, or the workload is a service |

The application's configured tag does not change: a deploy without `imageTag` (or **Redeploy** in the UI) runs the configured tag. Moving the application to another server keeps the tag that is running. `reason` is optional free text recorded in the audit log.

## Set up

1. In Aethera, create the application with source **Docker image**, image `ghcr.io/<owner>/<app>`, any tag (it is only the default). For a private package, add a registry for `ghcr.io` (Settings > Registries) with a GitHub token that can read packages, and select it on the image source.
2. Create an API token with the **deploy** and **read** scopes (Settings > API tokens): `deploy` queues the deployment, `read` lets the workflow below poll its status (drop `read` and the polling loop if you do not wait for the result). Two-factor authentication does not affect tokens.
3. In the GitHub repository, add the secrets `AETHERA_URL` (e.g. `https://aethera.example.com`), `AETHERA_TOKEN` and `AETHERA_APP_ID` (the id in the application's URL).

## GitHub Actions example

```yaml
name: deploy
on:
  push:
    branches: [master]

permissions:
  contents: read
  packages: write

jobs:
  deploy:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - uses: docker/login-action@v3
        with:
          registry: ghcr.io
          username: ${{ github.actor }}
          password: ${{ secrets.GITHUB_TOKEN }}

      - uses: docker/build-push-action@v6
        with:
          push: true
          tags: ghcr.io/${{ github.repository }}:${{ github.sha }}

      - name: Deploy this commit
        env:
          AETHERA_URL: ${{ secrets.AETHERA_URL }}
          AETHERA_TOKEN: ${{ secrets.AETHERA_TOKEN }}
          APP_ID: ${{ secrets.AETHERA_APP_ID }}
        run: |
          set -euo pipefail
          deployment=$(curl -fsS -X POST "$AETHERA_URL/api/v1/applications/$APP_ID/deployments" \
            -H "Authorization: Bearer $AETHERA_TOKEN" -H "Content-Type: application/json" \
            -d "{\"imageTag\":\"$GITHUB_SHA\",\"reason\":\"github: $GITHUB_REPOSITORY@$GITHUB_SHA\"}" | jq -r .id)
          echo "deployment $deployment"
          # Wait for the outcome (running = live, failed/cancelled = not deployed).
          for _ in $(seq 1 120); do
            status=$(curl -fsS "$AETHERA_URL/api/v1/deployments/$deployment" -H "Authorization: Bearer $AETHERA_TOKEN" | jq -r .status)
            case "$status" in
              running) echo "deployed"; exit 0 ;;
              failed|cancelled) echo "deployment $status"; exit 1 ;;
            esac
            sleep 5
          done
          echo "timed out waiting for the deployment"; exit 1
```

`github.repository` must be lower case for GHCR; use a fixed `ghcr.io/owner/app` if your repository name has capitals. The repository in the tag must be the application's image, or the call answers `image_tag.repository_mismatch`.

## Rolling back

Every pinned deployment is a rollback point. **Deployments > Roll back** (or `POST /api/v1/deployments/{id}/rollback`) runs that deployment's image again, by tag. Keep the old tags in GHCR for as long as you may want to roll back to them; the server also keeps the images of recent rollback points.
