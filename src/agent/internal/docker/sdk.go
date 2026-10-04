package docker

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"os"
	"os/exec"
	"strconv"
	"strings"
	"syscall"
	"time"

	"github.com/docker/docker/api/types/build"
	"github.com/docker/docker/api/types/container"
	"github.com/docker/docker/api/types/events"
	"github.com/docker/docker/api/types/filters"
	"github.com/docker/docker/api/types/image"
	"github.com/docker/docker/api/types/mount"
	"github.com/docker/docker/api/types/network"
	"github.com/docker/docker/api/types/registry"
	"github.com/docker/docker/api/types/volume"
	"github.com/docker/docker/client"
	"github.com/docker/docker/errdefs"
	"github.com/docker/docker/pkg/stdcopy"
	"github.com/docker/go-connections/nat"
	"google.golang.org/protobuf/types/known/durationpb"
	"google.golang.org/protobuf/types/known/timestamppb"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
)

// SDK implements API on top of the official Docker client.
type SDK struct {
	cli *client.Client
}

// NewSDK connects lazily to host ("" = DOCKER_HOST or the default socket).
func NewSDK(host string) (*SDK, error) {
	opts := []client.Opt{client.WithAPIVersionNegotiation()}
	if host != "" {
		opts = append(opts, client.WithHost(host))
	} else {
		opts = append(opts, client.FromEnv)
	}
	cli, err := client.NewClientWithOpts(opts...)
	if err != nil {
		return nil, err
	}
	return &SDK{cli: cli}, nil
}

var _ API = (*SDK)(nil)

// Close releases the client.
func (s *SDK) Close() error { return s.cli.Close() }

// mapErr wraps SDK errors with the package's sentinel kinds.
func mapErr(err error) error {
	if err == nil {
		return nil
	}
	if errors.Is(err, context.Canceled) || errors.Is(err, context.DeadlineExceeded) {
		return err
	}
	msg := err.Error()
	lower := strings.ToLower(msg)
	switch {
	case errdefs.IsNotFound(err):
		return fmt.Errorf("%w: %s", ErrNotFound, msg)
	case errdefs.IsUnauthorized(err):
		return fmt.Errorf("%w: %s", ErrAuth, msg)
	case errdefs.IsForbidden(err):
		if strings.Contains(lower, "already exists") {
			return fmt.Errorf("%w: %s", ErrAlreadyExist, msg)
		}
		return fmt.Errorf("%w: %s", ErrPermission, msg)
	case errdefs.IsConflict(err):
		return fmt.Errorf("%w: %s", ErrConflict, msg)
	case errdefs.IsInvalidParameter(err):
		return fmt.Errorf("%w: %s", ErrInvalid, msg)
	case client.IsErrConnectionFailed(err), errdefs.IsUnavailable(err):
		return fmt.Errorf("%w: %s", ErrUnavailable, msg)
	case strings.Contains(lower, "port is already allocated"), strings.Contains(lower, "address already in use"):
		return fmt.Errorf("%w: %s", ErrPortInUse, msg)
	case strings.Contains(lower, "permission denied"):
		return fmt.Errorf("%w: %s", ErrPermission, msg)
	}
	return err
}

// ClassifyDaemonError turns a ping/connect error into the DockerStatus axis.
// installed says whether a docker binary or daemon files exist on the host.
func ClassifyDaemonError(err error, installed bool) agentv1.DockerStatus {
	if err == nil {
		return agentv1.DockerStatus_DOCKER_STATUS_RUNNING
	}
	lower := strings.ToLower(err.Error())
	switch {
	case errors.Is(err, os.ErrPermission), errors.Is(err, syscall.EACCES), strings.Contains(lower, "permission denied"):
		return agentv1.DockerStatus_DOCKER_STATUS_PERMISSION_DENIED
	case errors.Is(err, context.DeadlineExceeded), strings.Contains(lower, "deadline exceeded"), strings.Contains(lower, "timeout"):
		return agentv1.DockerStatus_DOCKER_STATUS_UNREACHABLE
	case errors.Is(err, os.ErrNotExist), errors.Is(err, syscall.ENOENT), errors.Is(err, syscall.ECONNREFUSED),
		strings.Contains(lower, "no such file"), strings.Contains(lower, "connection refused"),
		strings.Contains(lower, "cannot connect to the docker daemon"), strings.Contains(lower, "is the docker daemon running"):
		if installed {
			return agentv1.DockerStatus_DOCKER_STATUS_STOPPED
		}
		return agentv1.DockerStatus_DOCKER_STATUS_NOT_INSTALLED
	}
	return agentv1.DockerStatus_DOCKER_STATUS_UNREACHABLE
}

