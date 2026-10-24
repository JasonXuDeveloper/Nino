# Nino 重构 / 安全 / 性能 跟踪

起点：origin/main 7dd8186（2026-10-23）。所有工作在独立 worktree 分支上，完成后由主会话审查、合并、全量验证，再快进推送到 main 和 claude/zen-goldberg-5qqqcu。

状态：⏳ 进行中 · ✅ 完成 · ⬜ 未开始 · ❓ 待决定

表中的 /workspace/wt-* 工作区和 scratchpad/ 路径属于当时的会话容器，容器回收后不再存在；分支内容都已合进 main。

## 第一阶段：并行分支

| 分支 / worktree | 范围 | 状态 |
|---|---|---|
| refactor/build · /workspace/wt-build | csproj/props/sln/global.json、集中包版本、生成器共享 props 与按目标路径打包、LangVersion、包校验、CI 警告即错误、单一版本来源、UnitTests.csproj 修正、AspNetCore.Tests 入 sln、Immutable 的 IsTrimmable/IsAotCompatible + AotTests | ✅ 9 个提交（556dee1…ac39d4c：元数据集中、中央包版本、生成器共享 props + GetTargetPath 打包、LangVersion latest + global.json、单一版本来源、测试工程 defines/NoWarn、包校验 + CI 警告即错误、Immutable 可裁剪/AOT、Nino.slnx）。nupkg 文件列表不变（只少了从未发布的 .symbols.nupkg）。合并时处理：CONTRIBUTING.md:51 仍提 Version.cs；global.json rollForward 改 latestMajor 以便 vars.DOTNET_VERSION 测新 SDK；AspNetCore 两个零分配测试本机失败但 main 的 CI 通过（容器环境问题） |
| refactor/gen · /workspace/wt-gen | 生成器 bug A–G（缓存漏 assembly NinoFormatter、别名/using static 接收者、SourceName、TypeFacts 缓存、LatestState 增长、泛型 FormerName 类型 id 冲突诊断、ShapeMatrix 维度）；规则统一、增量缓存、GeneratedFile、FormatterRef、TypeModel/BodyEmitter 拆分、CodeFix、原始字符串；输出变化：4.12 拦截器位置注释、每文件类型别名 | ✅ 17 个提交已合并（修复 A、B、E、H、I、K，D 编译性能，F NINO027，G ShapeMatrix，Q3 部分、Q6、Q8、Q9、Q10）；Q1–Q5、Q7、Q11 后来在 main 上补完（db8e667、4fbbc24、43cfc20、5d8d010、72d2d5f、2c30ac4），第三阶段 phase3/gen 核对并收尾（见下“生成器分支交回的后续项”） |
| refactor/core · /workspace/wt-core | Core bug B1–B6（卸载程序集重注册、Boxed null、raw 快路径大端、raw 规则两份、未知类型 id 负缓存、NinoSubtypeHandler 可见性）；NinoScratch、卸载钩子、NinoReflection、目录整理、NinoBlock 拆分 + 单一字节映射、具名布局结构、多维数组代码生成、#if 统一、杂项 | ✅ 10 个提交已合并（B1–B8 全部修复，Q9、Q10、Q11）；合并时改回 NinoTypeLayoutAttribute 构造参数名 typeId（公开 API）。Q1–Q8 后来在第二阶段做完：Q1 d7aa909 NinoScratch、Q2 38b4b92 卸载钩子、Q3 0609c72 NinoReflection、Q4 3f95560 目录整理、Q5 dc99b9c NinoBlock 拆分、Q6 eff1d0a 单一字节映射、Q7 dc933b2 具名布局结构、Q8 879976a 多维数组代码生成；草稿余下的 nits_block（Arm64 查表位、车道窗口）在第三阶段 ee82245 |
| refactor/sat · /workspace/wt-sat | Immutable（ref writer、内联、DuplicateKey、ArrayPool 清空）、AspNetCore（泛型缓存、OpenAPI 状态码）、TypeScript 常量契约测试、测试辅助去重（RoundTrip、编译设置）、UnityTypes | ✅ 6 个提交（4f6c6c0 TS 布局契约测试；4db3936 UnityTypes 用 NinoUnsafe；5aaaa89 AspNetCore Status 的 OpenAPI 说明；1cce82e 共用 RoundTrip；3b83db3 共用 TestCompilation；585c1f3 Immutable 按值传 writer）。合并时：Immutable.csproj 加 AllowUnsafeBlocks + SkipLocalsInit，链接 NinoThrow.cs 并删掉 DuplicateKey 副本；IncrementalGeneratorTests 和 CornerCompiler 改用 TestCompilation（需加 nullable/预处理符号选项）；AspNetCore 的类型缓存接入 Core 的卸载钩子；Gradient 读到 -1 抛 OverflowException 而不是 Nino 异常（归入安全：只抛 Nino 异常） |
| refactor/bench · /workspace/wt-bench | Benchmark 工程与 benchmarks/ 脚本清理；类名/方法名/参数不变，payload 字节不变，被测方法机器码不变 | ✅ 4 个提交（163bd66 删已结束的 probe，保留 CopyGapProbe/CopyProbe 等；74e0859；79831e5；2ed2c76 报表脚本共用 bdn.span）。遗留两项已在 phase3/misc 修好：GenericPolymorphicTest 登记 payload 大小（234f685）；单类报告（BenchmarkDotNet 不写 Type 列）由 merge_reports.py / compact_report.py 从文件名取类名，不再出现“### -”（b7e012b） |
| refactor/ts-perf · /workspace/wt-ts | TS 运行时与生成代码性能（Node + Chromium 实测）；安全（边界检查、先校验长度再分配、深度限制、类型 id 白名单、保持 class + Map 设计、`__proto__` 键测试）；深度延迟写入缺口（>128 层）验证并修复；node:test 单元测试套件；TS 目录整理 | ✅ 已合并 |

