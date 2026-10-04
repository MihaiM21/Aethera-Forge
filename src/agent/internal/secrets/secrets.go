// Package secrets finds secret values inside protocol messages and masks them
// in text. Secrets are recognised by message TYPE (aethera.agent.v1.SecretValue),
// never by field name, mirroring the protocol's redaction rule.
package secrets

import (
	"sort"
	"strings"

	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

const secretValueName protoreflect.FullName = "aethera.agent.v1.SecretValue"

// minLen is the shortest secret that is masked; shorter ones would shred
// ordinary output ("1", "a") and protect nothing.
const minLen = 4

// Mask replaces secret material in text.
const Mask = "***"

// Collect returns every SecretValue.value found anywhere inside m.
func Collect(m proto.Message) []string {
	if m == nil {
		return nil
	}
	var out []string
	walk(m.ProtoReflect(), &out)
	return out
}

func walk(m protoreflect.Message, out *[]string) {
	if !m.IsValid() {
		return
	}
	if m.Descriptor().FullName() == secretValueName {
		fd := m.Descriptor().Fields().ByName("value")
		if fd != nil {
			if v := m.Get(fd).String(); v != "" {
				*out = append(*out, v)
			}
		}
		return
	}
	m.Range(func(fd protoreflect.FieldDescriptor, v protoreflect.Value) bool {
		switch {
		case fd.IsMap():
			if fd.MapValue().Message() != nil {
				v.Map().Range(func(_ protoreflect.MapKey, mv protoreflect.Value) bool {
					walk(mv.Message(), out)
					return true
				})
			}
		case fd.IsList():
			if fd.Message() != nil {
				l := v.List()
				for i := 0; i < l.Len(); i++ {
					walk(l.Get(i).Message(), out)
				}
			}
		case fd.Message() != nil:
			walk(v.Message(), out)
		}
		return true
	})
}

// Masker replaces known secret values with "***". It is safe for concurrent
// use after construction.
type Masker struct {
	needles []string // longest first
	maxLen  int
}

// NewMasker builds a masker for values. Multi-line secrets (private keys) also
// contribute each of their lines, because logs are masked line by line.
func NewMasker(values ...string) *Masker {
	seen := map[string]bool{}
	var needles []string
	add := func(s string) {
		if len(s) >= minLen && !seen[s] {
			seen[s] = true
			needles = append(needles, s)
		}
	}
	for _, v := range values {
		add(v)
		if strings.ContainsAny(v, "\r\n") {
			for _, line := range strings.FieldsFunc(v, func(r rune) bool { return r == '\n' || r == '\r' }) {
				add(strings.TrimSpace(line))
			}
		}
	}
	sort.Slice(needles, func(i, j int) bool { return len(needles[i]) > len(needles[j]) })
	m := &Masker{needles: needles}
	for _, n := range needles {
		if len(n) > m.maxLen {
			m.maxLen = len(n)
		}
	}
	return m
}

// ForMessage builds a masker from every secret inside msg.
func ForMessage(msg proto.Message) *Masker { return NewMasker(Collect(msg)...) }

// Empty reports whether the masker has nothing to mask.
func (m *Masker) Empty() bool { return m == nil || len(m.needles) == 0 }

// MaxLen is the length of the longest needle (0 when empty).
func (m *Masker) MaxLen() int {
	if m == nil {
		return 0
	}
	return m.maxLen
}

// String masks s.
func (m *Masker) String(s string) string {
	if m.Empty() {
		return s
	}
	for _, n := range m.needles {
		if strings.Contains(s, n) {
			s = strings.ReplaceAll(s, n, Mask)
		}
	}
	return s
}

// Bytes masks b (returns b itself when nothing matched).
func (m *Masker) Bytes(b []byte) []byte {
	if m.Empty() {
		return b
	}
	s := string(b)
	r := m.String(s)
	if r == s {
		return b
	}
	return []byte(r)
}

// Merge returns a masker covering both.
func Merge(a, b *Masker) *Masker {
	var vals []string
	if a != nil {
		vals = append(vals, a.needles...)
	}
	if b != nil {
		vals = append(vals, b.needles...)
	}
	return NewMasker(vals...)
}
