; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
NINO026 | Nino | Error | A type takes one [NinoFormerName]
NINO027 | Nino | Warning | [NinoFormerName] on a generic type used polymorphically
NINO028 | Nino | Warning | [NinoMember] on a member that is never serialized
NINO029 | Nino | Error | Vector<T> cannot be serialized
NINO030 | Nino | Warning | [NinoUtf8] has no effect
NINO031 | Nino | Info | Use [NinoUtf8]
NINO032 | Nino | Error | WEAK_VERSION_TOLERANCE does not make types version tolerant
NINO033 | Nino | Warning | Attribute on an interface member has no effect
NINO034 | Nino | Warning | Layout change breaks data written before or after it
NINO035 | Nino | Warning | Layout change loses data without an error
NINO036 | Nino | Info | Layout change is compatible with a condition
NINO037 | Nino | Warning | Layout baseline cannot be read
NINO038 | Nino | Error | Serialization callback has the wrong signature
NINO039 | Nino | Error | Serialization callback is not accessible to generated code
NINO040 | Nino | Warning | Serialization callback is never called
NINO041 | Nino | Error | Two methods for one serialization callback
NINO042 | Nino | Error | Serialization callback must be an instance method
NINO043 | Nino | Error | No instance exists before the members are read
NINO044 | Nino | Error | Serialization callback on a struct copied as raw memory