## 子代理分工（并行，每个独立 worktree 和分支，不推送）

| 方向 | 分支 · worktree | 内容 | 状态 |
|---|---|---|---|
| 代码优化 | refactor/build、refactor/gen、refactor/core、refactor/sat、refactor/bench | 见上表 | ✅ 全部合并 |
| 运行时：TS | refactor/ts-perf · wt-ts | 性能、安全、单元测试、移到 runtimes/typescript | ✅ 6 个提交（856f50a 数组写入改索引循环；7ee4828 深度延迟写入，Fixed；004d9a4 移到 runtimes/typescript；ebaaf06 非法 UTF-8 抛 NinoError，Fixed；7fe6a58 node:test 81 项；e9979be __proto__ 键测试）。合并后待办：生成器在布局里发布 deferrable（gen J）后，TS 生成器改用它，不再从 IL 读 Traits；CLAUDE.md 写上 npm test；DateTime Unspecified 在 TS 里读成 UTC（既有行为，待定）；bool 字节 2–255 读成 true（与 .NET 一致，归入第二阶段严格度统一） |
| 运行时：Go | runtime/go · wt-go | runtimes/go：读写、校验、深度延迟、零依赖、基准对比 json/msgpack、原生 fuzz、CI job | ✅ 9 个提交；一致性语料全过（引用追踪除外），fuzz 约 560 万次无 panic。合并前必须：bf8894d 误提交 5.6 MB 测试二进制，本地分支未推送，合并前改写该分支历史去掉它；runtimes.yml 与 conformance job 合并成一个文件。规范待修项 ✅ 均已写进规范（aa043a3 等；phase3/misc 对照语料与 Nino.Core 逐条核对）：§3.3/§3.4 示例日期标签、§7.6 keyed 剩余字节非法、§7.10.3 三条区段规则、§9.10 decimal/DateOnly/TimeOnly 范围、空结构体 1 字节（§5.2）、corpus README 的 sbyte[] |
| 运行时：Rust | runtime/rust · wt-rust | runtimes/rust：默认 forbid(unsafe)、不 panic、criterion 对比 serde_json/bincode/rmp/postcard、miri、CI job | ✅ 15 个提交（含 nino-derive 过程宏、serde feature、trybuild 编译失败测试、MSRV 1.88）；语料 252 例 + 10,344 个截断前缀全过（引用追踪除外），derive 47 个类型过语料，变异 fuzz 75.6 万次无 panic。合并前必须：提交 10–15 缺 Co-Authored-By，本地改写补上；runtimes.yml 合成一个文件（conformance + go + rust + ts）。性能（phase3/rperf 4394eb1）：本机复测时字符串读取已与 bincode/postcard 持平（短字符串的耗时主要是每个 String 的分配），慢的是写入：改成按 UTF-8 字节一遍完成 ASCII 检查和 Latin-1/UTF-16 转码，1000 个 ASCII 字符串 5.9→4.0 µs（bincode 3.4），Latin-1 −50%，UTF-16 −43%（本机进程内交替测量，非发布数字）；单条记录序列化在 criterion 里与 bincode 持平（本机噪声 ±20%），指令数 −5%。规范待定项 ✅ 已写进规范：keyed 链示例与语料 deep-keyed-chain-129 一致（§7.10.3）；能自嵌套的结构体和集合计入 512 层（§9.9，§7.10.1 加了指引，99955f4）；SortedDictionary<string> 等排序集合读取保持线上顺序（§6.3） |
| 线格式规范 | docs/wire-spec · wt-spec | docs/spec/wire-format.md | ✅ b870f7c（规范 4.0 草案；§10 列出 12 处不一致或未定义） |
| 一致性语料 | test/conformance · wt-conf | conformance/：.NET 写出的 valid + malicious 语料、.NET 自测、runtimes.yml 的 conformance job | ✅ 3 个提交（66286fa、8507669、ee400b3）：109 个有效用例、149 个恶意用例 + 截断规则（37 个用例的每个前缀，共 10,441 个）。.NET 现有缺口 26 个（自嵌套栈溢出 2、bool/presence 字节 12、延迟区段偏移/标志 4、keyed 成员剩余字节 1、尾随字节 1、越界 raw 值 3…），修复后需重新生成语料。合并时：slnx 加入 Nino.Conformance；runtimes.yml 触发路径补全；TS job 接入语料；规范补“空 raw 结构体写 1 个零字节” |
| 安全：模糊测试 | test/fuzz · wt-fuzz | tests/Nino.Fuzz（保护页变异模糊器 + SharpFuzz）、fuzz.yml（SIMD 开/关矩阵 + nightly） | ✅ 4 个提交（20b3a15、6e641fe、62cbfe2、e1c4386）；各 SIMD 模式各 300 s + libFuzzer 每个目标，无新 bug。已知 tree/dtree/hash/hash-set 默认非严格，修复后设仓库变量 NINO_FUZZ_STRICT_KNOWN=1 并删 Probes.cs 的 Known。注意：哈希修复要过探针的 1 s / 50 倍限制（只做到和 BCL 一样不够）；Golden 链接路径随目录重组改；三个新工程加进 slnx。发现生成器 bug K（类型名只差大小写 → hintName 冲突 → 整个项目没有生成代码）→ 已交 gen |
| 安全：修复 | core 分支（B7 自嵌套、B5 负缓存）；第二阶段（哈希链上限、Untrusted、NinoDataException、限制） | | ✅ B7、B5 已合并 / ✅ 第二阶段 |
| 目录结构 | chore/layout · wt-layout | 可重跑的重组脚本（tests/、benchmarks/、根目录 slnx 和 Directory.*.props），先在当前 main 上跑通验证，合并后再跑一次 | ✅ 175c21a tools/restructure.py（--check、--dry-run，幂等）+ 159362f 在 7dd8186 上的结果（合并后改为重跑脚本，不合并此提交）。验证：slnx 构建零警告、三种单测、AOT、Mono RuntimeTests、基准构建、6 个包文件列表不变、Unity 包不变。注意：report.yml 手动跑旧标签会失败；dependabot directories 通配需确认；nino.docs CLAUDE.md 的路径要改 |
| AspNetCore 零分配测试 | fix/aspnetcore-alloc · wt-asp | 测试工程从未进 sln，CI 的这一步其实什么都没跑；两个零分配测试真实失败，查根因并修 | ✅ 根因：Debug 构建里 async 状态机是 class，每次调用都分配；Release 发布包本来就零分配（无用户可见 bug）。46232f0 已在管道里的请求体同步读完、不走 async 方法（本机 Release 约 225→165 ns，非完整模式数字，无尾注）；b28ac83 测试改为同步调用测量，仍是严格零分配。合并时：nino.docs aspnetcore 页（中英）第 113 行附近改说法 |
| 文档 | docs/scan · wt-docs + nino.docs | 文档对照当前 main 全面核对（含 pitfalls 的 NinoTuple），列出进行中工作将来需要的页面 | ✅ Nino 1b65faa（未合并）；nino.docs 41cf216 已推送（NINO017 补“非泛型”）。AnalyzerReleases.Shipped.md 里 NINO003/006/009 标题已与 NinoDiagnostics.cs 一致（394d5cb，AnalyzerTest.ReleaseTrackingMatchesTheDescriptors 检查每条规则的级别和标题） |

