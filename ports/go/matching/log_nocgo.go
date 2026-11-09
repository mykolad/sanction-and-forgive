//go:build !cgo

package matching

import "math"

// ln without cgo (a native build with no C compiler): Go's own logarithm, which can differ from the C library's in
// the last bit. The image builds with cgo; see log_cgo.go.
func ln(x float64) float64 { return math.Log(x) }
