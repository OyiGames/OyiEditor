# OyiEditor

Desktop editor for OyiEngine, written in C# with Avalonia (Windows and macOS). The editor runs in its own process
and talks to a running engine (`oyi serve --rpc`) over TCP with length-prefixed FlatBuffers messages. It depends only
on the OyiProtocol schema, never on engine sources.

中文版：[README.zh-CN.md](README.zh-CN.md)

## Build

Prerequisites: .NET SDK 10 (`global.json` pins the 10.0 feature band).

```bash
dotnet build OyiEditor.slnx -c Release
dotnet test --solution OyiEditor.slnx -c Release
dotnet run --project src/OyiEditor.App
dotnet format OyiEditor.slnx --verify-no-changes
```

## Layout

```text
src/OyiEditor.App/          Avalonia application: main window, connection panel, command console
src/OyiEditor.Protocol/     Wire protocol client: framing (FlatBuffers bindings and RPC client to follow)
tests/OyiEditor.Protocol.Tests/   xUnit tests for the protocol library
```

Package versions are managed centrally in `Directory.Packages.props`; shared compiler settings live in
`Directory.Build.props`.

## Status

Early scaffold. Framing matches the engine (32-bit little-endian length, 16 MiB limit). The handshake and commands
arrive once OyiProtocol publishes its first tag, which this repository will consume as a git submodule.

## License

MIT, see [LICENSE](LICENSE).