## 调研 / 审计

| 项 | 状态 | 结论 / 下一步 |
|---|---|---|
| FFI / 导出到其他语言 | ✅ 调研完成 | 不做 NativeAOT C ABI（跨边界 ~10 ns，句柄 50+ ns，小结构体得不偿失）、不做 .NET wasm。推荐：正式线格式规范 → 布局中间表示 + 各语言生成器（先 TS 移植，再 Rust、Go）→ 共享一致性语料（含恶意输入）；schema 导出附带。每种语言：一份手写运行时（读写基础类型、字符串、BCL 类型、校验，一次写好，与类型无关）+ 每个类型的编解码由生成器产出。生成器用 Scriban（.sbn）模板：逻辑全在 C# 的布局中间表示里，模板只拼文本；模板作为嵌入资源，Scriban 只是工具的依赖，不进运行时。❓ 是否启动、何时启动由维护者决定 |
| 安全审计（反序列化不可信输入） | ✅ 审计完成（约 420 万个变异输入，保护页，无越界、无运行时集合损坏） | ① 严重：自嵌套集合类型（`Tree : List<Tree>`、`DTree : Dictionary<string, DTree>`）无深度计数 → 栈溢出杀进程 —— 已交给 refactor/core（B7）。② 中：哈希碰撞键（long/double/结构体）使 Dictionary/HashSet/ImmutableDictionary 读取变成平方级（1 MB ≈ 1 分钟 CPU）—— ✅ 已批准两层都做（第二阶段）：默认 `Fill` 链长计数超限抛错（正常数据零成本）+ 可选 `NinoSettings.Untrusted`（随机化比较器 + ③ 的语义校验）。③ 低：raw 结构体里 bool/enum/DateTime/decimal 不做语义校验（值错误，非内存问题）—— 放进 Untrusted 模式。④ 低：未知类型 id 每次重扫 —— 已在 core B5。⑤ 低：TS 无深度限制 → RangeError —— 已在 ts-perf。设计：公开 `NinoDataException : InvalidOperationException`；`NinoSettings.MaxDepth/MaxCollectionLength`（static readonly）；CI 模糊测试（SharpFuzz + PR 级变异模糊器，SIMD 开/关、复用模式）。PoC 与模糊器：scratchpad/sec/poc/ |

