package agent

import (
	"github.com/mihaim21/aethera-forge/agent/internal/backup"
	"github.com/mihaim21/aethera-forge/agent/internal/ops"
)

// wireR1 enables container.exec and backup.v1 (ADR 0010). They need the Docker
// daemon only (helper containers, the exec API) and the state directory for
// local backups and downloads, so they are always available.
func (a *Agent) wireR1(deps *ops.Deps) {
	deps.Exec = &backup.Executor{Docker: a.o.Docker, Policy: a.pol}
	deps.Backup = &backup.Service{Docker: a.o.Docker, Policy: a.pol}
}
