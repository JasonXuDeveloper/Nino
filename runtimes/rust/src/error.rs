//! The one error type of the crate.

use core::fmt;

/// What went wrong. Every invalid input, and every value that cannot be written, is one of these.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
#[non_exhaustive]
pub enum ErrorKind {
    /// The data ended before the value did (or a member ran past the end of its region or entry).
    UnexpectedEnd,
    /// A length or count that is negative (other than -1 where null is allowed) or longer than the data left.
    InvalidLength,
    /// A presence byte other than 0 or 1.
    InvalidPresence,
    /// A `bool` byte other than 0 or 1.
    InvalidBool,
    /// A UTF-8 string whose bytes are not strict UTF-8, or that does not decode to its char count.
    InvalidUtf8,
    /// A type id that is not one of the types allowed where it was read.
    UnknownTypeId,
    /// A null (length -1, presence byte 0, type id 0) where the Rust type has no null.
    UnexpectedNull,
    /// A dictionary key or set element that is already in the collection.
    DuplicateKey,
    /// Bodies nested deeper than the format allows (512 for types that cannot be deferred).
    TooDeep,
    /// A region holding deferred bodies without the section they are in, or an invalid section.
    InvalidSection,
    /// An untracked object graph with a cycle, which would be written forever.
    Cycle,
    /// A value too large for the format (a count or region past `i32::MAX` bytes).
    TooLarge,
    /// A `DateTime`, `DateOnly`, `TimeOnly` or `DateTimeOffset` out of range.
    InvalidDateTime,
    /// A `Version` with a negative major (other than -1) or minor.
    InvalidVersion,
    /// A `BitArray` with bits set past its count.
    InvalidBits,
    /// A `char`/`Rune` value that is not a Unicode scalar value.
    InvalidChar,
    /// A `decimal` with invalid flags (only for conversions into other decimal types).
    InvalidDecimal,
    /// Bytes left over after the value, from [`crate::deserialize_exact`].
    TrailingData,
    /// Text that is not the form of the value parsed from it (`"1.5".parse::<Guid>()`).
    InvalidText,
    /// Data this runtime cannot represent (a body deferred inside a set element or a dictionary key).
    Unsupported,
}

/// A Nino error: its [`ErrorKind`] and, where there is one, the value that was rejected (a length, a byte, a
/// type id). Eight bytes, so `Result`s stay in registers.
#[derive(Clone, Copy, PartialEq, Eq, Hash)]
pub struct Error {
    kind: ErrorKind,
    value: i32,
}

/// `Result` with [`Error`].
pub type Result<T> = core::result::Result<T, Error>;

impl Error {
    /// An error of `kind` about `value`.
    #[cold]
    #[inline(never)]
    pub fn new(kind: ErrorKind, value: i32) -> Self {
        Self { kind, value }
    }

    /// An error of `kind`.
    #[cold]
    #[inline(never)]
    pub fn of(kind: ErrorKind) -> Self {
        Self { kind, value: 0 }
    }

    /// What went wrong.
    pub fn kind(&self) -> ErrorKind {
        self.kind
    }

    /// The value rejected (a length, a byte, a type id), or 0.
    pub fn value(&self) -> i32 {
        self.value
    }

    #[cold]
    #[inline(never)]
    pub(crate) fn end() -> Self {
        Self::of(ErrorKind::UnexpectedEnd)
    }

    #[cold]
    #[inline(never)]
    pub(crate) fn length(value: i32) -> Self {
        Self::new(ErrorKind::InvalidLength, value)
    }

    #[cold]
    #[inline(never)]
    pub(crate) fn null() -> Self {
        Self::of(ErrorKind::UnexpectedNull)
    }

    #[cold]
    #[inline(never)]
    pub(crate) fn too_large() -> Self {
        Self::of(ErrorKind::TooLarge)
    }
}

impl fmt::Display for Error {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let v = self.value;
        match self.kind {
            ErrorKind::UnexpectedEnd => write!(f, "Nino: unexpected end of data"),
            ErrorKind::InvalidLength => write!(f, "Nino: invalid length {v}"),
            ErrorKind::InvalidPresence => write!(f, "Nino: invalid presence byte {v}"),
            ErrorKind::InvalidBool => write!(f, "Nino: invalid bool byte {v}"),
            ErrorKind::InvalidUtf8 => write!(f, "Nino: invalid UTF-8 string"),
            ErrorKind::UnknownTypeId => {
                write!(f, "Nino: type id 0x{:08x} is not a type that can be read here", v as u32)
            }
            ErrorKind::UnexpectedNull => write!(f, "Nino: null where the type has no null"),
            ErrorKind::DuplicateKey => write!(f, "Nino: a key repeated in a dictionary or set"),
            ErrorKind::TooDeep => write!(f, "Nino: objects nested too deep"),
            ErrorKind::InvalidSection => write!(f, "Nino: missing or invalid deferred bodies"),
            ErrorKind::Cycle => write!(f, "Nino: a cycle in an object graph without reference tracking"),
            ErrorKind::TooLarge => write!(f, "Nino: value too large for the format"),
            ErrorKind::InvalidDateTime => write!(f, "Nino: date or time out of range"),
            ErrorKind::InvalidVersion => write!(f, "Nino: invalid Version"),
            ErrorKind::InvalidBits => write!(f, "Nino: BitArray with bits set past its count"),
            ErrorKind::InvalidChar => write!(f, "Nino: invalid char {v}"),
            ErrorKind::InvalidDecimal => write!(f, "Nino: invalid decimal"),
            ErrorKind::TrailingData => write!(f, "Nino: {v} bytes after the value"),
            ErrorKind::Unsupported => write!(f, "Nino: data this runtime cannot represent"),
            ErrorKind::InvalidText => write!(f, "Nino: text that is not a value of the type"),
        }
    }
}

impl fmt::Debug for Error {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "Error({:?}, {}): {}", self.kind, self.value, self)
    }
}

impl std::error::Error for Error {}
