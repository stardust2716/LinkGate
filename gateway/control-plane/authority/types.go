package authority

import (
	"bytes"
	"encoding/json/jsontext"
	"encoding/json/v2"
	"errors"
	"io"
	"regexp"
	"strconv"

	"github.com/google/uuid"
)

type Error struct{ Code string }

func (e *Error) Error() string { return "Control authority request rejected." }
func reject(code string) error { return &Error{Code: code} }
func Code(err error) string {
	var fixed *Error
	if errors.As(err, &fixed) {
		switch fixed.Code {
		case "AUTH_REQUIRED", "IDENTITY_CHANGED", "INVALID_REQUEST", "VERSION_UNSUPPORTED", "METHOD_UNSUPPORTED", "REVIEW_STALE", "OPERATION_CONFLICT", "OPERATION_UNKNOWN", "RESOURCE_EXHAUSTED", "STORAGE_UNAVAILABLE", "CLOCK_UNTRUSTED":
			return fixed.Code
		}
	}
	return "STORAGE_UNAVAILABLE"
}

var keyPattern = regexp.MustCompile(`^[0-9a-f]{64}$`)
var appPattern = regexp.MustCompile(`^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)+$`)
var servicePattern = regexp.MustCompile(`^[a-z][a-z0-9-]{0,31}$`)
var integerPattern = regexp.MustCompile(`^(0|[1-9][0-9]*)$`)

func validKey(value string) bool { return keyPattern.MatchString(value) }
func validApp(value string) bool { return len(value) <= 128 && appPattern.MatchString(value) }
func validUUID(value string) bool {
	id, err := uuid.Parse(value)
	return err == nil && id.String() == value
}
func decode(input []byte, value any) error {
	if len(input) == 0 || len(input) > 65536 {
		return reject("INVALID_REQUEST")
	}
	decoder := jsontext.NewDecoder(bytes.NewReader(input))
	for count := 0; ; count++ {
		token, err := decoder.ReadToken()
		if err == io.EOF {
			break
		}
		if err != nil || count >= 10000 || decoder.StackDepth() > 16 {
			return reject("INVALID_REQUEST")
		}
		if token.Kind() == '0' && !integerPattern.MatchString(token.String()) {
			return reject("INVALID_REQUEST")
		}
	}
	if err := json.Unmarshal(input, value, json.RejectUnknownMembers(true)); err != nil {
		return reject("INVALID_REQUEST")
	}
	return nil
}

type Revision uint64

func (r Revision) MarshalJSON() ([]byte, error) {
	return []byte(`"` + strconv.FormatUint(uint64(r), 10) + `"`), nil
}
func (r *Revision) UnmarshalJSON(input []byte) error {
	var text string
	if len(input) > 22 || json.Unmarshal(input, &text) != nil {
		return reject("INVALID_REQUEST")
	}
	value, err := strconv.ParseUint(text, 10, 64)
	if err != nil || strconv.FormatUint(value, 10) != text {
		return reject("INVALID_REQUEST")
	}
	*r = Revision(value)
	return nil
}
func encode(value any) ([]byte, error) {
	data, err := json.Marshal(value)
	if err != nil || len(data) > 65536 {
		return nil, reject("STORAGE_UNAVAILABLE")
	}
	return data, nil
}

type Roles struct {
	Owner   bool `json:"owner"`
	Publish bool `json:"publish"`
	Access  bool `json:"access"`
}
type Device struct {
	ID       string   `json:"device"`
	Revision Revision `json:"revision"`
	Active   bool     `json:"active"`
	Roles    Roles    `json:"roles"`
}
type ServiceScope struct {
	PublisherDevice string `json:"publisherDevice"`
	PublisherApp    string `json:"publisherApp"`
	Service         string `json:"service"`
}

func (s ServiceScope) Valid() bool {
	return validKey(s.PublisherDevice) && validApp(s.PublisherApp) && servicePattern.MatchString(s.Service)
}

type Service struct {
	Scope             ServiceScope `json:"scope"`
	Revision          Revision     `json:"revision"`
	PublisherRevision Revision     `json:"publisherRevision"`
	Active            bool         `json:"active"`
	Protocol          string       `json:"protocol"`
	Authority         string       `json:"authority"`
}
type AccessScope struct {
	VisitorDevice string       `json:"visitorDevice"`
	VisitorApp    string       `json:"visitorApp"`
	Target        ServiceScope `json:"target"`
}

func (s AccessScope) Valid() bool {
	return validKey(s.VisitorDevice) && validApp(s.VisitorApp) && s.Target.Valid()
}

type Grant struct {
	Scope             AccessScope `json:"scope"`
	Revision          Revision    `json:"revision"`
	VisitorRevision   Revision    `json:"visitorRevision"`
	PublisherRevision Revision    `json:"publisherRevision"`
	ServiceRevision   Revision    `json:"serviceRevision"`
	Active            bool        `json:"active"`
}
type Snapshot struct {
	Grant   Grant   `json:"grant"`
	Service Service `json:"service"`
}
