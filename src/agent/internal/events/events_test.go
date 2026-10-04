package events

import (
	"regexp"
	"testing"
	"time"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/docker"
)

func TestNewIDIsUUIDv7AndUnique(t *testing.T) {
	re := regexp.MustCompile(`^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$`)
	seen := map[string]bool{}
	for i := 0; i < 1000; i++ {
		id := NewID()
		if !re.MatchString(id) {
			t.Fatalf("not a UUIDv7: %s", id)
		}
		if seen[id] {
			t.Fatal("duplicate id")
		}
		seen[id] = true
	}
}

func TestConvertMapsDockerActions(t *testing.T) {
	c := NewConverter()
	now := time.Now()
	ev := func(action string, attrs map[string]string) docker.Event {
		return docker.Event{Action: action, ContainerID: "c1", Name: "web", Time: now, Attributes: attrs}
	}
	cases := []struct {
		action string
		attrs  map[string]string
		typ    agentv1.EventType
		check  func(*agentv1.EventNotice) bool
	}{
		{"start", nil, agentv1.EventType_EVENT_TYPE_CONTAINER_STARTED, nil},
		{"die", map[string]string{"exitCode": "137", "aethera.app": "x", "image": "nginx"}, agentv1.EventType_EVENT_TYPE_CONTAINER_DIED,
			func(n *agentv1.EventNotice) bool {
				_, hasImage := n.GetLabels()["image"]
				return n.GetExitCode() == 137 && n.GetLabels()["aethera.app"] == "x" && !hasImage
			}},
		{"oom", nil, agentv1.EventType_EVENT_TYPE_CONTAINER_OOM, func(n *agentv1.EventNotice) bool { return n.GetOomKilled() }},
		{"stop", nil, agentv1.EventType_EVENT_TYPE_CONTAINER_STOPPED, nil},
		{"restart", nil, agentv1.EventType_EVENT_TYPE_CONTAINER_RESTARTING, nil},
		{"destroy", nil, agentv1.EventType_EVENT_TYPE_CONTAINER_REMOVED, nil},
		{"health_status: unhealthy", nil, agentv1.EventType_EVENT_TYPE_CONTAINER_HEALTH_CHANGED,
			func(n *agentv1.EventNotice) bool {
				return n.GetHealth() == agentv1.ContainerHealth_CONTAINER_HEALTH_UNHEALTHY
			}},
	}
	for _, tc := range cases {
		n, ok := c.Convert(ev(tc.action, tc.attrs))
		if !ok || n.GetType() != tc.typ {
			t.Fatalf("%s: got %v ok=%v", tc.action, n, ok)
		}
		if n.GetEventId() == "" || n.GetContainerId() != "c1" || n.GetContainerName() != "web" || n.GetOccurredAt() == nil {
			t.Fatalf("%s: incomplete notice %v", tc.action, n)
		}
		if tc.check != nil && !tc.check(n) {
			t.Fatalf("%s: check failed for %v", tc.action, n)
		}
	}
	if _, ok := c.Convert(ev("exec_start: sh -c x", nil)); ok {
		t.Fatal("exec events must be ignored (they could carry commands)")
	}
}

func TestHealthTransitionsCarryPreviousHealth(t *testing.T) {
	c := NewConverter()
	mk := func(a string) *agentv1.EventNotice {
		n, ok := c.Convert(docker.Event{Action: a, ContainerID: "c1", Time: time.Now()})
		if !ok {
			t.Fatalf("not converted: %s", a)
		}
		return n
	}
	first := mk("health_status: starting")
	if first.GetPreviousHealth() != agentv1.ContainerHealth_CONTAINER_HEALTH_UNSPECIFIED {
		t.Fatalf("first previous = %v", first.GetPreviousHealth())
	}
	second := mk("health_status: healthy")
	if second.GetPreviousHealth() != agentv1.ContainerHealth_CONTAINER_HEALTH_STARTING || second.GetHealth() != agentv1.ContainerHealth_CONTAINER_HEALTH_HEALTHY {
		t.Fatalf("second = %v", second)
	}
	third := mk("health_status: unhealthy")
	if third.GetPreviousHealth() != agentv1.ContainerHealth_CONTAINER_HEALTH_HEALTHY {
		t.Fatalf("third previous = %v", third.GetPreviousHealth())
	}
}

func TestRingBoundsByCount(t *testing.T) {
	r := NewRing(1000, time.Hour, nil)
	for i := 0; i < 1500; i++ {
		r.Add(&agentv1.EventNotice{ExitCode: int32(i)})
	}
	got := r.Drain()
	if len(got) != 1000 {
		t.Fatalf("kept %d events, limit is 1000", len(got))
	}
	if got[0].GetExitCode() != 500 || got[999].GetExitCode() != 1499 {
		t.Fatalf("oldest must be evicted: first=%d last=%d", got[0].GetExitCode(), got[999].GetExitCode())
	}
	if r.Len() != 0 || len(r.Drain()) != 0 {
		t.Fatal("Drain must empty the ring")
	}
}

func TestRingExpiresByAge(t *testing.T) {
	now := time.Unix(1_700_000_000, 0)
	r := NewRing(0, 0, func() time.Time { return now })
	r.Add(&agentv1.EventNotice{ExitCode: 1})
	now = now.Add(10 * time.Minute)
	r.Add(&agentv1.EventNotice{ExitCode: 2})
	now = now.Add(6 * time.Minute) // first event is now 16 min old (> 15 min)
	got := r.Drain()
	if len(got) != 1 || got[0].GetExitCode() != 2 {
		t.Fatalf("got %v, want only the event younger than 15 minutes", got)
	}
}