## 多语言（TS、Go、Rust 先行）

- ✅ docs/spec/wire-format.md：正式线格式规范（docs/wire-spec · b870f7c）。由此发现的 bug：H 生成器从引用程序集读 BCL 结构布局（_dummyPrimitive），Complex/TimeOnly/Half/Int128/Rune/int? 发布的布局错误 → gen；I 8 项及以上的 ValueTuple 成员被静默跳过（数据丢失）→ gen；J 布局里没有 deferrable 标记 → gen；B8 RawPoint?[] 生成代码与运行时解析器写出不同字节 → core（以定长 stride 为准，Wire-Format 尾注）；宽松读取（bool/presence >1、keyed 重复键、尾随字节、DateTime kind 3、ImmutableArray null 抛 BCL 异常等）→ 第二阶段统一严格度并写进规范。✅ 维护者已同意，第二阶段执行：默认就报错 —— 延迟区段偏移/标志不符、keyed 成员剩余字节、bool/presence 字节 >1；仅 Untrusted 模式报错 —— 尾随字节（Span 切片场景兼容）、越界的 decimal/DateTime/DateOnly 等 raw 值
- ✅ TS 运行时移到 runtimes/typescript
- ✅ 布局中间表示 + nino-gen 命令行 + Scriban 模板（src/Nino.Codegen，取代 Nino.TypeScript 里的语言相关部分；phase2/codegen）：TS/Go/Rust 三个目标，生成代码跑语料（有效/恶意/截断），docs/codegen.md。Rust 用展开代码而非 derive（布局只有曾用名的哈希）。未生成：泛型类型（只有泛型定义有布局）、trackReferences、[NinoType] 集合；TS 运行时的恶意用例已全部拒绝（phase2/ts-strict：延迟区段、keyed 成员剩余字节、重复键/元素；对象键按引用比较，查不出重复）
- ✅ conformance/：.NET 写出的有效语料 + 恶意语料，所有语言共用
- ✅ runtimes/go、runtimes/rust：运行时 + 模板 + 测试 + 基准
- ✅ 每个运行时的性能维护：各自基准对比本生态的主流库（TS：JSON；Go：encoding/json、vmihailenco/msgpack、gob；Rust：serde_json、bincode、rmp-serde、postcard），report.yml 的 runtimes / publish-runtimes 任务把各语言结果作为发布说明末尾的独立小节（🌐 TypeScript、🐹 Go、🦀 Rust runtime benchmark，benchmarks/runtimes_report.py；ef07848，phase3/rperf 3ae1575 把 Rust 表改成每个序列化器一列并带编码大小），不影响网站解析的三个 .NET 小节；CLAUDE.md 已写各语言的性能和安全规则。TS 基准尚未对比 msgpackr
- ✅ Go 结构体标签（runtime/go 6 个提交 fd2bd02…3693254；语料经标签结构体 103/140/35 通过；反射计划缓存，比手写编解码慢 2–5 倍，仍远快于 encoding/json）。合并时：Go 自造的 `deferrable=false` 与生成器 23abe40 发布的标记对齐成同一语法。待定：普通 string 字段把 null 读成 ""（要保留 null 用 *string）；map 按键排序写出（[]nino.Entry 保留线上顺序）。原计划：`nino:"0"` / `nino:"Name"`（keyed）/ `nino:"-"` 等，用户自己的结构体走反射 + 按类型缓存的编解码计划（同等的安全保证、fuzz）；生成的 Go 结构体带 nino 标签和可配置的 json/yaml 标签（默认 camelCase）
- ✅ Rust 对应（runtime/rust）：`#[derive(NinoRead, NinoWrite)]` 过程宏 + `#[nino(index/key/skip/utf8)]` 属性（编译期生成，零运行时成本），可选 serde 派生（serde feature，rename_all 可配置）
- ✅ 包名已定（2026-10-24）：npm 包名 nino-serialization；crate 名和 Go 模块路径 github.com/JasonXuDeveloper/Nino/runtimes/go（标签 runtimes/go/vX.Y.Z）不变

