; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
NINO026 | Nino | Error | A type takes one [NinoFormerName]
NINO027 | Nino | Warning | [NinoFormerName] on a generic type used polymorphically
NINO028 | Nino | Warning | [NinoMember] on a member that is never serialized
