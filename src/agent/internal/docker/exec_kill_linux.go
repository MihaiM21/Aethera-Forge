//go:build linux

package docker

import (
	"os"
	"strconv"
	"strings"
	"syscall"
)

// killProcessTree kills pid (a host pid as reported by the Docker daemon) and
// its descendants. It only does so when /proc/<pid>/cgroup names the
// container: an agent in its own pid namespace sees different processes under
// the same numbers and must not kill an unrelated one.
func killProcessTree(pid int, containerID string) {
	if containerID == "" || !inContainerCgroup(pid, containerID) {
		return
	}
	tree := descendants(pid)
	for _, p := range append(tree, pid) {
		_ = syscall.Kill(p, syscall.SIGKILL)
	}
}

func inContainerCgroup(pid int, containerID string) bool {
	data, err := os.ReadFile("/proc/" + strconv.Itoa(pid) + "/cgroup")
	return err == nil && strings.Contains(string(data), containerID)
}

// descendants lists the children, grandchildren, ... of pid.
func descendants(pid int) []int {
	entries, err := os.ReadDir("/proc")
	if err != nil {
		return nil
	}
	children := map[int][]int{}
	for _, e := range entries {
		p, err := strconv.Atoi(e.Name())
		if err != nil {
			continue
		}
		data, err := os.ReadFile("/proc/" + e.Name() + "/stat")
		if err != nil {
			continue
		}
		// "pid (comm) S ppid ...": comm may contain spaces and parentheses.
		s := string(data)
		i := strings.LastIndexByte(s, ')')
		if i < 0 {
			continue
		}
		f := strings.Fields(s[i+1:])
		if len(f) < 2 {
			continue
		}
		if pp, err := strconv.Atoi(f[1]); err == nil {
			children[pp] = append(children[pp], p)
		}
	}
	var out []int
	queue := []int{pid}
	for len(queue) > 0 {
		cur := queue[0]
		queue = queue[1:]
		for _, c := range children[cur] {
			out = append(out, c)
			queue = append(queue, c)
		}
	}
	return out
}