### 目标目录结构（第一阶段合并后执行，Unity 包路径不动）

```
Nino/
├─ Nino.slnx                （slnx 格式，从 src/ 移到根目录）
├─ src/                     .NET 包（路径不变）+ Nino.Codegen（中间表示、nino-gen、Templates/{typescript,go,rust}/*.sbn）
├─ tests/                   .NET 测试工程：UnitTests、UnitTests.Subset、RuntimeTests、AotTests、AspNetCore.Tests、Codegen.Tests
├─ runtimes/
│  ├─ typescript/           package.json、src/、test/、bench/
│  ├─ go/                   go.mod、*.go、*_test.go、bench/
│  └─ rust/                 Cargo.toml、src/、tests/、benches/
├─ conformance/             Nino.Conformance（.NET 语料生成）、corpus/{valid,malicious}/
├─ benchmarks/              .NET 基准（Nino.Benchmark、GeneratorBenchmark 移入）、脚本、v3
├─ docs/                    spec/、design、pitfalls、diagnostics、benchmarks
├─ tools/                   CodeGen
└─ .github/workflows/       ci.yml（.NET）、runtimes.yml（ts/go/rust + 一致性语料）、release、report
```

## 第二阶段（合并第一阶段之后）

- ✅ 读取严格度（已批准）：默认报错 —— 延迟区段偏移/标志不符、keyed 成员剩余字节、bool/presence >1；Untrusted 模式报错 —— 尾随字节、越界 decimal/DateTime/DateOnly 等；写进规范；重新生成一致性语料；asm.py 确认热路径。
- ✅ 安全（已批准）：哈希链长上限（默认）；`NinoSettings.Untrusted`（随机化比较器、bool/enum/DateTime/decimal 等语义校验）；公开 `NinoDataException : InvalidOperationException`；`NinoSettings.MaxDepth` / `MaxCollectionLength`（static readonly，只在已有长度校验和深度路径上多一次比较）；CI 模糊测试（SharpFuzz + PR 级变异模糊器，SIMD 开/关、复用模式）；回归用例（自嵌套集合、碰撞 long 键）。热路径用 asm.py 和基准确认不变慢。
- ✅ sln → slnx（refactor/build ac39d4c，先在 src/，目录重组时移到根目录）

