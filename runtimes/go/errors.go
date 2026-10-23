package nino

import "strconv"

// Code says what kind of failure an [Error] is.
type Code uint8

const (
	// CodeEndOfData: the data ends before the value does.
	CodeEndOfData Code = iota + 1
	// CodeInvalidLength: a length or count that is negative (other than -1 for null) or claims more than the data
	// can hold.
	CodeInvalidLength
	// CodeInvalidData: bytes that are not a valid value of the type read (bad UTF-8, an invalid DateTimeOffset, a
	// bad offset of deferred bodies...).
	CodeInvalidData
	// CodeUnknownType: a type id that is not one of the types allowed where it was read, or a value whose type
	// cannot be written where it was written.
	CodeUnknownType
	// CodeDuplicateKey: a dictionary or set holds the same key twice.
	CodeDuplicateKey
	// CodeTooDeep: objects nested deeper than the limit (512 open bodies) where they cannot be deferred.
	CodeTooDeep
	// CodeTooLarge: a value too large for the format (a string or collection over 2^31-1 elements).
	CodeTooLarge
	// CodeCycle: an object graph with a cycle, which only reference tracking could write.
	CodeCycle
	// CodeInvalidValue: a value that has no Nino encoding (an invalid DateTimeOffset or Version...).
	CodeInvalidValue
)

var codeNames = [...]string{
	CodeEndOfData:     "unexpected end of data",
	CodeInvalidLength: "invalid length",
	CodeInvalidData:   "invalid data",
	CodeUnknownType:   "type not allowed here",
	CodeDuplicateKey:  "duplicate key",
	CodeTooDeep:       "nested too deep",
	CodeTooLarge:      "value too large",
	CodeCycle:         "cycle in object graph",
	CodeInvalidValue:  "invalid value",
}

// String returns a short description of the code.
func (c Code) String() string {
	if int(c) < len(codeNames) && codeNames[c] != "" {
		return codeNames[c]
	}
	return "error " + strconv.Itoa(int(c))
}

// Error is the only error type the package returns. A failed read or write returns no partial result.
//
// Compare with errors.Is against the sentinels (ErrEndOfData, ...): an *Error matches the sentinel of its Code.
type Error struct {
	Code Code
	// Offset is the position in the data (reading) or in the output (writing) where the failure was found.
	Offset int
	// Detail adds the value or the type involved, if any.
	Detail string
}

// Error implements the error interface.
func (e *Error) Error() string {
	s := "nino: " + e.Code.String()
	if e.Detail != "" {
		s += ": " + e.Detail
	}
	return s + " (at byte " + strconv.Itoa(e.Offset) + ")"
}

// Is reports whether target is an *Error with the same Code (the sentinels match every error of their code).
func (e *Error) Is(target error) bool {
	t, ok := target.(*Error)
	return ok && t.Code == e.Code
}

// Sentinels for errors.Is.
var (
	ErrEndOfData     = &Error{Code: CodeEndOfData}
	ErrInvalidLength = &Error{Code: CodeInvalidLength}
	ErrInvalidData   = &Error{Code: CodeInvalidData}
	ErrUnknownType   = &Error{Code: CodeUnknownType}
	ErrDuplicateKey  = &Error{Code: CodeDuplicateKey}
	ErrTooDeep       = &Error{Code: CodeTooDeep}
	ErrTooLarge      = &Error{Code: CodeTooLarge}
	ErrCycle         = &Error{Code: CodeCycle}
	ErrInvalidValue  = &Error{Code: CodeInvalidValue}
)
