package ops

import (
	"context"
	"errors"
	"strings"
	"time"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/dispatch"
	"github.com/mihaim21/aethera-forge/agent/internal/docker"
)

// ---------------------------------------------------------------------------
// Containers
// ---------------------------------------------------------------------------

func stateResult(info *agentv1.ContainerInfo) *agentv1.CommandResult {
	return &agentv1.CommandResult{Result: &agentv1.CommandResult_ContainerState{ContainerState: &agentv1.ContainerStateResult{Container: info}}}
}

func (d *Deps) containerCreate(ctx context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	req := env.Cmd.GetContainerCreate()
	spec := req.GetSpec()
	if spec == nil || spec.GetImage() == "" {
		return nil, invalidArg("container spec with an image is required")
	}
	if name := spec.GetName(); name != "" {
		if _, err := d.Docker.ContainerInspect(ctx, name, false); err == nil {
			if !req.GetReplaceExisting() {
				return nil, dispatch.Errorf(agentv1.ErrorCode_ERROR_CODE_ALREADY_EXISTS, "container %q already exists", name)
			}
			env.Progress("replace", "removing existing container", -1)
			if err := d.Docker.ContainerStop(ctx, name, dur(spec.GetStopTimeout())); err != nil && !isNotFound(err) {
				// A container that is not running cannot be stopped; removal with force copes.
				_ = err
			}
			if err := d.Docker.ContainerRemove(ctx, name, true, false); err != nil && !isNotFound(err) {
				return nil, err
			}
		} else if !isNotFound(err) {
			return nil, err
		}
	}
	if req.GetPullIfMissing() {
		if _, err := d.Docker.ImageInspect(ctx, spec.GetImage()); isNotFound(err) {
			if err := d.pull(ctx, env, spec.GetImage(), req.GetPullAuth(), ""); err != nil {
				return nil, err
			}
		} else if err != nil {
			return nil, err
		}
	}
	env.Progress("create", "creating container", -1)
	id, warnings, err := d.Docker.ContainerCreate(ctx, spec)
	if err != nil {
		if errors.Is(err, docker.ErrConflict) && spec.GetName() != "" {
			return nil, dispatch.Errorf(agentv1.ErrorCode_ERROR_CODE_ALREADY_EXISTS, "container %q already exists", spec.GetName())
		}
		return nil, err
	}
	if req.GetStart() {
		env.Progress("start", "starting container", -1)
		if err := d.Docker.ContainerStart(ctx, id); err != nil {
			return nil, err
		}
	}
	state := agentv1.ContainerState_CONTAINER_STATE_CREATED
	name := spec.GetName()
	if info, err := d.Docker.ContainerInspect(ctx, id, false); err == nil {
		state, name = info.GetState(), info.GetName()
	}
	return &agentv1.CommandResult{Result: &agentv1.CommandResult_ContainerCreate{ContainerCreate: &agentv1.ContainerCreateResult{
		ContainerId: id, Name: name, State: state, Warnings: warnings,
	}}}, nil
}

func (d *Deps) containerStart(ctx context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	ref := env.Cmd.GetContainerStart().GetContainer()
	if ref == "" {
		return nil, invalidArg("container is required")
	}
	if err := d.Docker.ContainerStart(ctx, ref); err != nil {
		return nil, err
	}
	return d.afterState(ctx, ref)
}

func (d *Deps) containerStop(ctx context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	req := env.Cmd.GetContainerStop()
	if req.GetContainer() == "" {
		return nil, invalidArg("container is required")
	}
	if err := d.Docker.ContainerStop(ctx, req.GetContainer(), dur(req.GetTimeout())); err != nil {
		return nil, err
	}
	return d.afterState(ctx, req.GetContainer())
}

