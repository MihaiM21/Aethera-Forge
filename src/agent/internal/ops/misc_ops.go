package ops

import (
	"context"
	"crypto/tls"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/netip"
	"net/url"
	"strconv"
	"strings"
	"time"

	"google.golang.org/protobuf/types/known/durationpb"
	"google.golang.org/protobuf/types/known/timestamppb"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/dispatch"
	"github.com/mihaim21/aethera-forge/agent/internal/docker"
	"github.com/mihaim21/aethera-forge/agent/internal/selfupdate"
)

// defaultTailCap bounds LogStreamStart.tail == 0 ("all, bounded by the agent").
const defaultTailCap = 5000

// ---------------------------------------------------------------------------
// Log streaming
// ---------------------------------------------------------------------------

func (d *Deps) logStreamStart(ctx context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	req := env.Cmd.GetLogStreamStart()
	if req.GetStreamId() == "" || req.GetContainer() == "" {
		return nil, invalidArg("stream_id and container are required")
	}
	if req.GetTail() < 0 {
		return nil, invalidArg("tail must not be negative")
	}
	stdout, stderr := req.GetIncludeStdout(), req.GetIncludeStderr()
	if !stdout && !stderr {
		stdout, stderr = true, true
	}
	result := func(started bool) *agentv1.CommandResult {
		return &agentv1.CommandResult{Result: &agentv1.CommandResult_LogStreamStart{LogStreamStart: &agentv1.LogStreamStartResult{
			StreamId: req.GetStreamId(), Started: started,
		}}}
	}
	if _, err := d.Docker.ContainerInspect(ctx, req.GetContainer(), false); err != nil {
		if isNotFound(err) {
			// The stream is finished before it began: tell the control plane via EOF.
			st := d.Logs.Open(req.GetStreamId(), agentv1.LogSource_LOG_SOURCE_CONTAINER, false, env.Masker)
			st.Close("container not found")
			return result(false), nil
		}
		return nil, err
	}

	opts := docker.LogOptions{Follow: req.GetFollow(), Tail: int(req.GetTail()), Stdout: stdout, Stderr: stderr}
	if req.GetSince() != nil {
		opts.Since = req.GetSince().AsTime()
	}
	if opts.Tail == 0 && opts.Since.IsZero() {
		opts.Tail = defaultTailCap
	}
	st := d.Logs.Open(req.GetStreamId(), agentv1.LogSource_LOG_SOURCE_CONTAINER, false, env.Masker)
	sctx, cancel := context.WithCancel(d.BaseContext)
	if old, loaded := d.streams.Swap(req.GetStreamId(), cancel); loaded {
		old.(context.CancelFunc)()
	}
	go func() {
		defer cancel()
		err := d.Docker.ContainerLogs(sctx, req.GetContainer(), opts,
			st.Writer(agentv1.LogStream_LOG_STREAM_STDOUT), st.Writer(agentv1.LogStream_LOG_STREAM_STDERR))
		reason := ""
		switch {
		case err == nil:
		case errors.Is(err, context.Canceled):
			reason = "stream stopped"
		case isNotFound(err):
			reason = "container removed"
		default:
			reason = "log read failed"
		}
		st.Close(reason)
	}()
	return result(true), nil
}

func (d *Deps) logStreamStop(_ context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	id := env.Cmd.GetLogStreamStop().GetStreamId()
	if id == "" {
		return nil, invalidArg("stream_id is required")
	}
	// Abort first so the final EOF is not delayed by a graceful close that
	// waits for credit; then stop the Docker reader.
	d.Logs.Stop(id, "stopped by control plane")
	if c, ok := d.streams.LoadAndDelete(id); ok {
		c.(context.CancelFunc)()
	}
	return empty(), nil
}

// ---------------------------------------------------------------------------
// Discovery
// ---------------------------------------------------------------------------

func (d *Deps) discoveryRefresh(ctx context.Context, _ *dispatch.Env) (*agentv1.CommandResult, error) {
	if d.Discover == nil {
		return nil, dispatch.Errorf(agentv1.ErrorCode_ERROR_CODE_UNSUPPORTED, "discovery is not available")
	}
	rep, err := d.Discover(ctx)
	if err != nil {
		return nil, err
	}
	return &agentv1.CommandResult{Result: &agentv1.CommandResult_Discovery{Discovery: rep}}, nil
}

// ---------------------------------------------------------------------------
// Self update
// ---------------------------------------------------------------------------

