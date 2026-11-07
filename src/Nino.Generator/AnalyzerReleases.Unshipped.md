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
NINO032 | Nino | Error | WEAK_VERSION_TOLERANCE was removed
NINO033 | Nino | Warning | Attribute on an interface member has no effect
