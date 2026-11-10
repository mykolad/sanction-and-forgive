package lists

import (
	"encoding/xml"
	"errors"
	"fmt"
	"io"
	"strings"
	"time"
	"unicode"

	"golang.org/x/text/encoding/htmlindex"
)

// SubjectType says what a list says a subject is.
type SubjectType int

const (
	Individual SubjectType = iota
	Entity
	Unknown
)

func (t SubjectType) String() string { return [...]string{"Individual", "Entity", "Unknown"}[t] }

// NameScript is a name's script.
type NameScript int

const (
	Latin NameScript = iota
	Cyrillic
	Other
)

func (s NameScript) String() string { return [...]string{"Latin", "Cyrillic", "Other"}[s] }

type ParsedName struct {
	FullName string
	IsWeak   bool
	Script   NameScript
}

type ParsedAddress struct {
	Raw         string
	CountryCode *string
}

// ParsedSubject is a listed party in the shape every source is reduced to.
type ParsedSubject struct {
	ExternalID  string
	Type        SubjectType
	PrimaryName string
	Names       []ParsedName
	Addresses   []ParsedAddress
	Programmes  *string
	ListedOn    *time.Time
	Remarks     *string
	SourceURL   *string
}

// FormatError is the reference's ListFormatException: the file isn't the list it should be.
type FormatError struct{ Message string }

func (e *FormatError) Error() string { return e.Message }

func formatError(format string, args ...any) error { return &FormatError{fmt.Sprintf(format, args...)} }

// DetectScript is Cyrillic if a name has any Cyrillic letter, Latin if all its letters are Latin, Other otherwise.
func DetectScript(value string) NameScript {
	latin, other := false, false
	for _, c := range value {
		if !(c <= 0xFFFF && unicode.IsLetter(c)) { // as .NET's char.IsLetter: no surrogate is a letter
			continue
		}
		if c >= 'Ѐ' && c <= 'ԯ' {
			return Cyrillic
		}
		if c <= 'ɏ' || (c >= 'Ḁ' && c <= 'ỿ') {
			latin = true
		} else {
			other = true
		}
	}
	if other || !latin {
		return Other
	}
	return Latin
}

// isWhiteSpace is .NET's char.IsWhiteSpace.
func isWhiteSpace(c rune) bool { return unicode.IsSpace(c) }

// Clean trims and collapses whitespace; nil when nothing is left.
func Clean(value *string) *string {
	if value == nil {
		return nil
	}
	trimmed := strings.TrimFunc(*value, isWhiteSpace)
	if trimmed == "" {
		return nil
	}
	var b strings.Builder
	space := false
	for _, c := range trimmed {
		if isWhiteSpace(c) {
			space = true
			continue
		}
		if space {
			b.WriteRune(' ')
			space = false
		}
		b.WriteRune(c)
	}
	s := b.String()
	return &s
}

func cleanString(value string) *string { return Clean(&value) }

// Join returns the non-empty parts, cleaned and joined; nil if there are none.
func Join(separator string, parts ...*string) *string {
	var cleaned []string
	for _, part := range parts {
		if c := Clean(part); c != nil {
			cleaned = append(cleaned, *c)
		}
	}
	if len(cleaned) == 0 {
		return nil
	}
	s := strings.Join(cleaned, separator)
	return &s
}

// ParseDate parses a date in one exact layout (Go's reference time), or nil.
func ParseDate(value *string, layout string) *time.Time {
	cleaned := Clean(value)
	if cleaned == nil {
		return nil
	}
	date, err := time.Parse(layout, *cleaned)
	if err != nil {
		return nil
	}
	return &date
}

// builder collects one subject's names and addresses, dropping empty and repeated ones. The first name added is the
// primary name unless one is added as primary.
type builder struct {
	externalID       string
	kind             SubjectType
	names            []ParsedName
	nameIndexes      map[string]int
	addresses        []ParsedAddress
	seenAddresses    map[string]bool
	primaryName      *string
	primaryIsFlagged bool
	Programmes       *string
	ListedOn         *time.Time
	Remarks          *string
	SourceURL        *string
}

func newBuilder(externalID string, kind SubjectType) *builder {
	return &builder{externalID: externalID, kind: kind, nameIndexes: map[string]int{}, seenAddresses: map[string]bool{}}
}

func (b *builder) HasNames() bool { return len(b.names) > 0 }

func (b *builder) AddName(fullName *string, isWeak, isPrimary bool) {
	cleaned := Clean(fullName)
	if cleaned == nil {
		return
	}
	if isPrimary && !b.primaryIsFlagged {
		b.primaryName, b.primaryIsFlagged = cleaned, true
	} else if b.primaryName == nil {
		b.primaryName = cleaned
	}
	if index, ok := b.nameIndexes[*cleaned]; ok {
		if b.names[index].IsWeak && !isWeak {
			b.names[index].IsWeak = false
		}
		return
	}
	b.nameIndexes[*cleaned] = len(b.names)
	b.names = append(b.names, ParsedName{*cleaned, isWeak, DetectScript(*cleaned)})
}

