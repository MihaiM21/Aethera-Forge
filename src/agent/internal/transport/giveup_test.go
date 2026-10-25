package transport

import (
	"context"
	"crypto/tls"
	"errors"
	"io"
	"log/slog"
	"sync"
	"testing"
	"time"
)

// manualClock moves only when Sleep is called, by exactly the requested delay.
type manualClock struct {
	mu  sync.Mutex
	now time.Time
}

func (c *manualClock) Now() time.Time {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.now
}

func (c *manualClock) Sleep(ctx context.Context, d time.Duration) error {
	c.mu.Lock()
	c.now = c.now.Add(d)
	c.mu.Unlock()
	return ctx.Err()
}

func newGiveUpClient(giveUp time.Duration, clock Clock) *Client {
	return New(Options{
		Endpoint: "127.0.0.1:1",
		// No usable credentials: every attempt fails at once, like a control plane that cannot be reached.
		TLS:         func() (*tls.Config, error) { return nil, errors.New("no credentials") },
		Clock:       clock,
		Rand:        func() float64 { return 0.5 },
		Logger:      slog.New(slog.NewTextHandler(io.Discard, nil)),
		GiveUpAfter: giveUp,
		TLSRetry:    time.Minute,
	})
}

func TestRunGivesUpAfterTheOfflineLimit(t *testing.T) {
	clock := &manualClock{now: time.Unix(1_700_000_000, 0)}
	c := newGiveUpClient(10*time.Minute, clock)

	done := make(chan error, 1)
	go func() { done <- c.Run(context.Background()) }()
	select {
	case err := <-done:
		if !errors.Is(err, ErrGaveUp) {
			t.Fatalf("Run = %v, want ErrGaveUp", err)
		}
	case <-time.After(5 * time.Second):
		t.Fatal("Run never gave up")
	}
	if elapsed := clock.Now().Sub(time.Unix(1_700_000_000, 0)); elapsed < 10*time.Minute {
		t.Fatalf("gave up after only %v", elapsed)
	}
}

func TestRunRetriesForeverWithoutALimit(t *testing.T) {
	clock := &manualClock{now: time.Unix(1_700_000_000, 0)}
	c := newGiveUpClient(0, clock)

	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan error, 1)
	go func() { done <- c.Run(ctx) }()
	for clock.Now().Sub(time.Unix(1_700_000_000, 0)) < 24*time.Hour {
		select {
		case err := <-done:
			t.Fatalf("Run returned %v although there is no limit", err)
		case <-time.After(time.Millisecond):
		}
	}
	cancel()
	select {
	case err := <-done:
		if !errors.Is(err, context.Canceled) {
			t.Fatalf("Run = %v, want context.Canceled", err)
		}
	case <-time.After(5 * time.Second):
		t.Fatal("Run did not stop after cancel")
	}
}