- ✅ 合并 6 个分支，解决冲突，全量验证：net10 / net8+ns2.1 / net8+ns2.0+Roslyn4.8；AVX512=0、AVX2=0、HWIntrinsic=0；Mono net472；qemu Arm64/Arm32；AspNetCore、TypeScript 测试；asm.py compare；pack 内容对比。跨运行时（164fc5d）：RuntimeTests 151 例在 x64、HWIntrinsic=0、Mono（含 MONO_GC_DEBUG=verify-before-collections）、Arm64、Arm32 字节全部一致；Arm32 单测两例失败是测试假定 Int128 按 16 对齐（Arm32 按 8，Nino 已正确转换），已改测试；Arm64 约 37 例在 qemu 下崩溃（不含 Nino 代码的对照程序同样崩溃），靠 CI 的真 Arm64 job 覆盖
- ✅ 只给生成代码用的公共成员挪到 `Nino.Core.CompilerServices`（Core 与生成器同步；Core 里放错的类型拆到各自文件）
- ✅ 生成代码形态审查（用户要求；做了：拆分读取的 null 检查、.NET 9+ 泛型类型的 UnsafeAccessor；C# 14 extension 成员评估后不采用：只是语法糖，机器码不变，还要为 C# 9/Roslyn 4.8 保留旧写法）：ref struct / `allows ref struct`（.NET 9+）、UnsafeAccessor、static abstract、去掉只做转发的方法（如 `__WriteSubtype`）等，可用 `#if` 区分平台，共用写法做回退；每项看机器码 + 基准，两条路径字节一致
- ✅ UnsafeAccessor：.NET 8+ 非泛型、.NET 9+ 泛型（9ed78a3，GenericAccessorTests）/ ❓ `allows ref struct`（.NET 9+，#if）：第三阶段评估后不做。Core 里没有把 ref struct 当类型参数的泛型代码，也没有因为不能这样做而绕开的写法：Reader/Writer 以具体类型按 ref 传，格式化器是空结构体，NinoScratch 的写入器是普通结构体；加上约束不改变任何机器码。它唯一的用处是让用户的 ref struct（或 Span<T>）直接成为可序列化类型，那是新功能（INinoFormatter<T> 和生成器都要改，NINO 诊断现在拒绝 ref struct），需要维护者决定
- ✅ 热路径项：Permute64x2 合并（152afd5）、Shuffles 循环改 ref 游标（da33878）、Writer 上下文检查（9a26995；第三阶段 9e921ef 去掉 EndOuterRegionCore 里重复的检查）、ReadConverted（dd8c650）、AVX-512 组循环改 ref 游标 + 倒数（ab1175d）、NinoMask 复制去重（488e6d5，一个泛型 CopyVectors，x64 三种 ISA 和 Arm64 qemu 逐条对比机器码只少一条 mov）、内联标记补齐（32c6971 NinoReferenceReadTable.Begin；其余没有标记的小方法都是有意不内联的冷路径）。asm.py 里仍有 Reader.Skip、Writer.Skip、NinoUnsafe.Write/Copy、Spread4/Pack4 等已标 AggressiveInlining 的方法以调用出现（Player 的 __ReadBody、多态字典 WritePairs 等大方法内联预算用尽），origin/main 上同样如此，不属于补标记，留作后续
- ✅ 安全审计发现的修复
- ✅ 定长非原始 `[NinoType]` 结构体（含 ValueTuple/KVP/Nullable 字段）生成 INinoBlockFormatter，数组走 SIMD 置换（需要接口公开到新命名空间）；ShapeMatrix 补维度；x64/Arm64 基准确认
- ✅ 单元测试工程整体整理（按领域分目录、碎片并入同主题类、文件名=类名；三种配置测试数量和名称不变，无完全重复的测试；命名空间不变，因为类型 id 由完整类型名哈希而来）
- ✅ CLAUDE.md：优先共用方案；平台专用快路径需记录并测试各平台结果一致；UnsafeAccessor / allows ref struct 约定；安全要求
- ✅ docs/pitfalls.md：删掉过时的 `NinoTuple` 说法（v3 类型，v4 已删除）
- ✅ 快进推送；nino.docs changelog 中英文更新（用户可见的修复和变化）
- ✅ 多语言代码生成统一：Nino.Codegen（MSBuild 包，`<NinoCodegen Include="typescript|go|rust" Out="..."/>`）+ Nino.Codegen.Tool（`nino-gen`），取代 Nino.TypeScript；网站四页合为 advanced/codegen
- ✅ benchmarks/v3 并入 Nino.Benchmark（一个 csproj，`NINO_BENCH_V3=true` 切到 NuGet 3.3.13）

### Unity 上的 SIMD（像 Burst 一样，用户要求记下，后面做）
- 现状：Unity 包里的 Nino.Core.dll 是 netstandard2.1 版本，没有 System.Runtime.Intrinsics，所以 Unity（Mono、IL2CPP、HybridCLR）走的全是标量路径。块置换、字节序转换、ASCII 字符串这些 SIMD 快路径都用不到。
- 思路：把这些内核写成只碰原始字节的静态函数（`byte*` 进、`byte*` 出、个数、置换表），做成 Unity 专用程序集里的 `[BurstCompile]` 函数指针（`BurstCompiler.CompileFunctionPointer`），内部用 `Unity.Burst.Intrinsics`（X86.Ssse3/Avx2、Arm.Neon，`IsXxxSupported` 分支）。Nino.Core 只看到一个可选的「块内核」钩子（启动时注册；没有就走现有标量路径），所以 Core 不依赖 Burst。
- 要先验证：
  - IL2CPP：Burst 函数指针在 IL2CPP 下由 Burst AOT 预编译，应该可用。要实机验证 Windows/Android/iOS，以及 WebGL（不支持，回退标量）。
  - HybridCLR：解释执行的热更程序集调用 AOT 程序集里的 Nino.Core，Nino.Core 再调 Burst 函数指针，应该可用。热更代码自己不能被 Burst 编译，但内核在 AOT 包里，所以没关系。要验证补充元数据和泛型实例化不影响。
  - 包依赖：com.unity.burst 设为可选（versionDefines + asmdef 的 defineConstraints），没装 Burst 时程序集不编译，自动回退。
  - 收益：先在 Unity 里测标量 vs Burst（数组的 struct/元组、字符串），值得再做；还要比较调用函数指针的开销（小数组可能更慢，需要阈值）。
  - Unity 6 之后若迁到 CoreCLR，netstandard 之外可能直接有 Intrinsics。届时评估直接发 net8+ 的 DLL。
- 一致性：Burst 路径和标量路径必须写出、读出相同字节。测试要覆盖两条路径（同一套 golden bytes）。

