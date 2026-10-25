module github.com/JasonXuDeveloper/Nino/runtimes/go/bench

go 1.24.0

require (
	github.com/JasonXuDeveloper/Nino/runtimes/go/v4 v4.0.0
	github.com/vmihailenco/msgpack/v5 v5.4.1
	golang.org/x/sys v0.41.0
	google.golang.org/protobuf v1.36.12
)

require github.com/vmihailenco/tagparser/v2 v2.0.0 // indirect

replace github.com/JasonXuDeveloper/Nino/runtimes/go/v4 => ../
