// Package generated holds the conformance corpus's types as nino-gen writes them (nino_types.go), and runs the
// corpus over them: the end-to-end test of the Go templates (src/Nino.Codegen/Templates/go). Do not edit
// nino_types.go: Nino.Codegen.Tests checks it is what nino-gen writes today, and regenerates it with
//
//	NINO_UPDATE_GENERATED=1 dotnet test tests/Nino.Codegen.Tests
//
// (or: nino-gen --assembly conformance/Nino.Conformance/bin/<config>/net10.0/Nino.Conformance.dll --lang go
// --out runtimes/go/internal/generated --package generated).
package generated
