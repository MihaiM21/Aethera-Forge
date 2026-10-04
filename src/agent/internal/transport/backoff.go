package transport

import (
	"context"
	"math"
	"math/rand/v2"
	"time"
)

// Reconnect parameters from the table in ADR 0002.
const (
	BackoffBase    = 1 * time.Second
	BackoffMax     = 60 * time.Second
	BackoffLongMax = 5 * time.Minute
	// BackoffLongAfter: after this many consecutive failures the cap rises
	// from BackoffMax to BackoffLongMax.
	BackoffLongAfter = 10
	// HealthyAfter: a session must stay Welcomed this long to reset the backoff.
	HealthyAfter = 60 * time.Second
	// TLSRetry is the fixed retry period after certificate/TLS errors.
	TLSRetry = 5 * time.Minute
)

// Backoff implements exponential backoff with FULL jitter:
// sleep = random(0, min(cap, base * 2^n)).
type Backoff struct {
	// Rand returns a float in [0,1). nil = math/rand/v2.
	Rand     func() float64
	failures int
}

// Next returns the delay before the next attempt and counts one more failure.
func (b *Backoff) Next() time.Duration {
	ceil := b.Ceiling()
	b.failures++
	r := rand.Float64
	if b.Rand != nil {
		r = b.Rand
	}
	return time.Duration(r() * float64(ceil))
}

// Ceiling is the upper bound for the delay that Next would draw from.
func (b *Backoff) Ceiling() time.Duration {
	limit := BackoffMax
	if b.failures >= BackoffLongAfter {
		limit = BackoffLongMax
	}
	exp := math.Pow(2, math.Min(float64(b.failures), 30))
	c := time.Duration(float64(BackoffBase) * exp)
	if c > limit || c <= 0 {
		c = limit
	}
	return c
}

// Reset clears the failure count.
func (b *Backoff) Reset() { b.failures = 0 }

// Failures is the number of consecutive failed attempts.
func (b *Backoff) Failures() int { return b.failures }

// Clock abstracts time for the reconnect loop.
type Clock interface {
	Now() time.Time
	// Sleep waits d or until ctx ends (returning ctx.Err()).
	Sleep(ctx context.Context, d time.Duration) error
}

type realClock struct{}

func (realClock) Now() time.Time { return time.Now() }

func (realClock) Sleep(ctx context.Context, d time.Duration) error {
	if d <= 0 {
		return ctx.Err()
	}
	t := time.NewTimer(d)
	defer t.Stop()
	select {
	case <-t.C:
		return nil
	case <-ctx.Done():
		return ctx.Err()
	}
}

// RealClock is the wall clock.
var RealClock Clock = realClock{}
