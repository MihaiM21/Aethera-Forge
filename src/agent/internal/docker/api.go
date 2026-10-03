// Package docker is the agent's view of the Docker daemon. All daemon access
// goes through the API interface so command handlers can be tested with fakes;
// the production implementation (sdk.go) wraps the official Docker SDK.
package docker

import (
	"context"
	"errors"
	"io"
	"time"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
)

// Sentinel error kinds. Implementations wrap them (fmt.Errorf("%w: ...")).
var (
	ErrNotFound     = errors.New("not found")
	ErrConflict     = errors.New("conflict")
	ErrAlreadyExist = errors.New("already exists")
	ErrUnavailable  = errors.New("docker daemon unavailable")
	ErrAuth         = errors.New("registry authentication failed")
	ErrPermission   = errors.New("permission denied")
	ErrPortInUse    = errors.New("port already allocated")
	ErrPullFailed   = errors.New("image pull failed")
	ErrInvalid      = errors.New("invalid argument")
)

// ErrorCode maps an error to the protocol's stable error code.
func ErrorCode(err error) agentv1.ErrorCode {
	switch {
	case err == nil:
		return agentv1.ErrorCode_ERROR_CODE_UNSPECIFIED
	case errors.Is(err, ErrNotFound):
		return agentv1.ErrorCode_ERROR_CODE_NOT_FOUND
	case errors.Is(err, ErrAlreadyExist):
		return agentv1.ErrorCode_ERROR_CODE_ALREADY_EXISTS
	case errors.Is(err, ErrConflict):
		return agentv1.ErrorCode_ERROR_CODE_CONFLICT
	case errors.Is(err, ErrUnavailable):
		return agentv1.ErrorCode_ERROR_CODE_DOCKER_UNAVAILABLE
	case errors.Is(err, ErrAuth):
		return agentv1.ErrorCode_ERROR_CODE_REGISTRY_AUTH_FAILED
	case errors.Is(err, ErrPermission):
		return agentv1.ErrorCode_ERROR_CODE_PERMISSION_DENIED
	case errors.Is(err, ErrPortInUse):
		return agentv1.ErrorCode_ERROR_CODE_PORT_CONFLICT
	case errors.Is(err, ErrPullFailed):
		return agentv1.ErrorCode_ERROR_CODE_IMAGE_PULL_FAILED
	case errors.Is(err, ErrInvalid):
		return agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT
	case errors.Is(err, context.DeadlineExceeded):
		return agentv1.ErrorCode_ERROR_CODE_TIMEOUT
	case errors.Is(err, context.Canceled):
		return agentv1.ErrorCode_ERROR_CODE_CANCELLED
	}
	return agentv1.ErrorCode_ERROR_CODE_INTERNAL
}

// LogOptions selects container log lines.
type LogOptions struct {
	Follow bool
	Since  time.Time // zero = no lower bound
	Tail   int       // 0 = all
	Stdout bool
	Stderr bool
}

// Event is a raw daemon event for a container, converted to EventNotice by the
// events package.
type Event struct {
	Action      string // "start", "die", "oom", "health_status: unhealthy", ...
	ContainerID string
	Name        string
	Attributes  map[string]string // includes container labels and exitCode
	Time        time.Time
}

// Stats is one resource sample of a running container.
type Stats struct {
	CPUPercent    float64 // relative to one core
	MemUsedBytes  int64
	MemLimitBytes int64
	NetRxBytes    uint64
	NetTxBytes    uint64
	BlockRead     uint64
	BlockWrite    uint64
	PIDs          uint32
}

// API is everything the agent needs from Docker. Methods return errors that
// wrap the sentinel kinds above. Lookups address objects by ID or name.
type API interface {
	// Status classifies the daemon without ever failing (spec section 44).
	Status(ctx context.Context) (agentv1.DockerStatus, string)
	Info(ctx context.Context) (*agentv1.DockerInfo, error)

	ContainerList(ctx context.Context, all bool, labelFilters []string, nameFilter string) ([]*agentv1.ContainerInfo, error)
	// ContainerInspect returns the observed state; with raw it also fills
	// InspectJson with env values already stripped.
	ContainerInspect(ctx context.Context, ref string, raw bool) (*agentv1.ContainerInfo, error)
	ContainerCreate(ctx context.Context, spec *agentv1.ContainerSpec) (id string, warnings []string, err error)
	ContainerStart(ctx context.Context, ref string) error
	ContainerStop(ctx context.Context, ref string, timeout *time.Duration) error
	ContainerRestart(ctx context.Context, ref string, timeout *time.Duration) error
	ContainerRemove(ctx context.Context, ref string, force, removeAnonymousVolumes bool) error
	ContainerStats(ctx context.Context, id string) (*Stats, error)
	// ContainerLogs copies demultiplexed log output until EOF or ctx ends.
	ContainerLogs(ctx context.Context, ref string, opts LogOptions, stdout, stderr io.Writer) error

	// ImagePull pulls and blocks until finished; progress receives short
	// human-readable status lines.
	ImagePull(ctx context.Context, ref string, auth *agentv1.RegistryAuth, platform string, progress func(string)) error
	ImageList(ctx context.Context, includeIntermediate bool, labelFilters []string, reference string) ([]*agentv1.ImageInfo, error)
	ImageInspect(ctx context.Context, ref string) (*agentv1.ImageInfo, error)
	// ImageRemove returns the removed/untagged references.
	ImageRemove(ctx context.Context, ref string, force bool) ([]string, error)

	VolumeList(ctx context.Context, labelFilters []string) ([]*agentv1.VolumeInfo, error)
	VolumeInspect(ctx context.Context, name string) (*agentv1.VolumeInfo, error)
	VolumeCreate(ctx context.Context, req *agentv1.VolumeCreate) (*agentv1.VolumeInfo, error)
	VolumeRemove(ctx context.Context, name string, force bool) error

	NetworkList(ctx context.Context, labelFilters []string) ([]*agentv1.NetworkInfo, error)
	NetworkInspect(ctx context.Context, ref string) (*agentv1.NetworkInfo, error)
	NetworkCreate(ctx context.Context, req *agentv1.NetworkCreate) (*agentv1.NetworkInfo, error)
	NetworkRemove(ctx context.Context, ref string) error
	NetworkConnect(ctx context.Context, req *agentv1.NetworkConnect) error
	NetworkDisconnect(ctx context.Context, network, container string, force bool) error

	// BuildCachePrune removes build cache and returns the reclaimed bytes.
	BuildCachePrune(ctx context.Context) (int64, error)

	// Events streams container events until ctx ends. The error channel
	// delivers at most one terminal error.
	Events(ctx context.Context) (<-chan Event, <-chan error)

	Close() error
}
