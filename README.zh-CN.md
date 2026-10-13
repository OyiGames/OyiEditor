# OyiEditor

OyiEngine 的桌面编辑器，C# + Avalonia（Windows 与 macOS）。编辑器是独立进程，通过 TCP 和长度前缀的 FlatBuffers 消息与运行中的引擎（`oyi serve --rpc`）通信。只依赖 OyiProtocol 的 schema，不依赖引擎源码。

English: [README.md](README.md)

## 构建

前置：.NET SDK 10（`global.json` 锁定 10.0 功能带）。

```bash
dotnet build OyiEditor.slnx -c Release
dotnet test --solution OyiEditor.slnx -c Release
dotnet run --project src/OyiEditor.App
dotnet format OyiEditor.slnx --verify-no-changes
```

## 目录

```text
src/OyiEditor.App/          Avalonia 应用：主窗口、连接面板、命令控制台
src/OyiEditor.Protocol/     协议客户端：分帧（FlatBuffers 绑定与 RPC 客户端随后加入）
tests/OyiEditor.Protocol.Tests/   协议库的 xUnit 测试
```

包版本集中在 `Directory.Packages.props` 管理；共用的编译设置在 `Directory.Build.props`。

## 状态

起步脚手架。分帧与引擎一致（32 位小端长度，上限 16 MiB）。握手与命令等 OyiProtocol 打出第一个 tag 后接入，本仓库以 git 子模块引用它。

## 许可

MIT，见 [LICENSE](LICENSE)。