func dockerInstalled() bool {
	if _, err := exec.LookPath("docker"); err == nil {
		return true
	}
	if _, err := exec.LookPath("dockerd"); err == nil {
		return true
	}
	for _, p := range []string{"/usr/bin/dockerd", "/usr/local/bin/dockerd", "/lib/systemd/system/docker.service", "/usr/lib/systemd/system/docker.service"} {
		if _, err := os.Stat(p); err == nil {
			return true
		}
	}
	return false
}

// Status pings the daemon with a short timeout.
func (s *SDK) Status(ctx context.Context) (agentv1.DockerStatus, string) {
	pctx, cancel := context.WithTimeout(ctx, 5*time.Second)
	defer cancel()
	if _, err := s.cli.Ping(pctx); err != nil {
		var ne net.Error
		if errors.As(err, &ne) && ne.Timeout() {
			return agentv1.DockerStatus_DOCKER_STATUS_UNREACHABLE, "docker daemon did not answer in time"
		}
		return ClassifyDaemonError(err, dockerInstalled()), err.Error()
	}
	return agentv1.DockerStatus_DOCKER_STATUS_RUNNING, ""
}

func (s *SDK) Info(ctx context.Context) (*agentv1.DockerInfo, error) {
	info, err := s.cli.Info(ctx)
	if err != nil {
		return nil, mapErr(err)
	}
	out := &agentv1.DockerInfo{
		Status:            agentv1.DockerStatus_DOCKER_STATUS_RUNNING,
		Version:           info.ServerVersion,
		StorageDriver:     info.Driver,
		CgroupVersion:     info.CgroupVersion,
		DockerRootDir:     info.DockerRootDir,
		ContainersRunning: uint32(max(info.ContainersRunning, 0)),
		ContainersStopped: uint32(max(info.ContainersStopped, 0)),
		ImageCount:        uint32(max(info.Images, 0)),
		SwarmActive:       info.Swarm.LocalNodeState == "active",
	}
	for _, o := range info.SecurityOptions {
		if strings.Contains(o, "rootless") {
			out.Rootless = true
		}
	}
	out.ApiVersion = s.cli.ClientVersion()
	return out, nil
}

// ---------------------------------------------------------------------------
// Containers
// ---------------------------------------------------------------------------

func labelFilter(args *filters.Args, labels []string) {
	for _, l := range labels {
		args.Add("label", l)
	}
}

func (s *SDK) ContainerList(ctx context.Context, all bool, labelFilters []string, nameFilter string) ([]*agentv1.ContainerInfo, error) {
	f := filters.NewArgs()
	labelFilter(&f, labelFilters)
	if nameFilter != "" {
		f.Add("name", nameFilter)
	}
	list, err := s.cli.ContainerList(ctx, container.ListOptions{All: all, Filters: f})
	if err != nil {
		return nil, mapErr(err)
	}
	out := make([]*agentv1.ContainerInfo, 0, len(list))
	for _, c := range list {
		out = append(out, summaryToInfo(c))
	}
	return out, nil
}

func parseState(s string) agentv1.ContainerState {
	switch s {
	case "created":
		return agentv1.ContainerState_CONTAINER_STATE_CREATED
	case "running":
		return agentv1.ContainerState_CONTAINER_STATE_RUNNING
	case "paused":
		return agentv1.ContainerState_CONTAINER_STATE_PAUSED
	case "restarting":
		return agentv1.ContainerState_CONTAINER_STATE_RESTARTING
	case "removing":
		return agentv1.ContainerState_CONTAINER_STATE_REMOVING
	case "exited":
		return agentv1.ContainerState_CONTAINER_STATE_EXITED
	case "dead":
		return agentv1.ContainerState_CONTAINER_STATE_DEAD
	}
	return agentv1.ContainerState_CONTAINER_STATE_UNSPECIFIED
}

func parseHealth(s string) agentv1.ContainerHealth {
	switch s {
	case "starting":
		return agentv1.ContainerHealth_CONTAINER_HEALTH_STARTING
	case "healthy":
		return agentv1.ContainerHealth_CONTAINER_HEALTH_HEALTHY
	case "unhealthy":
		return agentv1.ContainerHealth_CONTAINER_HEALTH_UNHEALTHY
	case "none", "":
		return agentv1.ContainerHealth_CONTAINER_HEALTH_NONE
	}
	return agentv1.ContainerHealth_CONTAINER_HEALTH_UNSPECIFIED
}

func parseProto(p string) agentv1.PortProtocol {
	switch strings.ToLower(p) {
	case "udp":
		return agentv1.PortProtocol_PORT_PROTOCOL_UDP
	case "sctp":
		return agentv1.PortProtocol_PORT_PROTOCOL_SCTP
	}
	return agentv1.PortProtocol_PORT_PROTOCOL_TCP
}