func (d *Deps) containerRestart(ctx context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	req := env.Cmd.GetContainerRestart()
	if req.GetContainer() == "" {
		return nil, invalidArg("container is required")
	}
	if err := d.Docker.ContainerRestart(ctx, req.GetContainer(), dur(req.GetTimeout())); err != nil {
		return nil, err
	}
	return d.afterState(ctx, req.GetContainer())
}

func (d *Deps) afterState(ctx context.Context, ref string) (*agentv1.CommandResult, error) {
	info, err := d.Docker.ContainerInspect(ctx, ref, false)
	if err != nil {
		return nil, err
	}
	return stateResult(info), nil
}

// containerRemove succeeds when the container is already gone (idempotent).
func (d *Deps) containerRemove(ctx context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	req := env.Cmd.GetContainerRemove()
	if req.GetContainer() == "" {
		return nil, invalidArg("container is required")
	}
	err := d.Docker.ContainerRemove(ctx, req.GetContainer(), req.GetForce(), req.GetRemoveAnonymousVolumes())
	if err != nil && !isNotFound(err) {
		return nil, err
	}
	return empty(), nil
}

func (d *Deps) containerInspect(ctx context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	req := env.Cmd.GetContainerInspect()
	if req.GetContainer() == "" {
		return nil, invalidArg("container is required")
	}
	info, err := d.Docker.ContainerInspect(ctx, req.GetContainer(), req.GetIncludeRaw())
	if err != nil {
		return nil, err
	}
	return &agentv1.CommandResult{Result: &agentv1.CommandResult_ContainerInspect{ContainerInspect: &agentv1.ContainerInspectResult{Container: info}}}, nil
}

func (d *Deps) containerList(ctx context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	req := env.Cmd.GetContainerList()
	list, err := d.Docker.ContainerList(ctx, req.GetAll(), req.GetLabelFilters(), req.GetNameFilter())
	if err != nil {
		return nil, err
	}
	return &agentv1.CommandResult{Result: &agentv1.CommandResult_ContainerList{ContainerList: &agentv1.ContainerListResult{Containers: list}}}, nil
}

// ---------------------------------------------------------------------------
// Images
// ---------------------------------------------------------------------------

// pull runs a throttled-progress image pull.
func (d *Deps) pull(ctx context.Context, env *dispatch.Env, ref string, auth *agentv1.RegistryAuth, platform string) error {
	last := time.Time{}
	progress := func(line string) {
		if now := time.Now(); now.Sub(last) >= 500*time.Millisecond {
			last = now
			env.Progress("pull", line, -1)
		}
	}
	env.Progress("pull", "pulling "+ref, 0)
	return d.Docker.ImagePull(ctx, ref, auth, platform, progress)
}

func (d *Deps) imagePull(ctx context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	req := env.Cmd.GetImagePull()
	if req.GetReference() == "" || strings.HasPrefix(req.GetReference(), "-") {
		return nil, invalidArg("a valid image reference is required")
	}
	before, berr := d.Docker.ImageInspect(ctx, req.GetReference())
	if berr != nil && !isNotFound(berr) {
		return nil, berr
	}
	if err := d.pull(ctx, env, req.GetReference(), req.GetAuth(), req.GetPlatform()); err != nil {
		return nil, err
	}
	after, err := d.Docker.ImageInspect(ctx, req.GetReference())
	if err != nil {
		return nil, err
	}
	res := &agentv1.ImagePullResult{
		ImageId: after.GetId(), SizeBytes: after.GetSizeBytes(), RepoDigests: after.GetRepoDigests(),
		AlreadyPresent: berr == nil && before.GetId() == after.GetId(),
	}
	repo := req.GetReference()
	if i := strings.LastIndexAny(repo, ":@"); i > strings.LastIndex(repo, "/") {
		repo = repo[:i]
	}
	for _, rd := range after.GetRepoDigests() {
		if strings.HasPrefix(rd, repo+"@") {
			res.Digest = strings.TrimPrefix(rd, repo+"@")
			break
		}
	}
	if res.Digest == "" && len(after.GetRepoDigests()) > 0 {
		if _, dg, ok := strings.Cut(after.GetRepoDigests()[0], "@"); ok {
			res.Digest = dg
		}
	}
	return &agentv1.CommandResult{Result: &agentv1.CommandResult_ImagePull{ImagePull: res}}, nil
}