### Core 分支交回的后续项
- ✅ Q1–Q8 已做（提交见上表 refactor/core 一行）。
- 验证缺口：B1/B2 的 ISA 跑法当时没真跑（`env` 是空壳，改用 /usr/bin/env）；Mono/net472 和 qemu Arm 未跑 —— 集成分支上已补跑 net10 三种 ISA，Mono/Arm 待跑。
- 写入一个百万层自嵌套集合仍会无限递归（只发生在调用方自己的对象上），未修。
- ✅ 生成器的 IsRawStruct 要与运行时的判断保持一致（原始结构体的生成格式化器是 INinoRawFormatter）。phase3/gen：RawRuleAgreementTests 在 70 种形状上比较生成器 RawRules.IsRawUnmanaged 与 Core NinoReflection.IsRawUnmanaged，查出两处不一致并在生成器里修正（c2b8ee0）：闭合的泛型 [NinoType] 结构体（`Pad<int>`）作为成员时按原始内存写（含填充 8 字节），而它的格式化器（`Box<T>` 里、它自己的注册）逐成员写（5 字节）—— 现在按定义判断，与格式化器一致（Wire-Format）；无类型参数的 `System.ValueTuple` 与 Core 一样按原始值。

### 集成时我修的
- netstandard2.1 测试构建：Convert.FromHexString → Rt.FromHex。
- 3 个提交的 trailer 与 Co-Authored-By 之间多了空行（release_notes.py 读不到），只改提交信息。
- 一致性语料重新生成（布局字符串 + 两条自嵌套结果），Go layouts.go / Rust derive.rs 同步；Go 的 RecordChain 保留 `deferrable=false`（J 项）。
- 目录重组后模糊测试工程受集中包版本管理约束：SharpFuzz 版本移进 Directory.Packages.props。
- asm.py compare：23 个"变差"都已解释（Immutable 按值传 writer 是有意的；多态/Pooled/HashSet 单独重跑无变差；拦截器 SerializeNNN 编号因删除 TupleProbe 错位）。

### 生成器分支交回的后续项（合并后处理）
- ✅（已在 main）J：Core 的 `NinoTypeLayoutAttribute` 加 `public bool Deferrable { get; set; }`（保留 7 参构造），ToString 在 members 前加 `; deferrable=true|false`（仅设置时）；规范 §8.1/§7.10.1/§10 第 5 条同步；TS LayoutReader 读 NamedArguments["Deferrable"]；Go 对齐。
- Core 的 NinoLayout 未建模 Int128/UInt128 在 .NET 8+ 的 16 字节对齐（生成器已把含 Int128 的结构视为不可移植）。
- ✅ 可选：Core 加 ValueTuple`8（TRest）格式化器，之后生成器可去掉 LongTupleFormatter。phase3/gen（2505e80）：TupleFormatters.g.cs 模板加八参数的 ValueTupleFormatter（逐项写，与 LongTupleFormatter 字节相同，不做块格式化器），生成器改用它并删掉 LongTupleFormatter；运行时解析 8 项以上的元组（泛型 `Box<T>` 里）以前抛“no formatter”，现在可用（Fixed）。
- ✅（已在 main）docs/diagnostics.md 加 NINO027；NinoFormerNameAttribute 注释提到 NINO027；nino.docs 诊断页同步。
- ✅ Q11：在已有类型前插入类型会让之后的格式化器全部重新输出（Combine 展平所致），输出正确，只是增量缓存失效。2c30ac4 按值缓存格式化器文件（插入时之后的类型拿回同一个 SourceText，不再重新输出；Roslyn 按位置匹配本身无法改）；phase3/gen 加 AddingATypeLeavesTheOtherOutputsCached（0f8c74c）（开启步骤跟踪：在文件末尾或新文件里加类型，其他模型 Cached/Unchanged，只多输出它自己的文件和注册表/分派）。
- ✅ Q1（db8e667 TypeShape：每类型一次的记忆化事实，TypeRules 统一 HasTop/DispatchesSubtypes）、Q2（4fbbc24 ProjectFacts 一步读整个编译、调用按文件和位置缓存、WithTrackingName）、Q3（43cfc20 GeneratedCode.File 与别名常量、TypeModel.Self/DispatchEntry.Self 同一规则）、Q4（5d8d010 FormatterRef）、Q5（72d2d5f TypeModel 子记录、MemberModel 具名参数、Build 拆成 BuildMembers/BuildPartial/BuildDispatch/BuildRegistrations、不可变 BodyMode、BodySignature 改名 EmitForwardersReturningSignature）、Q7（43cfc20 原始字符串；phase3/gen 把最后一处 `{{ }}` 转义模板也改成原始字符串，d026e19，输出不变）均已完成。
- Immutable：链接 Core 的 NinoThrow 不可行（Immutable 也对 UnitTests 开放内部成员，且 NinoThrow 依赖 NinoDepth）；SkipLocalsInit 已做（b293049，asm.py 对比无变差，树复制少一条序言清零）。

## 文档规则（每项用户可见的改动都要做）

- Nino 提交：Conventional Commits，用户可见的改动在最后一段加 `Fixed:` / `Added:` / `Performance:`（实测、仅完整模式数字）/ `Wire-Format:` 尾注，`Closes #N` 放在尾注上方；重构、测试、CI、文档不加。
- Nino 文档：README（中英）、docs/（design、pitfalls、spec、diagnostics）随代码更新。
- nino.docs：changelog 中英文按尾注写下一版本的条目，每条对照源码并链接文档页；新功能（安全设置、限制 API、多语言运行时）写对应的 v4 页面（中英文同结构，中文标题带英文 id），`bun run check && bun run build` 通过后推送。