func summaryToInfo(c container.Summary) *agentv1.ContainerInfo {
	name := ""
	if len(c.Names) > 0 {
		name = strings.TrimPrefix(c.Names[0], "/")
	}
	info := &agentv1.ContainerInfo{
		Id:        c.ID,
		Name:      name,
		Image:     c.Image,
		ImageId:   c.ImageID,
		State:     parseState(c.State),
		Status:    c.Status,
		Health:    healthFromStatus(c.Status),
		CreatedAt: timestamppb.New(time.Unix(c.Created, 0)),
		Labels:    c.Labels,
	}
	for _, p := range c.Ports {
		info.Ports = append(info.Ports, &agentv1.PortMapping{
			ContainerPort: uint32(p.PrivatePort),
			HostPort:      uint32(p.PublicPort),
			HostIp:        p.IP,
			Protocol:      parseProto(p.Type),
		})
	}
	for _, m := range c.Mounts {
		vm := &agentv1.VolumeMount{Target: m.Destination, ReadOnly: !m.RW}
		switch m.Type {
		case mount.TypeVolume:
			vm.Type, vm.Source = agentv1.MountType_MOUNT_TYPE_VOLUME, m.Name
		case mount.TypeBind:
			vm.Type, vm.Source = agentv1.MountType_MOUNT_TYPE_BIND, m.Source
		case mount.TypeTmpfs:
			vm.Type = agentv1.MountType_MOUNT_TYPE_TMPFS
		}
		info.Mounts = append(info.Mounts, vm)
	}
	if c.NetworkSettings != nil {
		for name, ep := range c.NetworkSettings.Networks {
			info.Networks = append(info.Networks, &agentv1.ContainerNetworkInfo{
				Network: name, IpAddress: ep.IPAddress, MacAddress: ep.MacAddress, Aliases: ep.Aliases,
			})
		}
	}
	return info
}

func healthFromStatus(status string) agentv1.ContainerHealth {
	switch {
	case strings.Contains(status, "(unhealthy)"):
		return agentv1.ContainerHealth_CONTAINER_HEALTH_UNHEALTHY
	case strings.Contains(status, "(healthy)"):
		return agentv1.ContainerHealth_CONTAINER_HEALTH_HEALTHY
	case strings.Contains(status, "(health: starting)"):
		return agentv1.ContainerHealth_CONTAINER_HEALTH_STARTING
	}
	return agentv1.ContainerHealth_CONTAINER_HEALTH_NONE
}

func parseDockerTime(s string) *timestamppb.Timestamp {
	t, err := time.Parse(time.RFC3339Nano, s)
	if err != nil || t.Year() <= 1 {
		return nil
	}
	return timestamppb.New(t)
}

func (s *SDK) ContainerInspect(ctx context.Context, ref string, raw bool) (*agentv1.ContainerInfo, error) {
	resp, body, err := s.cli.ContainerInspectWithRaw(ctx, ref, false)
	if err != nil {
		return nil, mapErr(err)
	}
	info := &agentv1.ContainerInfo{
		Id:      resp.ID,
		Name:    strings.TrimPrefix(resp.Name, "/"),
		ImageId: resp.Image,
		Labels:  nil,
	}
	if resp.Config != nil {
		info.Image = resp.Config.Image
		info.Labels = resp.Config.Labels
	}
	info.CreatedAt = parseDockerTime(resp.Created)
	info.RestartCount = uint32(max(resp.RestartCount, 0))
	info.Health = agentv1.ContainerHealth_CONTAINER_HEALTH_NONE
	if st := resp.State; st != nil {
		info.State = parseState(st.Status)
		info.ExitCode = int32(st.ExitCode)
		info.OomKilled = st.OOMKilled
		info.StartedAt = parseDockerTime(st.StartedAt)
		info.FinishedAt = parseDockerTime(st.FinishedAt)
		if st.Health != nil {
			info.Health = parseHealth(st.Health.Status)
		}
		info.Status = st.Status
	}
	for _, m := range resp.Mounts {
		vm := &agentv1.VolumeMount{Target: m.Destination, ReadOnly: !m.RW}
		switch m.Type {
		case mount.TypeVolume:
			vm.Type, vm.Source = agentv1.MountType_MOUNT_TYPE_VOLUME, m.Name
		case mount.TypeBind:
			vm.Type, vm.Source = agentv1.MountType_MOUNT_TYPE_BIND, m.Source
		case mount.TypeTmpfs:
			vm.Type = agentv1.MountType_MOUNT_TYPE_TMPFS
		}
		info.Mounts = append(info.Mounts, vm)
	}
	if resp.NetworkSettings != nil {
		for name, ep := range resp.NetworkSettings.Networks {
			info.Networks = append(info.Networks, &agentv1.ContainerNetworkInfo{
				Network: name, IpAddress: ep.IPAddress, MacAddress: ep.MacAddress, Aliases: ep.Aliases,
			})
		}
		for port, bindings := range resp.NetworkSettings.Ports {
			for _, b := range bindings {
				hp, _ := strconv.Atoi(b.HostPort)
				info.Ports = append(info.Ports, &agentv1.PortMapping{
					ContainerPort: uint32(port.Int()),
					HostPort:      uint32(hp),
					HostIp:        b.HostIP,
					Protocol:      parseProto(port.Proto()),
				})
			}
		}
	}
	if raw {
		stripped, serr := StripInspectEnv(body)
		if serr != nil {
			return nil, fmt.Errorf("strip inspect env: %w", serr)
		}
		info.InspectJson = stripped
	}
	return info, nil
}

