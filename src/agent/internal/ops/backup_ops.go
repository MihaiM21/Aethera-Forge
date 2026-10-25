package ops

import (
	"context"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/backup"
	"github.com/mihaim21/aethera-forge/agent/internal/dispatch"
)

func (d *Deps) backupIO(env *dispatch.Env) (backup.IO, func(error)) {
	out, errw, closeLog := d.execLog(env)
	return backup.IO{Out: out, Err: errw, Progress: env.Progress, Masker: env.Masker}, closeLog
}

func (d *Deps) backupRun(ctx context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	io, closeLog := d.backupIO(env)
	res, err := d.Backup.Run(ctx, env.Cmd.GetBackupRun(), io)
	closeLog(err)
	if err != nil {
		return nil, execError(err)
	}
	return &agentv1.CommandResult{Result: &agentv1.CommandResult_BackupRun{BackupRun: res}}, nil
}

func (d *Deps) backupRestore(ctx context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	io, closeLog := d.backupIO(env)
	res, err := d.Backup.Restore(ctx, env.Cmd.GetBackupRestore(), io)
	closeLog(err)
	if err != nil {
		return nil, execError(err)
	}
	return &agentv1.CommandResult{Result: &agentv1.CommandResult_BackupRestore{BackupRestore: res}}, nil
}

func (d *Deps) backupDelete(ctx context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	io, closeLog := d.backupIO(env)
	err := d.Backup.Delete(ctx, env.Cmd.GetBackupDelete(), io)
	closeLog(err)
	if err != nil {
		return nil, execError(err)
	}
	return empty(), nil
}

// registerBackup wires the backup arms when the backup service is configured.
func (d *Deps) registerBackup(r func(string, string, dispatch.Handler)) {
	if d.Backup != nil {
		r("backup_run", CapBackup, d.backupRun)
		r("backup_restore", CapBackup, d.backupRestore)
		r("backup_delete", CapBackup, d.backupDelete)
	}
}

// r1Capabilities lists the capabilities of the configured exec and backup subsystems.
func (d *Deps) r1Capabilities() []string {
	var c []string
	if d.Exec != nil {
		c = append(c, CapExec)
	}
	if d.Backup != nil {
		c = append(c, CapBackup)
	}
	return c
}
