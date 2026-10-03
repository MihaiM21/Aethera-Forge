// Package metrics gathers host facts, host and container metrics and the
// resource discovery report (spec sections 55 and 44) using gopsutil and the
// Docker API.
package metrics

import (
	"context"
	"os"
	"runtime"
	"strings"
	"time"

	"github.com/shirou/gopsutil/v4/cpu"
	"github.com/shirou/gopsutil/v4/disk"
	"github.com/shirou/gopsutil/v4/host"
	"github.com/shirou/gopsutil/v4/load"
	"github.com/shirou/gopsutil/v4/mem"
	psnet "github.com/shirou/gopsutil/v4/net"
	"google.golang.org/protobuf/types/known/durationpb"
	"google.golang.org/protobuf/types/known/timestamppb"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
)

// HostFacts returns the static facts about this machine.
func HostFacts(ctx context.Context) *agentv1.HostFacts {
	f := &agentv1.HostFacts{Architecture: runtime.GOARCH}
	if h, err := host.InfoWithContext(ctx); err == nil {
		f.Hostname = h.Hostname
		f.OsName = h.Platform
		f.OsVersion = h.PlatformVersion
		f.KernelVersion = h.KernelVersion
		if h.BootTime > 0 {
			f.BootTime = timestamppb.New(time.Unix(int64(h.BootTime), 0))
		}
		if h.VirtualizationRole == "guest" || h.VirtualizationSystem != "" {
			f.Virtualization = h.VirtualizationSystem
		}
	}
	if f.Hostname == "" {
		f.Hostname, _ = os.Hostname()
	}
	if infos, err := cpu.InfoWithContext(ctx); err == nil && len(infos) > 0 {
		f.CpuModel = infos[0].ModelName
	}
	if n, err := cpu.CountsWithContext(ctx, false); err == nil && n > 0 {
		f.CpuCoresPhysical = uint32(n)
	}
	if n, err := cpu.CountsWithContext(ctx, true); err == nil && n > 0 {
		f.CpuCoresLogical = uint32(n)
	} else {
		f.CpuCoresLogical = uint32(runtime.NumCPU())
	}
	if v, err := mem.VirtualMemoryWithContext(ctx); err == nil {
		f.MemoryTotalBytes = int64(v.Total)
	}
	if s, err := mem.SwapMemoryWithContext(ctx); err == nil {
		f.SwapTotalBytes = int64(s.Total)
	}
	for _, i := range Interfaces(ctx) {
		if !i.GetUp() {
			continue
		}
		for _, a := range i.GetAddresses() {
			if !isLoopbackAddr(a) {
				f.IpAddresses = append(f.IpAddresses, a)
			}
		}
	}
	f.Timezone = time.Now().Location().String()
	if tz := os.Getenv("TZ"); tz != "" {
		f.Timezone = tz
	} else if name, _ := time.Now().Zone(); f.Timezone == "Local" {
		f.Timezone = name
	}
	return f
}

func isLoopbackAddr(cidr string) bool {
	return strings.HasPrefix(cidr, "127.") || strings.HasPrefix(cidr, "::1") || strings.HasPrefix(cidr, "fe80:")
}

// BootID identifies the current host boot.
func BootID() string {
	if b, err := os.ReadFile("/proc/sys/kernel/random/boot_id"); err == nil {
		return strings.TrimSpace(string(b))
	}
	if id, err := host.BootTime(); err == nil {
		return time.Unix(int64(id), 0).UTC().Format(time.RFC3339)
	}
	return ""
}

// Interfaces lists network interfaces.
func Interfaces(ctx context.Context) []*agentv1.NetworkInterfaceInfo {
	list, err := psnet.InterfacesWithContext(ctx)
	if err != nil {
		return nil
	}
	out := make([]*agentv1.NetworkInterfaceInfo, 0, len(list))
	for _, i := range list {
		ni := &agentv1.NetworkInterfaceInfo{Name: i.Name, MacAddress: i.HardwareAddr, Mtu: uint32(max(i.MTU, 0))}
		for _, fl := range i.Flags {
			if fl == "up" {
				ni.Up = true
			}
		}
		for _, a := range i.Addrs {
			ni.Addresses = append(ni.Addresses, a.Addr)
		}
		out = append(out, ni)
	}
	return out
}

var pseudoFS = map[string]bool{
	"tmpfs": true, "devtmpfs": true, "overlay": true, "squashfs": true, "proc": true, "sysfs": true,
	"cgroup": true, "cgroup2": true, "devpts": true, "mqueue": true, "nsfs": true, "ramfs": true, "autofs": true,
}

// Disks reports usage of real filesystems.
func Disks(ctx context.Context) []*agentv1.DiskUsage {
	parts, err := disk.PartitionsWithContext(ctx, false)
	if err != nil {
		return nil
	}
	var out []*agentv1.DiskUsage
	seen := map[string]bool{}
	for _, p := range parts {
		if pseudoFS[p.Fstype] || seen[p.Device] || strings.HasPrefix(p.Mountpoint, "/var/lib/docker/") ||
			strings.HasPrefix(p.Mountpoint, "/snap/") || strings.HasPrefix(p.Mountpoint, "/run/") {
			continue
		}
		u, err := disk.UsageWithContext(ctx, p.Mountpoint)
		if err != nil || u.Total == 0 {
			continue
		}
		seen[p.Device] = true
		out = append(out, &agentv1.DiskUsage{
			MountPoint: p.Mountpoint, Device: p.Device, FsType: p.Fstype,
			TotalBytes: int64(u.Total), UsedBytes: int64(u.Used),
			InodesTotal: int64(u.InodesTotal), InodesUsed: int64(u.InodesUsed),
		})
	}
	return out
}

// HostMetrics samples CPU, memory, load, disks and network counters. CPU
// utilisation is measured since the previous call (the first call blocks
// briefly for a baseline).
func HostMetrics(ctx context.Context) *agentv1.HostMetrics {
	m := &agentv1.HostMetrics{}
	if pct, err := cpu.PercentWithContext(ctx, 0, false); err == nil && len(pct) > 0 {
		m.CpuPercent = pct[0]
	}
	if n, err := cpu.CountsWithContext(ctx, true); err == nil {
		m.CpuCores = uint32(n)
	}
	if l, err := load.AvgWithContext(ctx); err == nil {
		m.Load1, m.Load5, m.Load15 = l.Load1, l.Load5, l.Load15
	}
	if v, err := mem.VirtualMemoryWithContext(ctx); err == nil {
		m.MemoryTotalBytes, m.MemoryUsedBytes, m.MemoryAvailableBytes = int64(v.Total), int64(v.Used), int64(v.Available)
	}
	if s, err := mem.SwapMemoryWithContext(ctx); err == nil {
		m.SwapTotalBytes, m.SwapUsedBytes = int64(s.Total), int64(s.Used)
	}
	m.Disks = Disks(ctx)
	if io, err := psnet.IOCountersWithContext(ctx, true); err == nil {
		for _, c := range io {
			if c.Name == "lo" {
				continue
			}
			m.Interfaces = append(m.Interfaces, &agentv1.NetworkInterfaceStats{
				Name: c.Name, RxBytes: c.BytesRecv, TxBytes: c.BytesSent,
				RxPackets: c.PacketsRecv, TxPackets: c.PacketsSent, RxErrors: c.Errin, TxErrors: c.Errout,
			})
		}
	}
	if up, err := host.UptimeWithContext(ctx); err == nil {
		m.Uptime = durationpb.New(time.Duration(up) * time.Second)
	}
	return m
}