func envList(vars []*agentv1.EnvVar) []string {
	out := make([]string, 0, len(vars))
	for _, e := range vars {
		v := e.GetPlain()
		if sec := e.GetSecret(); sec != nil {
			v = sec.GetValue()
		}
		out = append(out, e.GetName()+"="+v)
	}
	return out
}

func secs(d *durationpb.Duration) *int {
	if d == nil {
		return nil
	}
	v := int(d.AsDuration().Round(time.Second) / time.Second)
	return &v
}

func (s *SDK) ContainerCreate(ctx context.Context, spec *agentv1.ContainerSpec) (string, []string, error) {
	cfg := &container.Config{
		Image:        spec.GetImage(),
		Hostname:     spec.GetHostname(),
		Entrypoint:   spec.GetEntrypoint(),
		Cmd:          spec.GetCommand(),
		WorkingDir:   spec.GetWorkingDir(),
		User:         spec.GetUser(),
		Env:          envList(spec.GetEnv()),
		Labels:       spec.GetLabels(),
		StopSignal:   spec.GetStopSignal(),
		StopTimeout:  secs(spec.GetStopTimeout()),
		ExposedPorts: nat.PortSet{},
	}
	if hc := spec.GetHealthcheck(); hc != nil {
		cfg.Healthcheck = &container.HealthConfig{
			Test:          hc.GetTest(),
			Interval:      hc.GetInterval().AsDuration(),
			Timeout:       hc.GetTimeout().AsDuration(),
			Retries:       int(hc.GetRetries()),
			StartPeriod:   hc.GetStartPeriod().AsDuration(),
			StartInterval: hc.GetStartInterval().AsDuration(),
		}
	}
	host := &container.HostConfig{
		PortBindings:   nat.PortMap{},
		ReadonlyRootfs: spec.GetReadOnlyRootFs(),
		CapDrop:        spec.GetCapDrop(),
		ExtraHosts:     spec.GetExtraHosts(),
	}
	if spec.GetInit() {
		t := true
		host.Init = &t
	}
	for _, p := range spec.GetPorts() {
		proto := "tcp"
		switch p.GetProtocol() {
		case agentv1.PortProtocol_PORT_PROTOCOL_UDP:
			proto = "udp"
		case agentv1.PortProtocol_PORT_PROTOCOL_SCTP:
			proto = "sctp"
		}
		port := nat.Port(fmt.Sprintf("%d/%s", p.GetContainerPort(), proto))
		cfg.ExposedPorts[port] = struct{}{}
		if p.GetHostPort() != 0 {
			host.PortBindings[port] = append(host.PortBindings[port], nat.PortBinding{
				HostIP: p.GetHostIp(), HostPort: strconv.Itoa(int(p.GetHostPort())),
			})
		}
	}
	for _, m := range spec.GetMounts() {
		mm := mount.Mount{Target: m.GetTarget(), Source: m.GetSource(), ReadOnly: m.GetReadOnly()}
		switch m.GetType() {
		case agentv1.MountType_MOUNT_TYPE_VOLUME:
			mm.Type = mount.TypeVolume
		case agentv1.MountType_MOUNT_TYPE_BIND:
			mm.Type = mount.TypeBind
		case agentv1.MountType_MOUNT_TYPE_TMPFS:
			mm.Type = mount.TypeTmpfs
			mm.Source = ""
			if sz := m.GetTmpfsSizeBytes(); sz > 0 {
				mm.TmpfsOptions = &mount.TmpfsOptions{SizeBytes: sz}
			}
		default:
			return "", nil, fmt.Errorf("%w: mount type missing", ErrInvalid)
		}
		host.Mounts = append(host.Mounts, mm)
	}
	if r := spec.GetResources(); r != nil {
		host.Resources.NanoCPUs = int64(r.GetCpuLimitCores() * 1e9)
		host.Resources.CPUShares = int64(r.GetCpuReservationCores() * 1024)
		host.Resources.Memory = r.GetMemoryLimitBytes()
		host.Resources.MemoryReservation = r.GetMemoryReservationBytes()
		host.Resources.MemorySwap = r.GetMemorySwapLimitBytes()
		host.Resources.CpusetCpus = r.GetCpusetCpus()
		if pl := r.GetPidsLimit(); pl != 0 {
			host.Resources.PidsLimit = &pl
		}
	}
	if rp := spec.GetRestartPolicy(); rp != nil {
		host.RestartPolicy = container.RestartPolicy{MaximumRetryCount: int(rp.GetMaximumRetryCount())}
		switch rp.GetName() {
		case agentv1.RestartPolicyName_RESTART_POLICY_NAME_ALWAYS:
			host.RestartPolicy.Name = container.RestartPolicyAlways
		case agentv1.RestartPolicyName_RESTART_POLICY_NAME_ON_FAILURE:
			host.RestartPolicy.Name = container.RestartPolicyOnFailure
		case agentv1.RestartPolicyName_RESTART_POLICY_NAME_UNLESS_STOPPED:
			host.RestartPolicy.Name = container.RestartPolicyUnlessStopped
		default:
			host.RestartPolicy.Name = container.RestartPolicyDisabled
			host.RestartPolicy.MaximumRetryCount = 0
		}
	}
	if lc := spec.GetLogConfig(); lc != nil && lc.GetDriver() != "" {
		host.LogConfig = container.LogConfig{Type: lc.GetDriver(), Config: lc.GetOptions()}
	}

	netCfg := &network.NetworkingConfig{EndpointsConfig: map[string]*network.EndpointSettings{}}
	nets := spec.GetNetworks()
	if len(nets) > 0 {
		first := nets[0]
		host.NetworkMode = container.NetworkMode(first.GetNetwork())
		es := &network.EndpointSettings{Aliases: first.GetAliases()}
		if ip := first.GetIpv4Address(); ip != "" {
			es.IPAMConfig = &network.EndpointIPAMConfig{IPv4Address: ip}
		}
		netCfg.EndpointsConfig[first.GetNetwork()] = es
	}

	resp, err := s.cli.ContainerCreate(ctx, cfg, host, netCfg, nil, spec.GetName())
	if err != nil {
		return "", nil, mapErr(err)
	}
	for _, extra := range nets[min(1, len(nets)):] {
		if err := s.NetworkConnect(ctx, &agentv1.NetworkConnect{
			Network: extra.GetNetwork(), Container: resp.ID, Aliases: extra.GetAliases(), Ipv4Address: extra.GetIpv4Address(),
		}); err != nil {
			_ = s.cli.ContainerRemove(context.WithoutCancel(ctx), resp.ID, container.RemoveOptions{Force: true})
			return "", nil, err
		}
	}
	return resp.ID, resp.Warnings, nil
}

