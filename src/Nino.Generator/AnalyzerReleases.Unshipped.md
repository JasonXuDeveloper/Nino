; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
NINO001 | Nino | Error | No usable or matching constructor
NINO002 | Nino | Error | NinoType must be accessible
NINO003 | Nino | Warning | Sub-type of a NinoType without the attribute
NINO004 | Nino | Warning | Redundant NinoMemberAttribute
NINO005 | Nino | Warning | Redundant NinoIgnoreAttribute
NINO006 | Nino | Error | Ambiguous member
NINO007 | Nino | Error | Private member without containNonPublicMembers
NINO009 | Nino | Error | Duplicate member index
NINO010 | Nino | Error | Invalid custom formatter type
NINO011 | Nino | Warning | Member type cannot be serialized
NINO012 | Nino | Error | Invalid NinoRefDeserializationAttribute usage
NINO013 | Nino | Error | Type must be partial
NINO014 | Nino | Warning | Member cannot be deserialized
NINO015 | Nino | Error | Member key collision
NINO016 | Nino | Warning | Cannot track references
