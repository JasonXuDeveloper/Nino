; Shipped analyzer releases
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

; First shipped in 4.0.0-rc.1 (the release header takes a plain version only).
## Release 4.0.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
NINO001 | Nino | Error | NinoType must be accessible
NINO002 | Nino | Error | Type must be partial
NINO003 | Nino | Warning | Subtype of a NinoType without [NinoType]
NINO004 | Nino | Error | No usable constructor
NINO005 | Nino | Error | Constructor parameter matches no member
NINO006 | Nino | Warning | Member cannot be deserialized
NINO007 | Nino | Warning | Member type cannot be serialized
NINO008 | Nino | Error | Invalid custom formatter type
NINO009 | Nino | Error | Non-public member is not serialized
NINO010 | Nino | Warning | Redundant [NinoMember]
NINO011 | Nino | Warning | Redundant [NinoIgnore]
NINO012 | Nino | Error | Member has both [NinoMember] and [NinoIgnore]
NINO013 | Nino | Error | Duplicate member index
NINO014 | Nino | Error | Member key collision
NINO015 | Nino | Warning | trackReferences on a struct
NINO016 | Nino | Error | Cannot track references
NINO017 | Nino | Error | Invalid [NinoFactory]
NINO018 | Nino | Error | Type cannot be pooled
NINO019 | Nino | Error | Pool hooks not implemented
NINO020 | Nino | Error | Invalid formatter registration
NINO021 | Nino | Error | NinoConstructor is not accessible
NINO022 | Nino | Warning | Hidden member cannot be serialized
NINO023 | Nino | Error | Struct serializes no member
NINO024 | Nino | Warning | [NinoFormerName] on a member has no effect in a positional type
NINO025 | Nino | Warning | NinoFactory ignored because of NinoConstructor