func (s *SDK) ContainerStart(ctx context.Context, ref string) error {
	return mapErr(s.cli.ContainerStart(ctx, ref, container.StartOptions{}))
}

func stopOpts(t *time.Duration) container.StopOptions {
	if t == nil {
		return container.StopOptions{}
	}
	v := int(t.Round(time.Second) / time.Second)
	return container.StopOptions{Timeout: &v}
}

func (s *SDK) ContainerStop(ctx context.Context, ref string, t *time.Duration) error {
	return mapErr(s.cli.ContainerStop(ctx, ref, stopOpts(t)))
}

func (s *SDK) ContainerRestart(ctx context.Context, ref string, t *time.Duration) error {
	return mapErr(s.cli.ContainerRestart(ctx, ref, stopOpts(t)))
}

func (s *SDK) ContainerRemove(ctx context.Context, ref string, force, vols bool) error {
	return mapErr(s.cli.ContainerRemove(ctx, ref, container.RemoveOptions{Force: force, RemoveVolumes: vols}))
}

func (s *SDK) ContainerStats(ctx context.Context, id string) (*Stats, error) {
	resp, err := s.cli.ContainerStats(ctx, id, false)
	if err != nil {
		return nil, mapErr(err)
	}
	defer resp.Body.Close()
	var st container.StatsResponse
	if err := json.NewDecoder(resp.Body).Decode(&st); err != nil {
		return nil, err
	}
	out := &Stats{
		MemLimitBytes: int64(st.MemoryStats.Limit),
		PIDs:          uint32(st.PidsStats.Current),
	}
	used := st.MemoryStats.Usage
	if v, ok := st.MemoryStats.Stats["inactive_file"]; ok && v < used {
		used -= v
	} else if v, ok := st.MemoryStats.Stats["total_inactive_file"]; ok && v < used {
		used -= v
	}
	out.MemUsedBytes = int64(used)
	cpuDelta := float64(st.CPUStats.CPUUsage.TotalUsage) - float64(st.PreCPUStats.CPUUsage.TotalUsage)
	sysDelta := float64(st.CPUStats.SystemUsage) - float64(st.PreCPUStats.SystemUsage)
	cpus := float64(st.CPUStats.OnlineCPUs)
	if cpus == 0 {
		cpus = float64(len(st.CPUStats.CPUUsage.PercpuUsage))
	}
	if sysDelta > 0 && cpuDelta > 0 {
		out.CPUPercent = cpuDelta / sysDelta * cpus * 100
	}
	for _, n := range st.Networks {
		out.NetRxBytes += n.RxBytes
		out.NetTxBytes += n.TxBytes
	}
	for _, b := range st.BlkioStats.IoServiceBytesRecursive {
		switch strings.ToLower(b.Op) {
		case "read":
			out.BlockRead += b.Value
		case "write":
			out.BlockWrite += b.Value
		}
	}
	return out, nil
}