func (d *Deps) imageList(ctx context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	req := env.Cmd.GetImageList()
	images, err := d.Docker.ImageList(ctx, req.GetIncludeIntermediate(), req.GetLabelFilters(), req.GetReferenceFilter())
	if err != nil {
		return nil, err
	}
	if cons, cerr := d.Docker.ContainerList(ctx, true, nil, ""); cerr == nil {
		users := map[string]int32{}
		for _, c := range cons {
			users[c.GetImageId()]++
		}
		for _, im := range images {
			im.ContainersUsing = users[im.GetId()]
		}
	}
	return &agentv1.CommandResult{Result: &agentv1.CommandResult_ImageList{ImageList: &agentv1.ImageListResult{Images: images}}}, nil
}

func (d *Deps) imageInspect(ctx context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	ref := env.Cmd.GetImageInspect().GetImage()
	if ref == "" {
		return nil, invalidArg("image is required")
	}
	im, err := d.Docker.ImageInspect(ctx, ref)
	if err != nil {
		return nil, err
	}
	return &agentv1.CommandResult{Result: &agentv1.CommandResult_ImageInspect{ImageInspect: &agentv1.ImageInspectResult{Image: im}}}, nil
}

func (d *Deps) imageRemove(ctx context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	req := env.Cmd.GetImageRemove()
	if req.GetImage() == "" {
		return nil, invalidArg("image is required")
	}
	if _, err := d.Docker.ImageRemove(ctx, req.GetImage(), req.GetForce()); err != nil && !isNotFound(err) {
		return nil, err
	}
	return empty(), nil
}

func (d *Deps) imagePrune(ctx context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	req := env.Cmd.GetImagePrune()
	res, err := d.pruneImages(ctx, req.GetAllUnused(), req.GetOlderThan().AsDuration(), req.GetLabelFilters(), req.GetKeep())
	if err != nil {
		return nil, err
	}
	return &agentv1.CommandResult{Result: &agentv1.CommandResult_Prune{Prune: res}}, nil
}

func isDangling(im *agentv1.ImageInfo) bool {
	if len(im.GetRepoTags()) == 0 {
		return true
	}
	for _, t := range im.GetRepoTags() {
		if t != "<none>:<none>" {
			return false
		}
	}
	return true
}

func keepsImage(im *agentv1.ImageInfo, keep []string) bool {
	for _, k := range keep {
		if k == "" {
			continue
		}
		if k == im.GetId() || (len(k) >= 12 && strings.HasPrefix(im.GetId(), "sha256:"+k)) {
			return true
		}
		for _, t := range im.GetRepoTags() {
			if t == k {
				return true
			}
		}
		for _, rd := range im.GetRepoDigests() {
			if rd == k {
				return true
			}
		}
	}
	return false
}

