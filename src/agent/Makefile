VERSION ?= 0.0.0-dev
PKG     := github.com/mihaim21/aethera-forge/agent
LDFLAGS := -s -w -X $(PKG)/internal/version.Version=$(VERSION)
DIST    := dist

.PHONY: all vet test build cross clean

all: vet test build

vet:
	go vet ./...

test:
	go test ./...

build:
	CGO_ENABLED=0 go build -trimpath -ldflags "$(LDFLAGS)" -o $(DIST)/aethera-agent ./cmd/aethera-agent

# Static linux binaries for amd64 and arm64.
cross:
	CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build -trimpath -ldflags "$(LDFLAGS)" -o $(DIST)/aethera-agent-linux-amd64 ./cmd/aethera-agent
	CGO_ENABLED=0 GOOS=linux GOARCH=arm64 go build -trimpath -ldflags "$(LDFLAGS)" -o $(DIST)/aethera-agent-linux-arm64 ./cmd/aethera-agent

clean:
	rm -rf $(DIST)
