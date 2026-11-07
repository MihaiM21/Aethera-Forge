## What and why

<!-- What changed, and why. Link the issue: Fixes #123 -->

## How I verified it

<!-- The commands you ran (see CONTRIBUTING.md). For anything that crosses a boundary, the matching end-to-end check. Screenshots for UI changes, light and dark. -->

## Checklist

- [ ] Tests added or updated (a bug fix has a test that fails without it)
- [ ] The build and tests pass for what I touched: `dotnet build -warnaserror && dotnet test` (with `AETHERA_TEST_DB` set), `go vet ./... && go test ./...`, `pnpm lint && pnpm test && pnpm build`
- [ ] No secrets in code, logs, errors or test output
- [ ] `CHANGELOG.md` updated under *Unreleased* if users or operators would notice
- [ ] Docs updated, and an ADR if this contradicts or extends one
- [ ] Conventional Commit messages, focused diff