// pruneImages removes images that no container uses (never those in keep).
func (d *Deps) pruneImages(ctx context.Context, allUnused bool, olderThan time.Duration, labels, keep []string) (*agentv1.PruneResult, error) {
	images, err := d.Docker.ImageList(ctx, false, nil, "")
	if err != nil {
		return nil, err
	}
	cons, err := d.Docker.ContainerList(ctx, true, nil, "")
	if err != nil {
		return nil, err
	}
	usedID := map[string]bool{}
	usedRef := map[string]bool{}
	for _, c := range cons {
		usedID[c.GetImageId()] = true
		usedRef[c.GetImage()] = true
	}
	res := &agentv1.PruneResult{}
	cutoff := d.now().Add(-olderThan)
	for _, im := range images {
		if usedID[im.GetId()] || keepsImage(im, keep) || !matchLabels(im.GetLabels(), labels) {
			continue
		}
		inUse := false
		for _, t := range im.GetRepoTags() {
			if usedRef[t] {
				inUse = true
			}
		}
		dangling := isDangling(im)
		if inUse || (!allUnused && !dangling) {
			continue
		}
		if olderThan > 0 && im.GetCreatedAt() != nil && im.GetCreatedAt().AsTime().After(cutoff) {
			continue
		}
		var targets []string
		if dangling {
			targets = []string{im.GetId()}
		} else {
			targets = im.GetRepoTags()
		}
		removed := false
		for _, t := range targets {
			deleted, rerr := d.Docker.ImageRemove(ctx, t, false)
			if rerr != nil {
				if ctx.Err() != nil {
					return res, ctx.Err()
				}
				continue // in use or already gone: skip
			}
			res.Deleted = append(res.Deleted, deleted...)
			removed = true
		}
		if removed {
			res.SpaceReclaimedBytes += im.GetSizeBytes()
		}
	}
	return res, nil
}

// ---------------------------------------------------------------------------
// Volumes
// ---------------------------------------------------------------------------

func (d *Deps) volumeCreate(ctx context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	req := env.Cmd.GetVolumeCreate()
	if req.GetName() == "" {
		return nil, invalidArg("volume name is required")
	}
	vol, err := d.Docker.VolumeCreate(ctx, req)
	if err != nil {
		return nil, err
	}
	return &agentv1.CommandResult{Result: &agentv1.CommandResult_VolumeCreate{VolumeCreate: &agentv1.VolumeCreateResult{Volume: vol}}}, nil
}

func (d *Deps) volumeList(ctx context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	vols, err := d.Docker.VolumeList(ctx, env.Cmd.GetVolumeList().GetLabelFilters())
	if err != nil {
		return nil, err
	}
	if cons, cerr := d.Docker.ContainerList(ctx, true, nil, ""); cerr == nil {
		refs := volumeRefs(cons)
		for _, v := range vols {
			v.RefCount = int32(refs[v.GetName()])
		}
	}
	return &agentv1.CommandResult{Result: &agentv1.CommandResult_VolumeList{VolumeList: &agentv1.VolumeListResult{Volumes: vols}}}, nil
}

func volumeRefs(cons []*agentv1.ContainerInfo) map[string]int {
	refs := map[string]int{}
	for _, c := range cons {
		for _, m := range c.GetMounts() {
			if m.GetType() == agentv1.MountType_MOUNT_TYPE_VOLUME {
				refs[m.GetSource()]++
			}
		}
	}
	return refs
}

func (d *Deps) volumeRemove(ctx context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	req := env.Cmd.GetVolumeRemove()
	if req.GetName() == "" {
		return nil, invalidArg("volume name is required")
	}
	if err := d.Docker.VolumeRemove(ctx, req.GetName(), req.GetForce()); err != nil && !isNotFound(err) {
		return nil, err
	}
	return empty(), nil
}

func (d *Deps) volumePrune(ctx context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	req := env.Cmd.GetVolumePrune()
	res, err := d.pruneVolumes(ctx, func(v *agentv1.VolumeInfo) bool {
		if len(v.GetLabels()) == 0 {
			return req.GetIncludeUnlabelled() // unlabelled volumes are never pruned by default
		}
		return matchLabels(v.GetLabels(), req.GetLabelFilters())
	})
	if err != nil {
		return nil, err
	}
	return &agentv1.CommandResult{Result: &agentv1.CommandResult_Prune{Prune: res}}, nil
}