## 进行中工作需要的文档页（nino.docs，中英文）

- 安全：新增 v4/security（设置、限制、Untrusted 模式）；更新 basic#invalid-data、vs-memorypack#untrusted-input、faq、README“适合不可信数据”段、docs/v4-design.md
- NinoDataException：所有“抛 InvalidOperationException（Nino: 前缀）”的说法（README 中英、basic、vs-memorypack、types、diagnostics NINO021/026）、不可变集合重复键说明、migration、whats-new、changelog
- 多语言：新增 v4/wire-format（或移动 platforms#binary-format）；advanced/go、advanced/rust（与 typescript 并列）；advanced 索引卡片和 meta；平台表、README“Built for”表和安装段
- 一致性语料：wire-format 页一节 + CONTRIBUTING
- slnx / 目录重组：CONTRIBUTING、benchmarks/README、CLAUDE.md、docs/*.md 的 src/ 路径；nino.docs 的 GitHub 链接（perf、typescript、analyzers、changelog）、sync-benchmarks.ts、nino.docs CLAUDE.md 的源码路径
- 命名空间迁移：v4-design、custom-formatters、polymorphism、advanced/unity、platforms、migration 里提到 NinoFormatterCache 等的行

## 待用户操作

- ⬜ report.yml 8 分片拆分后的耗时，需下次发布或手动运行确认（`gh workflow run report.yml`）
- ⬜ NuGet 可信发布：给 Nino.Codegen、Nino.Codegen.Tool 两个新包配置
- ⬜ 首个带 Nino.Codegen 的版本发布后，在 NuGet 上把 Nino.TypeScript 标为弃用并指向 Nino.Codegen
- ✅ 维护者已决定（2026-10-24），由其他分支实现：
  - TS 继续把 Kind=Unspecified 的 DateTime 读成 UTC，写进文档；
  - Go 普通 string 继续把 null 读成 ""（要保留 null 用 *string）；
  - 泛型类型在每种语言里都生成（nino-gen 生成泛型类型）；
  - ConcurrentDictionary 也受 MaxHashCollisions 上限保护；
  - Rust 的映射类型改为选项：默认 indexmap，可选 std HashMap；
  - npm 包名 nino-serialization，crate 名和 Go 模块路径不变；
  - 自嵌套集合的写入超过 MaxDepth 时抛异常（不再无限递归）

## 记录

- 2026-10-23：维护者确认审计五项全部修复（①④ core 分支，⑤ ts-perf 分支，②③ 第二阶段）。
- 2026-10-23：启动第一阶段四个分支；追加 bench、ts-perf、FFI 调研、安全审计；FFI 报告完成；TS 追加单元测试和目录整理。
- 2026-10-23：集成分支 integrate 摘入 11 个分支 + gen 分支 17 个提交；补 slnx/global.json、Gradient -1、AspNetCore 可回收类型缓存、CLAUDE.md（共用优先、TS 一节）。单测整套在容器里因 OOM 中断（多代理同时跑），需分段跑。
- 2026-10-23：gen、core 分支合入 integrate；全量验证（三种配置、三种 ISA、AspNetCore、TS、Go、Rust、一致性语料）通过；跑完目录重组脚本；nino.docs 写好 rc.8 更新日志。
- 2026-10-24：phase3/gen：生成器后续项核对完毕（Q1–Q5、Q7、Q11、J、NINO027 已在 main）；修生成器与运行时 raw 判断的两处不一致（闭合泛型 [NinoType] 结构体，Wire-Format）；Core 加八参数 ValueTupleFormatter，生成器删 LongTupleFormatter。
- 2026-10-24：命名空间定为 Nino.Core.CompilerServices；Nino.Codegen 统一 TS/Go/Rust；benchmarks/v3 并入；单元测试按领域整理；Mono/Arm 跨运行时验证，修 Arm32 上 Int128 布局测试。
- 2026-10-24：第三阶段 phase3/core：核对 Q1–Q8 均已在第二阶段完成；补 Arm64 查表位命名、AVX-512 组循环 ref 游标、NinoMask 复制去重、Begin 内联、区段结束的重复检查；`allows ref struct` 评估后不做（理由见第二阶段一节）。