func (s *SDK) ContainerLogs(ctx context.Context, ref string, o LogOptions, stdout, stderr io.Writer) error {
	opts := container.LogsOptions{ShowStdout: o.Stdout, ShowStderr: o.Stderr, Follow: o.Follow}
	if !o.Since.IsZero() {
		opts.Since = strconv.FormatInt(o.Since.Unix(), 10) + "." + fmt.Sprintf("%09d", o.Since.Nanosecond())
	}
	if o.Tail > 0 {
		opts.Tail = strconv.Itoa(o.Tail)
	}
	insp, err := s.cli.ContainerInspect(ctx, ref)
	if err != nil {
		return mapErr(err)
	}
	rc, err := s.cli.ContainerLogs(ctx, ref, opts)
	if err != nil {
		return mapErr(err)
	}
	defer rc.Close()
	if insp.Config != nil && insp.Config.Tty {
		_, err = io.Copy(stdout, rc)
	} else {
		_, err = stdcopy.StdCopy(stdout, stderr, rc)
	}
	if err != nil && ctx.Err() != nil {
		return ctx.Err()
	}
	return err
}

// ---------------------------------------------------------------------------
// Images
// ---------------------------------------------------------------------------

func encodeAuth(a *agentv1.RegistryAuth) (string, error) {
	if a == nil {
		return "", nil
	}
	return registry.EncodeAuthConfig(registry.AuthConfig{
		Username:      a.GetUsername(),
		Password:      a.GetPassword().GetValue(),
		ServerAddress: a.GetServer(),
		IdentityToken: a.GetIdentityToken().GetValue(),
	})
}

func (s *SDK) ImagePull(ctx context.Context, ref string, auth *agentv1.RegistryAuth, platform string, progress func(string)) error {
	enc, err := encodeAuth(auth)
	if err != nil {
		return err
	}
	rc, err := s.cli.ImagePull(ctx, ref, image.PullOptions{RegistryAuth: enc, Platform: platform})
	if err != nil {
		return pullErr(err)
	}
	defer rc.Close()
	sc := bufio.NewScanner(rc)
	sc.Buffer(make([]byte, 0, 64*1024), 1<<20)
	for sc.Scan() {
		var msg struct {
			Status string `json:"status"`
			ID     string `json:"id"`
			Error  string `json:"error"`
		}
		if json.Unmarshal(sc.Bytes(), &msg) != nil {
			continue
		}
		if msg.Error != "" {
			return pullErr(errors.New(msg.Error))
		}
		if progress != nil && msg.Status != "" {
			line := msg.Status
			if msg.ID != "" {
				line = msg.ID + ": " + line
			}
			progress(line)
		}
	}
	if err := sc.Err(); err != nil {
		if ctx.Err() != nil {
			return ctx.Err()
		}
		return pullErr(err)
	}
	return nil
}

func pullErr(err error) error {
	if errors.Is(err, context.Canceled) || errors.Is(err, context.DeadlineExceeded) {
		return err
	}
	lower := strings.ToLower(err.Error())
	if errdefs.IsUnauthorized(err) || strings.Contains(lower, "unauthorized") || strings.Contains(lower, "authentication required") || strings.Contains(lower, "denied") {
		return fmt.Errorf("%w: %s", ErrAuth, err.Error())
	}
	if client.IsErrConnectionFailed(err) {
		return fmt.Errorf("%w: %s", ErrUnavailable, err.Error())
	}
	return fmt.Errorf("%w: %s", ErrPullFailed, err.Error())
}

