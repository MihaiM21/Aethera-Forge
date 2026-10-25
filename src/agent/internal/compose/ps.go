package compose

import (
	"bytes"
	"encoding/json"
	"sort"
	"strings"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
)

// psEntry is one line of `docker compose ps --format json`.
type psEntry struct {
	ID         string `json:"ID"`
	Name       string `json:"Name"`
	Image      string `json:"Image"`
	Service    string `json:"Service"`
	State      string `json:"State"`
	Health     string `json:"Health"`
	ExitCode   int32  `json:"ExitCode"`
	Publishers []struct {
		TargetPort    uint32 `json:"TargetPort"`
		PublishedPort uint32 `json:"PublishedPort"`
		Protocol      string `json:"Protocol"`
		URL           string `json:"URL"`
	} `json:"Publishers"`
}

// ParsePs reads NDJSON or a JSON array as printed by different compose versions.
func ParsePs(b []byte) []*agentv1.ComposeServiceStatus {
	var entries []psEntry
	trim := bytes.TrimSpace(b)
	if bytes.HasPrefix(trim, []byte("[")) {
		_ = json.Unmarshal(trim, &entries)
	} else {
		dec := json.NewDecoder(bytes.NewReader(trim))
		for dec.More() {
			var e psEntry
			if dec.Decode(&e) != nil {
				break
			}
			entries = append(entries, e)
		}
	}
	out := make([]*agentv1.ComposeServiceStatus, 0, len(entries))
	for _, e := range entries {
		st := &agentv1.ComposeServiceStatus{
			Service: e.Service, ContainerId: e.ID, ContainerName: e.Name, Image: e.Image, ExitCode: e.ExitCode,
			State: mapState(e.State), Health: mapHealth(e.Health),
		}
		for _, p := range e.Publishers {
			if p.PublishedPort == 0 {
				continue
			}
			proto := agentv1.PortProtocol_PORT_PROTOCOL_TCP
			if strings.EqualFold(p.Protocol, "udp") {
				proto = agentv1.PortProtocol_PORT_PROTOCOL_UDP
			}
			st.Ports = append(st.Ports, &agentv1.PortMapping{ContainerPort: p.TargetPort, HostPort: p.PublishedPort, Protocol: proto})
		}
		out = append(out, st)
	}
	sort.SliceStable(out, func(i, j int) bool { return out[i].Service < out[j].Service })
	return out
}

func mapState(s string) agentv1.ContainerState {
	switch strings.ToLower(s) {
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

func mapHealth(s string) agentv1.ContainerHealth {
	switch strings.ToLower(s) {
	case "healthy":
		return agentv1.ContainerHealth_CONTAINER_HEALTH_HEALTHY
	case "unhealthy":
		return agentv1.ContainerHealth_CONTAINER_HEALTH_UNHEALTHY
	case "starting":
		return agentv1.ContainerHealth_CONTAINER_HEALTH_STARTING
	case "":
		return agentv1.ContainerHealth_CONTAINER_HEALTH_NONE
	}
	return agentv1.ContainerHealth_CONTAINER_HEALTH_UNSPECIFIED
}
