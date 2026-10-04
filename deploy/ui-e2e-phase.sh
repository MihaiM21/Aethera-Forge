# shellcheck shell=bash
# Phase "ui" of deploy/agent-e2e.sh (Phase 4 verification): the full product UI in a real browser.
#
#   E2E_PHASES=ui bash deploy/agent-e2e.sh
#
# Brings up the same stack as the agent phases (real API serving src/web/out, throw-away PostgreSQL/Redis, the real agent against a
# Docker-in-Docker daemon), then runs src/web/scripts/e2e-phase4.mjs (Playwright) through the sign-in, the Create Application
# wizard, a live deployment with its logs, redeploy and rollback, a failing deployment, a Redis service from its template and the
# ops pages, and finally checks with plain HTTP that the proxy really routes the application's domain. Screenshots (dark and light)
# land in src/web/screenshots/phase4. This file is sourced: it relies on the helpers of agent-e2e.sh.
#
#   SKIP_WEB_BUILD=1   reuse the existing src/web/out
#   E2E_UI_OUT         screenshot directory (default src/web/screenshots/phase4)
#   E2E_UI_HOST        host name routed to the application (default ui.localtest.me)

# The proxy bind-mounts a configuration file the agent wrote, and the agent and the Docker daemon are different containers here (on a
# real server they share a host): mounting the agent's state volume into the daemon's container at the same path makes the file visible.
start_dind_shared_state() {
  docker volume create --label "$LABEL" "$PREFIX-ui-state" >/dev/null
  docker pull -q docker:27-dind >/dev/null 2>&1 || true
  docker run -d --privileged --name "$PREFIX-dind" --label "$LABEL" "${HOST_FLAGS[@]}" -e DOCKER_TLS_CERTDIR= -v "$SOCK_VOL:/var/run" -v "$PREFIX-ui-state:/var/lib/aethera" docker:27-dind >/dev/null     || fail "could not start Docker-in-Docker"
  wait_for "the Docker-in-Docker daemon" 90 docker exec "$PREFIX-dind" docker info
  ok "Docker-in-Docker daemon is up (the agent's Docker, sharing the agent's state directory)"
}

phase_ui() {
  info "---- phase ui: the product UI in a real browser"
  command -v node >/dev/null 2>&1 || fail "node is required"
  command -v pnpm >/dev/null 2>&1 || fail "pnpm is required"
  local web="$ROOT/src/web" out="${E2E_UI_OUT:-$ROOT/src/web/screenshots/phase4}" host="${E2E_UI_HOST:-ui.localtest.me}"

  if [[ "${SKIP_WEB_BUILD:-}" == 1 && -f "$web/out/login.html" ]]; then
    ok "web export reused ($web/out)"
  else
    info "building the web export (pnpm install + build)"
    (cd "$web" && pnpm install --frozen-lockfile >"$WORK/pnpm-install.log" 2>&1 && pnpm build >"$WORK/pnpm-build.log" 2>&1) \
      || { tail -n 30 "$WORK/pnpm-build.log" "$WORK/pnpm-install.log" >&2 2>/dev/null; fail "web build failed"; }
    ok "web export built"
  fi

  start_dind_shared_state
  new_database; start_api "$DB_CONN"
  login_owner
  api GET / >/dev/null; [[ "$STATUS" == 200 ]] || fail "the API does not serve the web export at / (HTTP $STATUS)"
  ok "owner created; the API serves the UI at $API"

  api POST /api/v1/servers '{"name":"e2e-server","host":"host.docker.internal","roles":["master","worker"]}'; expect 201 "POST /servers"
  local server; server="$(jqe .id)"
  new_join_token "$server"
  AGENT_NETWORK="container:$PREFIX-dind"
  agent_setup ui
  enroll_agent ui "$TOKEN" "$ENDPOINT" "$PIN" || { cat "$WORK/enroll-ui.log" >&2; fail "aethera-agent enroll failed"; }
  run_agent ui
  wait_for "agent axis available" 60 axis_is "$server" agent available
  wait_for "docker axis available" 60 axis_is "$server" docker available
  ok "agent connected to a Docker-in-Docker daemon"

  info "driving the UI with Playwright (this pulls nginx and redis inside the Docker-in-Docker daemon: allow a few minutes)"
  node "$(hostpath "$web/scripts/e2e-phase4.mjs")" --base "$API" --email "$EMAIL" --password "$PASSWORD" --server e2e-server --host "$host" \
    --out "$(hostpath "$out")" || fail "the browser run failed (screenshots of the steps so far are in $out)"

  # The browser proved the UI; this proves the deployment is real: Traefik inside the daemon routes the host to nginx.
  local body
  body="$(docker exec "$PREFIX-dind" wget -qO- --header "Host: $host" http://127.0.0.1/ 2>/dev/null || true)"
  [[ "$body" == *"Welcome to nginx"* ]] || fail "the proxy does not route $host to the application (got: ${body:0:120})"
  ok "Traefik routes http://$host to the deployed nginx (the page answers)"
  local redis
  redis="$(docker exec "$PREFIX-dind" docker ps --format '{{.Names}} {{.Image}}' | grep -c 'redis:' || true)"
  [[ "$redis" -ge 1 ]] || fail "the Redis service container is not running in the daemon"
  ok "the Redis service container runs next to the application"
}