func (s *SDK) ImageList(ctx context.Context, all bool, labelFilters []string, reference string) ([]*agentv1.ImageInfo, error) {
	f := filters.NewArgs()
	labelFilter(&f, labelFilters)
	if reference != "" {
		f.Add("reference", reference)
	}
	list, err := s.cli.ImageList(ctx, image.ListOptions{All: all, Filters: f})
	if err != nil {
		return nil, mapErr(err)
	}
	out := make([]*agentv1.ImageInfo, 0, len(list))
	for _, i := range list {
		out = append(out, &agentv1.ImageInfo{
			Id: i.ID, RepoTags: i.RepoTags, RepoDigests: i.RepoDigests, SizeBytes: i.Size,
			CreatedAt: timestamppb.New(time.Unix(i.Created, 0)), Labels: i.Labels, ContainersUsing: -1,
		})
	}
	return out, nil
}

func (s *SDK) ImageInspect(ctx context.Context, ref string) (*agentv1.ImageInfo, error) {
	i, err := s.cli.ImageInspect(ctx, ref)
	if err != nil {
		return nil, mapErr(err)
	}
	info := &agentv1.ImageInfo{
		Id: i.ID, RepoTags: i.RepoTags, RepoDigests: i.RepoDigests, SizeBytes: i.Size,
		CreatedAt: parseDockerTime(i.Created), Architecture: i.Architecture, Os: i.Os, ContainersUsing: -1,
	}
	if i.Config != nil {
		info.Labels = i.Config.Labels
	}
	return info, nil
}

func (s *SDK) ImageRemove(ctx context.Context, ref string, force bool) ([]string, error) {
	items, err := s.cli.ImageRemove(ctx, ref, image.RemoveOptions{Force: force, PruneChildren: true})
	if err != nil {
		return nil, mapErr(err)
	}
	var out []string
	for _, it := range items {
		if it.Deleted != "" {
			out = append(out, it.Deleted)
		}
		if it.Untagged != "" {
			out = append(out, it.Untagged)
		}
	}
	return out, nil
}

// ---------------------------------------------------------------------------
// Volumes
// ---------------------------------------------------------------------------

func volToInfo(v *volume.Volume) *agentv1.VolumeInfo {
	info := &agentv1.VolumeInfo{
		Name: v.Name, Driver: v.Driver, Mountpoint: v.Mountpoint, Labels: v.Labels,
		CreatedAt: parseDockerTime(v.CreatedAt), SizeBytes: -1, RefCount: -1,
	}
	if v.UsageData != nil {
		info.SizeBytes, info.RefCount = v.UsageData.Size, int32(v.UsageData.RefCount)
	}
	return info
}

func (s *SDK) VolumeList(ctx context.Context, labelFilters []string) ([]*agentv1.VolumeInfo, error) {
	f := filters.NewArgs()
	labelFilter(&f, labelFilters)
	resp, err := s.cli.VolumeList(ctx, volume.ListOptions{Filters: f})
	if err != nil {
		return nil, mapErr(err)
	}
	out := make([]*agentv1.VolumeInfo, 0, len(resp.Volumes))
	for _, v := range resp.Volumes {
		out = append(out, volToInfo(v))
	}
	return out, nil
}

func (s *SDK) VolumeInspect(ctx context.Context, name string) (*agentv1.VolumeInfo, error) {
	v, err := s.cli.VolumeInspect(ctx, name)
	if err != nil {
		return nil, mapErr(err)
	}
	return volToInfo(&v), nil
}

func (s *SDK) VolumeCreate(ctx context.Context, req *agentv1.VolumeCreate) (*agentv1.VolumeInfo, error) {
	v, err := s.cli.VolumeCreate(ctx, volume.CreateOptions{
		Name: req.GetName(), Driver: req.GetDriver(), DriverOpts: req.GetDriverOptions(), Labels: req.GetLabels(),
	})
	if err != nil {
		return nil, mapErr(err)
	}
	return volToInfo(&v), nil
}

func (s *SDK) VolumeRemove(ctx context.Context, name string, force bool) error {
	return mapErr(s.cli.VolumeRemove(ctx, name, force))
}

// ---------------------------------------------------------------------------
// Networks
// ---------------------------------------------------------------------------

func netToInfo(n network.Inspect) *agentv1.NetworkInfo {
	info := &agentv1.NetworkInfo{
		Id: n.ID, Name: n.Name, Driver: n.Driver, Scope: n.Scope, Internal: n.Internal,
		Attachable: n.Attachable, Ipv6: n.EnableIPv6, Labels: n.Labels, CreatedAt: timestamppb.New(n.Created),
	}
	for _, c := range n.IPAM.Config {
		info.Ipam = append(info.Ipam, &agentv1.IpamConfig{Subnet: c.Subnet, Gateway: c.Gateway})
	}
	for id, ep := range n.Containers {
		info.Endpoints = append(info.Endpoints, &agentv1.NetworkEndpoint{
			ContainerId: id, ContainerName: ep.Name, Ipv4Address: strings.SplitN(ep.IPv4Address, "/", 2)[0],
		})
	}
	return info
}

