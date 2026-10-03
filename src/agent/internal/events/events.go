// Package events converts Docker daemon events into protocol EventNotices and
// keeps the bounded ring buffer used while the control plane is unreachable
// (ADR 0002: 1000 events / 15 minutes, flushed after Hello).
package events

import (
	"crypto/rand"
	"encoding/binary"
	"fmt"
	"strconv"
	"strings"
	"sync"
	"time"

	"google.golang.org/protobuf/types/known/timestamppb"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/docker"
)

// Ring limits from ADR 0002.
const (
	DefaultMax    = 1000
	DefaultMaxAge = 15 * time.Minute
)

// NewID returns a UUIDv7 string (unique per agent process, time ordered).
func NewID() string {
	var b [16]byte
	_, _ = rand.Read(b[:])
	ms := uint64(time.Now().UnixMilli())
	var ts [8]byte
	binary.BigEndian.PutUint64(ts[:], ms<<16)
	copy(b[:6], ts[:6])
	b[6] = (b[6] & 0x0f) | 0x70
	b[8] = (b[8] & 0x3f) | 0x80
	return fmt.Sprintf("%x-%x-%x-%x-%x", b[0:4], b[4:6], b[6:8], b[8:10], b[10:16])
}

// Converter maps Docker events to notices, remembering each container's last
// health so HEALTH_CHANGED carries previous_health.
type Converter struct {
	mu     sync.Mutex
	health map[string]agentv1.ContainerHealth
}

// NewConverter creates a converter.
func NewConverter() *Converter {
	return &Converter{health: map[string]agentv1.ContainerHealth{}}
}

var skipLabel = map[string]bool{"name": true, "image": true, "exitCode": true, "signal": true, "execDuration": true}

// Convert returns the notice for ev, or false for events the protocol ignores.
func (c *Converter) Convert(ev docker.Event) (*agentv1.EventNotice, bool) {
	n := &agentv1.EventNotice{
		EventId:       NewID(),
		OccurredAt:    timestamppb.New(ev.Time),
		ContainerId:   ev.ContainerID,
		ContainerName: ev.Name,
	}
	if len(ev.Attributes) > 0 {
		n.Labels = map[string]string{}
		for k, v := range ev.Attributes {
			if !skipLabel[k] {
				n.Labels[k] = v
			}
		}
	}
	action := ev.Action
	switch {
	case action == "start":
		n.Type = agentv1.EventType_EVENT_TYPE_CONTAINER_STARTED
		c.setHealth(ev.ContainerID, agentv1.ContainerHealth_CONTAINER_HEALTH_UNSPECIFIED)
	case action == "stop":
		n.Type = agentv1.EventType_EVENT_TYPE_CONTAINER_STOPPED
	case action == "die":
		n.Type = agentv1.EventType_EVENT_TYPE_CONTAINER_DIED
		if code, err := strconv.Atoi(ev.Attributes["exitCode"]); err == nil {
			n.ExitCode = int32(code)
		}
	case action == "oom":
		n.Type = agentv1.EventType_EVENT_TYPE_CONTAINER_OOM
		n.OomKilled = true
	case action == "restart":
		n.Type = agentv1.EventType_EVENT_TYPE_CONTAINER_RESTARTING
	case action == "destroy":
		n.Type = agentv1.EventType_EVENT_TYPE_CONTAINER_REMOVED
		c.setHealth(ev.ContainerID, agentv1.ContainerHealth_CONTAINER_HEALTH_UNSPECIFIED)
	case strings.HasPrefix(action, "health_status"):
		status := strings.TrimSpace(strings.TrimPrefix(strings.TrimPrefix(action, "health_status"), ":"))
		var h agentv1.ContainerHealth
		switch status {
		case "healthy":
			h = agentv1.ContainerHealth_CONTAINER_HEALTH_HEALTHY
		case "unhealthy":
			h = agentv1.ContainerHealth_CONTAINER_HEALTH_UNHEALTHY
		case "starting":
			h = agentv1.ContainerHealth_CONTAINER_HEALTH_STARTING
		default:
			return nil, false
		}
		n.Type = agentv1.EventType_EVENT_TYPE_CONTAINER_HEALTH_CHANGED
		n.Health = h
		n.PreviousHealth = c.setHealth(ev.ContainerID, h)
	default:
		return nil, false
	}
	return n, true
}

func (c *Converter) setHealth(id string, h agentv1.ContainerHealth) agentv1.ContainerHealth {
	c.mu.Lock()
	defer c.mu.Unlock()
	prev := c.health[id]
	if h == agentv1.ContainerHealth_CONTAINER_HEALTH_UNSPECIFIED {
		delete(c.health, id)
	} else {
		c.health[id] = h
	}
	return prev
}

type item struct {
	at time.Time
	ev *agentv1.EventNotice
}

// Ring is the disconnected-period event buffer.
type Ring struct {
	mu     sync.Mutex
	max    int
	maxAge time.Duration
	now    func() time.Time
	items  []item
}

// NewRing creates a ring. Zero max/maxAge use the ADR defaults; now may be nil.
func NewRing(max int, maxAge time.Duration, now func() time.Time) *Ring {
	if max <= 0 {
		max = DefaultMax
	}
	if maxAge <= 0 {
		maxAge = DefaultMaxAge
	}
	if now == nil {
		now = time.Now
	}
	return &Ring{max: max, maxAge: maxAge, now: now}
}

// Add stores an event, evicting the oldest beyond the size limit.
func (r *Ring) Add(ev *agentv1.EventNotice) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.items = append(r.items, item{at: r.now(), ev: ev})
	if over := len(r.items) - r.max; over > 0 {
		r.items = append([]item(nil), r.items[over:]...)
	}
}

// Drain returns the unexpired events in order and empties the buffer.
func (r *Ring) Drain() []*agentv1.EventNotice {
	r.mu.Lock()
	defer r.mu.Unlock()
	cutoff := r.now().Add(-r.maxAge)
	var out []*agentv1.EventNotice
	for _, it := range r.items {
		if !it.at.Before(cutoff) {
			out = append(out, it.ev)
		}
	}
	r.items = nil
	return out
}

// Len is the number of buffered events (including not yet expired-checked).
func (r *Ring) Len() int {
	r.mu.Lock()
	defer r.mu.Unlock()
	return len(r.items)
}
