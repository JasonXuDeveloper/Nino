module github.com/JasonXuDeveloper/Nino/runtimes/go/bench

go 1.24

require (
	github.com/JasonXuDeveloper/Nino/runtimes/go v0.0.0
	github.com/vmihailenco/msgpack/v5 v5.4.1
	google.golang.org/protobuf v1.36.12
)

require github.com/vmihailenco/tagparser/v2 v2.0.0 // indirect

replace github.com/JasonXuDeveloper/Nino/runtimes/go => ../
