# Security policy

Nino reads untrusted bytes: a payload that crashes a reader, reads outside its input, allocates or takes time out of
proportion to its size, or recurses without a bound is a vulnerability, in .NET, Go, Rust, TypeScript or the code
nino-gen writes. `.oss-scanner/threat_model.md` describes what is in scope and how severity is rated.

## Reporting a vulnerability

Report privately through GitHub's private vulnerability reporting
(https://github.com/JasonXuDeveloper/Nino/security/advisories/new) or by email to jason@xgamedev.net. Please do not
open a public issue. Include the runtime and version, the payload as hex and the type it is read as.

## Supported versions

Fixes go into the latest release; read untrusted data with `NinoSettings.Untrusted` (.NET).
