; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
NINO001 | Nino | Error | NinoType must be accessible
NINO002 | Nino | Error | Type must be partial
NINO003 | Nino | Warning | Sub-type of a NinoType without the attribute
NINO004 | Nino | Error | No usable constructor
NINO005 | Nino | Error | Constructor parameter matches no member
NINO006 | Nino | Warning | Read-only member cannot be deserialized
NINO007 | Nino | Warning | Member type cannot be serialized
NINO008 | Nino | Error | Invalid custom formatter type
NINO009 | Nino | Error | Private member without containNonPublicMembers
NINO010 | Nino | Warning | Redundant NinoMemberAttribute
NINO011 | Nino | Warning | Redundant NinoIgnoreAttribute
NINO012 | Nino | Error | Ambiguous member
NINO013 | Nino | Error | Duplicate member index
NINO014 | Nino | Error | Member key collision
NINO015 | Nino | Warning | trackReferences on a struct
NINO016 | Nino | Error | Cannot track references
NINO017 | Nino | Error | Invalid NinoRefDeserializationAttribute usage
NINO018 | Nino | Error | Type cannot be pooled
NINO019 | Nino | Error | Pool hooks not implemented
NINO020 | Nino | Error | Invalid formatter registration
NINO021 | Nino | Error | NinoConstructor is not accessible