func (b *builder) AddAddress(raw *string, countryCode *string) {
	cleaned := Clean(raw)
	var code *string
	if c := Clean(countryCode); c != nil {
		upper := strings.Map(upperInvariant, *c)
		code = &upper
	}
	if cleaned == nil && code == nil {
		return
	}
	if cleaned == nil {
		cleaned = code
	}
	key := *cleaned + "|"
	if code != nil {
		key += *code
	}
	if b.seenAddresses[key] {
		return
	}
	b.seenAddresses[key] = true
	var kept *string
	if code != nil && len(*code) == 2 && isUpperASCII((*code)[0]) && isUpperASCII((*code)[1]) {
		kept = code
	}
	b.addresses = append(b.addresses, ParsedAddress{*cleaned, kept})
}

func isUpperASCII(c byte) bool { return c >= 'A' && c <= 'Z' }

// Build returns the subject, or nil if it has no usable name.
func (b *builder) Build() *ParsedSubject {
	if b.primaryName == nil {
		return nil
	}
	return &ParsedSubject{b.externalID, b.kind, *b.primaryName, b.names, b.addresses, b.Programmes, b.ListedOn, b.Remarks, b.SourceURL}
}

// --- XML: one record element at a time (Parsers/Xml.cs) ---

// Element is an XML element: its local name, attributes, children, and the text of all its descendants (.NET's
// XElement.Value), whitespace-only text nodes left out as the reference's reader ignores them.
type Element struct {
	Name     string
	attrs    map[string]string
	Children []*Element
	text     strings.Builder
}

// Value is the concatenated text of the element and its descendants.
func (e *Element) Value() string { return e.text.String() }

func (e *Element) El(name string) *Element {
	if e == nil {
		return nil
	}
	for _, child := range e.Children {
		if child.Name == name {
			return child
		}
	}
	return nil
}

func (e *Element) Els(name string) []*Element {
	if e == nil {
		return nil
	}
	var out []*Element
	for _, child := range e.Children {
		if child.Name == name {
			out = append(out, child)
		}
	}
	return out
}

// Val is the cleaned value of the first child element named name.
func (e *Element) Val(name string) *string {
	child := e.El(name)
	if child == nil {
		return nil
	}
	return cleanString(child.Value())
}

// Attr is the cleaned value of an attribute (by local name).
func (e *Element) Attr(name string) *string {
	if e == nil {
		return nil
	}
	value, ok := e.attrs[name]
	if !ok {
		return nil
	}
	return cleanString(value)
}

func eq(value *string, expected string) bool { return value != nil && *value == expected }

// records streams the elements at depth (the root is depth 0), refusing a document whose root isn't expectedRoot or
// that declares a DTD.
func records(reader io.Reader, expectedRoot string, depth int, each func(*Element) error) error {
	decoder := xml.NewDecoder(reader)
	decoder.CharsetReader = func(charset string, input io.Reader) (io.Reader, error) {
		encoding, err := htmlindex.Get(charset)
		if err != nil {
			return nil, err
		}
		return encoding.NewDecoder().Reader(input), nil
	}
	level := -1
	var stack []*Element
	rootSeen := false
	for {
		token, err := decoder.Token()
		if errors.Is(err, io.EOF) {
			if !rootSeen {
				return formatError("Expected root element <%s>, found <>.", expectedRoot)
			}
			return nil
		}
		if err != nil {
			return err
		}
		switch t := token.(type) {
		case xml.Directive:
			return formatError("A DTD isn't allowed in the list.")
		case xml.StartElement:
			level++
			if level == 0 {
				if rootSeen {
					return formatError("The list has more than one root element.")
				}
				rootSeen = true
				if t.Name.Local != expectedRoot {
					return formatError("Expected root element <%s>, found <%s>.", expectedRoot, t.Name.Local)
				}
				continue
			}
			if level >= depth {
				element := &Element{Name: t.Name.Local, attrs: map[string]string{}}
				for _, a := range t.Attr {
					if a.Name.Space == "xmlns" || a.Name.Local == "xmlns" {
						continue
					}
					element.attrs[a.Name.Local] = a.Value
				}
				if len(stack) > 0 {
					parent := stack[len(stack)-1]
					parent.Children = append(parent.Children, element)
				}
				stack = append(stack, element)
			}
		case xml.EndElement:
			if level >= depth && len(stack) > 0 {
				element := stack[len(stack)-1]
				stack = stack[:len(stack)-1]
				if len(stack) == 0 {
					if err := each(element); err != nil {
						return err
					}
				}
			}
			level--
		case xml.CharData:
			if len(stack) > 0 && strings.TrimFunc(string(t), isXMLSpace) != "" {
				for _, e := range stack {
					e.text.Write(t)
				}
			}
		}
	}
}

func isXMLSpace(c rune) bool { return c == ' ' || c == '\t' || c == '\n' || c == '\r' }

// upperInvariant is .NET's char.ToUpperInvariant: the Turkish dotless ı (U+0131) stays as it is.
func upperInvariant(c rune) rune {
	if c == 'ı' {
		return c
	}
	return unicode.ToUpper(c)
}

// lowerInvariant is .NET's char.ToLowerInvariant: the Turkish capital İ (U+0130) stays as it is.
func lowerInvariant(c rune) rune {
	if c == 'İ' {
		return c
	}
	return unicode.ToLower(c)
}
