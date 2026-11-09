//go:build cgo

package matching

// #cgo LDFLAGS: -lm
// #include <math.h>
import "C"

// ln is the C library's natural logarithm: what .NET's Math.Log calls (glibc's log on Linux, where production runs).
// Go's math.Log differs from it in the last bit for some arguments, and a word's weight on a band's edge then turns
// a 99% into a 98%: the token weights must be the reference's, bit for bit.
func ln(x float64) float64 { return float64(C.log(C.double(x))) }