func (d *Deps) agentSelfUpdate(ctx context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	req := env.Cmd.GetAgentSelfUpdate()
	env.Progress("download", "downloading update", -1)
	res, err := d.Updater.Apply(ctx, selfupdate.Request{
		URL: req.GetUrl(), SHA256: req.GetSha256(), Version: req.GetTargetVersion(), Signature: req.GetSignature(),
	})
	if err != nil {
		switch {
		case errors.Is(err, selfupdate.ErrChecksum):
			return nil, dispatch.Errorf(agentv1.ErrorCode_ERROR_CODE_CHECKSUM_MISMATCH, "%v", err)
		case errors.Is(err, selfupdate.ErrInsecureURL), errors.Is(err, selfupdate.ErrSignature):
			return nil, dispatch.Errorf(agentv1.ErrorCode_ERROR_CODE_POLICY_VIOLATION, "%v", err)
		case errors.Is(err, selfupdate.ErrBusy):
			return nil, dispatch.Errorf(agentv1.ErrorCode_ERROR_CODE_CONFLICT, "%v", err)
		}
		return nil, err
	}
	return &agentv1.CommandResult{Result: &agentv1.CommandResult_AgentSelfUpdate{AgentSelfUpdate: &agentv1.AgentSelfUpdateResult{
		PreviousVersion: res.PreviousVersion, NewVersion: res.NewVersion, Restarting: true,
	}}}, nil
}

// ---------------------------------------------------------------------------
// Health probes
// ---------------------------------------------------------------------------

const (
	defaultProbeTimeout  = 5 * time.Second
	defaultProbeInterval = 2 * time.Second
	maxProbeBody         = 64 * 1024
)

type probeOutcome struct {
	ok      bool
	status  int32
	latency time.Duration
	detail  string
}

func (d *Deps) healthProbe(ctx context.Context, env *dispatch.Env) (*agentv1.CommandResult, error) {
	req := env.Cmd.GetHealthProbe()
	timeout := orDur(req.GetTimeout(), defaultProbeTimeout)
	interval := orDur(req.GetInterval(), defaultProbeInterval)
	attempts := int(req.GetRetries())
	if attempts < 1 {
		attempts = 1
	}
	threshold := int(req.GetSuccessThreshold())
	if threshold < 1 {
		threshold = 1
	}
	if sp := req.GetStartPeriod().AsDuration(); sp > 0 {
		if !sleep(ctx, sp) {
			return nil, ctx.Err()
		}
	}
	var last probeOutcome
	streak, done := 0, 0
	for done < attempts {
		done++
		last = d.probeOnce(ctx, req, timeout)
		if last.ok {
			streak++
		} else {
			streak = 0
		}
		if streak >= threshold {
			return probeResult(true, done, last), nil
		}
		if done < attempts && !sleep(ctx, interval) {
			return nil, ctx.Err()
		}
	}
	res := probeResult(false, done, last)
	return res, dispatch.Errorf(agentv1.ErrorCode_ERROR_CODE_HEALTH_CHECK_FAILED, "health probe failed after %d attempts: %s", done, last.detail)
}

func probeResult(healthy bool, attempts int, o probeOutcome) *agentv1.CommandResult {
	return &agentv1.CommandResult{Result: &agentv1.CommandResult_HealthProbe{HealthProbe: &agentv1.HealthProbeResult{
		Healthy: healthy, Attempts: int32(attempts), HttpStatus: o.status, Latency: durationpb.New(o.latency),
		Detail: o.detail, CheckedAt: timestamppb.Now(),
	}}}
}

func orDur(d *durationpb.Duration, def time.Duration) time.Duration {
	if d == nil || d.AsDuration() <= 0 {
		return def
	}
	return d.AsDuration()
}

func sleep(ctx context.Context, d time.Duration) bool {
	t := time.NewTimer(d)
	defer t.Stop()
	select {
	case <-t.C:
		return true
	case <-ctx.Done():
		return false
	}
}

func (d *Deps) probeOnce(ctx context.Context, req *agentv1.HealthProbe, timeout time.Duration) probeOutcome {
	pctx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()
	start := time.Now()
	var out probeOutcome
	switch t := req.GetTarget().(type) {
	case *agentv1.HealthProbe_Http:
		out = d.httpProbe(pctx, t.Http, timeout)
	case *agentv1.HealthProbe_Tcp:
		out = d.tcpProbe(pctx, t.Tcp)
	case *agentv1.HealthProbe_Container:
		out = d.containerProbe(pctx, t.Container.GetContainer())
	default:
		out = probeOutcome{detail: "probe target is required"}
	}
	out.latency = time.Since(start)
	return out
}

// dialGuarded resolves host through the SSRF policy and dials the vetted IPs
// directly, so a DNS answer cannot change between check and connect.
func (d *Deps) dialGuarded(ctx context.Context, network, address string) (net.Conn, error) {
	host, port, err := net.SplitHostPort(address)
	if err != nil {
		return nil, err
	}
	addrs, err := d.Policy.ResolveProbeHost(ctx, host)
	if err != nil {
		return nil, err
	}
	var dialer net.Dialer
	var lastErr error
	for _, a := range addrs {
		c, derr := dialer.DialContext(ctx, network, netip.AddrPortFrom(a, mustPort(port)).String())
		if derr == nil {
			return c, nil
		}
		lastErr = derr
	}
	return nil, lastErr
}