// pruneVolumes removes unused volumes accepted by pick.
func (d *Deps) pruneVolumes(ctx context.Context, pick func(*agentv1.VolumeInfo) bool) (*agentv1.PruneResult, error) {
	vols, err := d.Docker.VolumeList(ctx, nil)
	if err != nil {
		return nil, err
	}
	cons, err := d.Docker.ContainerList(ctx, true, nil, "")
	if err != nil {
		return nil, err
	}
	refs := volumeRefs(cons)
	res := &agentv1.PruneResult{}
	for _, v := range vols {
		if refs[v.GetName()] > 0 || !pick(v) {
			continue
		}
		size := v.GetSizeBytes()
		if err := d.Docker.VolumeRemove(ctx, v.GetName(), false); err != nil {
			if ctx.Err() != nil {
				return res, ctx.Err()
			}
			continue
		}
		res.Deleted = append(res.Deleted, v.GetName())
		if size > 0 {
			res.SpaceReclaimedBytes += size
		}
	}
	return res, nil
}

func isAnonymousVolume(name string) bool {
	if len(name) != 64 {
		return false
	}
	for _, r := range name {
		if !strings.ContainsRune("0123456789abcdef", r) {
			return false
		}
	}
	return true
}

// ---------------------------------------------------------------------------
// Networks
// ---------------------------------------------------------------------------

func netResult(n *agentv1.NetworkInfo) *agentv1.CommandResult {
	return &agentv1.CommandResult{Result: &agentv1.CommandResult_NetworkCreate{NetworkCreate: &agentv1.NetworkCreateResult{Network: n}}}
}

func (d *Deps) networkCreate(ctx context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	req := env.Cmd.GetNetworkCreate()
	if req.GetName() == "" {
		return nil, invalidArg("network name is required")
	}
	existing, err := d.Docker.NetworkInspect(ctx, req.GetName())
	if err == nil {
		if req.GetIfNotExists() {
			return netResult(existing), nil
		}
		return nil, dispatch.Errorf(agentv1.ErrorCode_ERROR_CODE_ALREADY_EXISTS, "network %q already exists", req.GetName())
	}
	if !isNotFound(err) {
		return nil, err
	}
	n, err := d.Docker.NetworkCreate(ctx, req)
	if err != nil {
		return nil, err
	}
	return netResult(n), nil
}

func (d *Deps) networkList(ctx context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	list, err := d.Docker.NetworkList(ctx, env.Cmd.GetNetworkList().GetLabelFilters())
	if err != nil {
		return nil, err
	}
	return &agentv1.CommandResult{Result: &agentv1.CommandResult_NetworkList{NetworkList: &agentv1.NetworkListResult{Networks: list}}}, nil
}

func (d *Deps) networkRemove(ctx context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	ref := env.Cmd.GetNetworkRemove().GetNetwork()
	if ref == "" {
		return nil, invalidArg("network is required")
	}
	if err := d.Docker.NetworkRemove(ctx, ref); err != nil && !isNotFound(err) {
		return nil, err
	}
	return empty(), nil
}

func (d *Deps) networkConnect(ctx context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	req := env.Cmd.GetNetworkConnect()
	if req.GetNetwork() == "" || req.GetContainer() == "" {
		return nil, invalidArg("network and container are required")
	}
	if err := d.Docker.NetworkConnect(ctx, req); err != nil {
		if errors.Is(err, docker.ErrAlreadyExist) {
			return empty(), nil // already attached: idempotent
		}
		return nil, err
	}
	return empty(), nil
}

func (d *Deps) networkDisconnect(ctx context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	req := env.Cmd.GetNetworkDisconnect()
	if req.GetNetwork() == "" || req.GetContainer() == "" {
		return nil, invalidArg("network and container are required")
	}
	if err := d.Docker.NetworkDisconnect(ctx, req.GetNetwork(), req.GetContainer(), req.GetForce()); err != nil && !isNotFound(err) {
		return nil, err
	}
	return empty(), nil
}