func (s *SDK) NetworkList(ctx context.Context, labelFilters []string) ([]*agentv1.NetworkInfo, error) {
	f := filters.NewArgs()
	labelFilter(&f, labelFilters)
	list, err := s.cli.NetworkList(ctx, network.ListOptions{Filters: f})
	if err != nil {
		return nil, mapErr(err)
	}
	out := make([]*agentv1.NetworkInfo, 0, len(list))
	for _, n := range list {
		// The list call omits endpoints; inspect fills them in.
		if full, ierr := s.cli.NetworkInspect(ctx, n.ID, network.InspectOptions{}); ierr == nil {
			n = full
		}
		out = append(out, netToInfo(n))
	}
	return out, nil
}

func (s *SDK) NetworkInspect(ctx context.Context, ref string) (*agentv1.NetworkInfo, error) {
	n, err := s.cli.NetworkInspect(ctx, ref, network.InspectOptions{})
	if err != nil {
		return nil, mapErr(err)
	}
	return netToInfo(n), nil
}

func (s *SDK) NetworkCreate(ctx context.Context, req *agentv1.NetworkCreate) (*agentv1.NetworkInfo, error) {
	opts := network.CreateOptions{
		Driver: req.GetDriver(), Internal: req.GetInternal(), Attachable: req.GetAttachable(),
		Options: req.GetOptions(), Labels: req.GetLabels(),
	}
	if req.GetIpv6() {
		t := true
		opts.EnableIPv6 = &t
	}
	if len(req.GetIpam()) > 0 {
		opts.IPAM = &network.IPAM{}
		for _, c := range req.GetIpam() {
			opts.IPAM.Config = append(opts.IPAM.Config, network.IPAMConfig{Subnet: c.GetSubnet(), Gateway: c.GetGateway()})
		}
	}
	resp, err := s.cli.NetworkCreate(ctx, req.GetName(), opts)
	if err != nil {
		return nil, mapErr(err)
	}
	return s.NetworkInspect(ctx, resp.ID)
}

func (s *SDK) NetworkRemove(ctx context.Context, ref string) error {
	return mapErr(s.cli.NetworkRemove(ctx, ref))
}

func (s *SDK) NetworkConnect(ctx context.Context, req *agentv1.NetworkConnect) error {
	es := &network.EndpointSettings{Aliases: req.GetAliases()}
	if ip := req.GetIpv4Address(); ip != "" {
		es.IPAMConfig = &network.EndpointIPAMConfig{IPv4Address: ip}
	}
	return mapErr(s.cli.NetworkConnect(ctx, req.GetNetwork(), req.GetContainer(), es))
}

func (s *SDK) NetworkDisconnect(ctx context.Context, net, ctr string, force bool) error {
	return mapErr(s.cli.NetworkDisconnect(ctx, net, ctr, force))
}

func (s *SDK) BuildCachePrune(ctx context.Context) (int64, error) {
	rep, err := s.cli.BuildCachePrune(ctx, build.CachePruneOptions{All: true})
	if err != nil {
		return 0, mapErr(err)
	}
	return int64(rep.SpaceReclaimed), nil
}

// ---------------------------------------------------------------------------
// Events
// ---------------------------------------------------------------------------

func (s *SDK) Events(ctx context.Context) (<-chan Event, <-chan error) {
	out := make(chan Event, 64)
	errc := make(chan error, 1)
	f := filters.NewArgs(filters.Arg("type", string(events.ContainerEventType)))
	msgs, errs := s.cli.Events(ctx, events.ListOptions{Filters: f})
	go func() {
		defer close(out)
		for {
			select {
			case <-ctx.Done():
				return
			case err := <-errs:
				if err != nil {
					errc <- mapErr(err)
				}
				return
			case m, ok := <-msgs:
				if !ok {
					return
				}
				ev := Event{
					Action:      string(m.Action),
					ContainerID: m.Actor.ID,
					Attributes:  m.Actor.Attributes,
					Time:        time.Unix(0, m.TimeNano),
				}
				if m.TimeNano == 0 {
					ev.Time = time.Unix(m.Time, 0)
				}
				ev.Name = m.Actor.Attributes["name"]
				select {
				case out <- ev:
				case <-ctx.Done():
					return
				}
			}
		}
	}()
	return out, errc
}
