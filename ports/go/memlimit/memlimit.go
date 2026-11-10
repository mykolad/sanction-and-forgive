// Package memlimit gives the Go runtime a soft memory limit from the container's: the garbage collector then works
// harder as the heap nears it, instead of letting the heap double past the replica's memory (GOGC's default).
package memlimit

import (
	"os"
	"runtime/debug"
	"strconv"
	"strings"
)

// Share is the part of the container's memory the heap may use; the rest is the runtime's, the stacks' and the
// binary's, with room for a list being rebuilt next to the one it replaces.
const Share = 0.8

// Apply sets the soft limit to Share of the container's memory limit (cgroup v2, then v1), unless GOMEMLIMIT is set
// (the runtime has read it) or there's no limit. It returns the limit in bytes and where it came from.
func Apply() (int64, string) {
	if value, ok := os.LookupEnv("GOMEMLIMIT"); ok && value != "" {
		return debug.SetMemoryLimit(-1), "GOMEMLIMIT"
	}
	for _, path := range []string{"/sys/fs/cgroup/memory.max", "/sys/fs/cgroup/memory/memory.limit_in_bytes"} {
		content, err := os.ReadFile(path)
		if err != nil {
			continue
		}
		limit, err := strconv.ParseInt(strings.TrimSpace(string(content)), 10, 64)
		// "max" (v2) and a huge number (v1) mean no limit.
		if err != nil || limit <= 0 || limit >= 1<<50 {
			return debug.SetMemoryLimit(-1), "none"
		}
		soft := int64(float64(limit) * Share)
		debug.SetMemoryLimit(soft)
		return soft, path
	}
	return debug.SetMemoryLimit(-1), "none"
}