func (d *Deps) networkPrune(ctx context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	req := env.Cmd.GetNetworkPrune()
	res, err := d.pruneNetworks(ctx, req.GetOlderThan().AsDuration(), req.GetLabelFilters())
	if err != nil {
		return nil, err
	}
	return &agentv1.CommandResult{Result: &agentv1.CommandResult_Prune{Prune: res}}, nil
}

func (d *Deps) pruneNetworks(ctx context.Context, olderThan time.Duration, labels []string) (*agentv1.PruneResult, error) {
	nets, err := d.Docker.NetworkList(ctx, nil)
	if err != nil {
		return nil, err
	}
	res := &agentv1.PruneResult{}
	cutoff := d.now().Add(-olderThan)
	for _, n := range nets {
		switch n.GetName() {
		case "bridge", "host", "none":
			continue
		}
		if len(n.GetEndpoints()) > 0 || !matchLabels(n.GetLabels(), labels) {
			continue
		}
		if olderThan > 0 && n.GetCreatedAt() != nil && n.GetCreatedAt().AsTime().After(cutoff) {
			continue
		}
		if err := d.Docker.NetworkRemove(ctx, n.GetId()); err != nil {
			if ctx.Err() != nil {
				return res, ctx.Err()
			}
			continue
		}
		res.Deleted = append(res.Deleted, n.GetName())
	}
	return res, nil
}

// ---------------------------------------------------------------------------
// System prune
// ---------------------------------------------------------------------------

func (d *Deps) systemPrune(ctx context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	req := env.Cmd.GetSystemPrune()
	total := &agentv1.PruneResult{}
	merge := func(r *agentv1.PruneResult) {
		if r != nil {
			total.Deleted = append(total.Deleted, r.Deleted...)
			total.SpaceReclaimedBytes += r.SpaceReclaimedBytes
		}
	}
	older := req.GetOlderThan().AsDuration()

	if req.GetStoppedContainers() {
		env.Progress("containers", "pruning stopped containers", -1)
		cons, err := d.Docker.ContainerList(ctx, true, nil, "")
		if err != nil {
			return nil, err
		}
		cutoff := d.now().Add(-older)
		for _, c := range cons {
			if c.GetState() != agentv1.ContainerState_CONTAINER_STATE_EXITED && c.GetState() != agentv1.ContainerState_CONTAINER_STATE_DEAD {
				continue
			}
			if older > 0 && c.GetCreatedAt() != nil && c.GetCreatedAt().AsTime().After(cutoff) {
				continue
			}
			if err := d.Docker.ContainerRemove(ctx, c.GetId(), false, false); err == nil {
				total.Deleted = append(total.Deleted, c.GetName())
			}
		}
	}
	if req.GetDanglingImages() || req.GetUnusedImages() {
		env.Progress("images", "pruning images", -1)
		r, err := d.pruneImages(ctx, req.GetUnusedImages(), older, nil, req.GetKeepImages())
		if err != nil {
			return nil, err
		}
		merge(r)
	}
	if req.GetUnusedNetworks() {
		env.Progress("networks", "pruning networks", -1)
		r, err := d.pruneNetworks(ctx, older, nil)
		if err != nil {
			return nil, err
		}
		merge(r)
	}
	if req.GetBuildCache() {
		env.Progress("build_cache", "pruning build cache", -1)
		n, err := d.Docker.BuildCachePrune(ctx)
		if err != nil {
			return nil, err
		}
		total.SpaceReclaimedBytes += n
	}
	if req.GetVolumes() {
		env.Progress("volumes", "pruning unused anonymous volumes", -1)
		// Like `docker system prune --volumes`: only anonymous volumes; named
		// volumes need an explicit VolumePrune command.
		r, err := d.pruneVolumes(ctx, func(v *agentv1.VolumeInfo) bool { return isAnonymousVolume(v.GetName()) })
		if err != nil {
			return nil, err
		}
		merge(r)
	}
	return &agentv1.CommandResult{Result: &agentv1.CommandResult_Prune{Prune: total}}, nil
}
