# OyiEditor

OyiEngine 的桌面编辑器，C# + Avalonia（Windows 与 macOS）。编辑器是独立进程，通过 TCP 和长度前缀的 FlatBuffers 消息与运行中的引擎（`oyi serve --rpc`）通信。只依赖 OyiProtocol 的 schema，不依赖引擎源码。

English: [README.md](README.md)

## 构建

前置：.NET SDK 10（`global.json` 锁定 10.0 功能带）。协议 schema 是 git 子模块：

```bash
git clone --recurse-submodules https://github.com/OyiGames/OyiEditor.git   # 或：git submodule update --init
dotnet build OyiEditor.slnx -c Release
dotnet test --solution OyiEditor.slnx -c Release
dotnet run --project src/OyiEditor.App
dotnet format OyiEditor.slnx --verify-no-changes --exclude protocol src/OyiEditor.Protocol.Schema
```

把 `OYI_EXE` 设为引擎 CLI（`oyi` / `oyi.exe`）即可同时跑对真实 `oyi serve --rpc --once` 的集成测试；不设则跳过。

## 协议绑定

`protocol/` 是锁定到某个 tag 的 [OyiProtocol](https://github.com/OyiGames/OyiProtocol)。
`src/OyiEditor.Protocol.Schema/Generated/` 里的 C# 绑定由 flatc 生成并提交，构建不需要 flatc。子模块换 tag 后，
用协议仓库锁定的 flatc 重新生成（来自 fork [OyiGames/flatbuffers](https://github.com/OyiGames/flatbuffers/releases)
的 release，tag 见 `protocol/.github/workflows/ci.yml` 的 `FLATBUFFERS_TAG`）：

```bash
git -C protocol checkout v0.1.2
python scripts/generate_protocol.py --flatc path/to/flatc
```

提交的绑定与 schema 不一致时 CI 失败。

## 目录

```text
protocol/                         OyiProtocol 子模块（schema、vendor 的 FlatBuffers C# 运行时）
src/OyiEditor.App/                Avalonia 应用：主窗口、连接面板、命令控制台
src/OyiEditor.Protocol/           RPC 客户端：分帧、Envelope 编解码、RpcClient
src/OyiEditor.Protocol.Schema/    生成的绑定 + vendor 的 FlatBuffers 运行时（不放手写代码）
tests/OyiEditor.Protocol.Tests/   对脚本化假引擎的客户端测试，以及真实引擎测试
tests/OyiEditor.App.Tests/        控制台解析 / 格式化，以及端到端的视图模型测试
scripts/generate_protocol.py      重新生成或检查绑定
```

包版本集中在 `Directory.Packages.props` 管理；共用的编译设置在 `Directory.Build.props`。

## 状态

可连接 `oyi serve --rpc`：Hello / Welcome 握手（协议版本 1）、按 id 匹配的并发请求、流帧，以及调用引擎命令的控制台（`ping`、`version`、`echo -- 文本`）。还没有 world 视图。

## 许可

MIT，见 [LICENSE](LICENSE)。