func mustPort(p string) uint16 {
	n, _ := strconv.ParseUint(p, 10, 16)
	return uint16(n)
}

func (d *Deps) tcpProbe(ctx context.Context, t *agentv1.TcpProbe) probeOutcome {
	c, err := d.dialGuarded(ctx, "tcp", net.JoinHostPort(t.GetHost(), strconv.Itoa(int(t.GetPort()))))
	if err != nil {
		return probeOutcome{detail: "tcp connect failed: " + errSummary(err)}
	}
	_ = c.Close()
	return probeOutcome{ok: true, detail: "tcp connect ok"}
}

func (d *Deps) containerProbe(ctx context.Context, ref string) probeOutcome {
	info, err := d.Docker.ContainerInspect(ctx, ref, false)
	if err != nil {
		return probeOutcome{detail: "inspect failed: " + errSummary(err)}
	}
	if info.GetState() != agentv1.ContainerState_CONTAINER_STATE_RUNNING {
		return probeOutcome{detail: "container is " + info.GetState().String()}
	}
	switch info.GetHealth() {
	case agentv1.ContainerHealth_CONTAINER_HEALTH_HEALTHY:
		return probeOutcome{ok: true, detail: "container healthy"}
	case agentv1.ContainerHealth_CONTAINER_HEALTH_NONE, agentv1.ContainerHealth_CONTAINER_HEALTH_UNSPECIFIED:
		return probeOutcome{ok: true, detail: "container running (no healthcheck defined)"}
	}
	return probeOutcome{detail: "container health is " + info.GetHealth().String()}
}

func (d *Deps) httpProbe(ctx context.Context, p *agentv1.HttpProbe, timeout time.Duration) probeOutcome {
	u, err := url.Parse(p.GetUrl())
	if err != nil {
		return probeOutcome{detail: "invalid url"}
	}
	method := p.GetMethod()
	if method == "" {
		method = http.MethodGet
	}
	req, err := http.NewRequestWithContext(ctx, method, u.String(), nil)
	if err != nil {
		return probeOutcome{detail: "invalid request"}
	}
	for k, v := range p.GetHeaders() {
		if strings.EqualFold(k, "Host") {
			req.Host = v
			continue
		}
		req.Header.Set(k, v)
	}
	tr := &http.Transport{
		DialContext:           d.dialGuarded,
		TLSClientConfig:       &tls.Config{MinVersion: tls.VersionTLS12, InsecureSkipVerify: p.GetInsecureSkipVerify()}, //nolint:gosec // explicit per-probe option
		TLSHandshakeTimeout:   timeout,
		ResponseHeaderTimeout: timeout,
		DisableKeepAlives:     true,
		Proxy:                 nil, // never honour proxy environment variables
	}
	client := &http.Client{Transport: tr, Timeout: timeout}
	if p.GetFollowRedirects() {
		client.CheckRedirect = func(_ *http.Request, via []*http.Request) error {
			if len(via) >= 5 {
				return errors.New("too many redirects")
			}
			return nil // each hop dials through dialGuarded
		}
	} else {
		client.CheckRedirect = func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }
	}
	resp, err := client.Do(req)
	if err != nil {
		return probeOutcome{detail: "request failed: " + errSummary(err)}
	}
	defer resp.Body.Close()
	out := probeOutcome{status: int32(resp.StatusCode)}
	if !statusOK(int32(resp.StatusCode), p.GetExpectedStatus()) {
		out.detail = fmt.Sprintf("unexpected status %d", resp.StatusCode)
		return out
	}
	if want := p.GetBodyContains(); want != "" {
		body, _ := io.ReadAll(io.LimitReader(resp.Body, maxProbeBody))
		if !strings.Contains(string(body), want) {
			out.detail = "response body does not contain the expected text"
			return out
		}
	}
	out.ok, out.detail = true, fmt.Sprintf("status %d", resp.StatusCode)
	return out
}

func statusOK(code int32, expected []int32) bool {
	if len(expected) == 0 {
		return code >= 200 && code < 400
	}
	for _, e := range expected {
		if e == code {
			return true
		}
	}
	return false
}

// errSummary keeps probe errors short and free of URLs/credentials.
func errSummary(err error) string {
	var ue *url.Error
	if errors.As(err, &ue) {
		err = ue.Err
	}
	s := err.Error()
	if len(s) > 200 {
		s = s[:200]
	}
	return s
}
