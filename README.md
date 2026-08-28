# Performance Monitor

Lightweight dual-PC Windows performance monitor with LAN monitoring.

Performance Monitor 是一个轻量的 Windows 本机与局域网双机性能监视器。同一个程序既显示本机指标，也可在用户主动启用后提供只读 LAN 监控端点。

## Features

- CPU usage, temperature, and real-time package power
- GPU usage and temperature
- VRAM usage
- RAM usage
- Aggregate disk activity and read/write throughput
- Aggregate network download/upload throughput
- Fan speed when supported
- LAN remote-device monitoring with Online/Offline state
- System tray with Open, Settings, and Exit actions
- Optional sign-in startup through Windows Task Scheduler

温度、功耗和风扇等硬件传感器取决于 CPU/GPU、主板、BIOS/固件、驱动，以及 LibreHardwareMonitor 是否能够读取。不可用或异常的传感器显示为 `--`；程序不会用 TDP、功耗限制或估算值冒充实时数据。

## Requirements

- Windows 10 or Windows 11
- x64 processor
- Administrator approval when the application starts

正式安装包是 `win-x64` self-contained 发布，不需要另外安装 .NET Desktop Runtime。

AMD CPU 温度和 Package Power 通过 LibreHardwareMonitor 与 PawnIO 读取。正式安装器会在 PawnIO 2.x 缺失时安装 LibreHardwareMonitor 0.9.6 随附、有效签名的官方 PawnIO 2.1.0.0 组件；已有兼容的 PawnIO 2.x 不会重复安装。`PerformanceMonitor.exe` 使用 Windows `requireAdministrator` 清单启动，因此双击时会出现 UAC 提示。

关闭主窗口会将程序隐藏到系统托盘；双击托盘图标可恢复窗口，也可从托盘菜单打开设置或明确退出。设置中的 **Start with Windows** 默认关闭。用户主动开启后，程序会为当前用户创建登录触发的 Windows 计划任务，以最高权限直接运行 `PerformanceMonitor.exe --start-minimized`，无需 CMD/PowerShell 中转，也不会在每次登录时再次弹出 UAC。关闭该选项或卸载程序会删除这个计划任务。

## Install

1. 打开本仓库的 **Releases** 页面。
2. 下载 `PerformanceMonitor-Setup-v1.0.0.exe`。
3. 运行安装程序，可按需勾选桌面快捷方式。

开发构建需要 .NET 8 SDK：

```powershell
dotnet build .\PerformanceMonitor.sln -c Release -p:Platform=x64
```

## LAN Setup

电脑 A（被监控端）：

1. 打开 **Settings**。
2. 勾选 **Enable LAN Monitoring**。
3. 记下本机 IPv4、端口（默认 `52100`）和 Access Token。
4. 保存设置。

电脑 B（查看端）：

1. 打开 **Settings → Remote Devices**。
2. 点击 **Add**。
3. 填写 Display Name、电脑 A 的 IPv4、端口和完全相同的 Access Token。
4. 保存设置，设备会自动显示 Online 或 Offline。

如 Windows 防火墙阻止连接，仅为本程序或 TCP `52100` 添加 **Private Network（专用网络）** 入站许可。不要向公用网络或互联网转发该端口。

设置保存在 `%LOCALAPPDATA%\PerformanceMonitor\settings.json`。升级或正常卸载不会删除该文件，因此 Remote Devices 和 Token 配置会保留。

## Security and Privacy

- No telemetry and no cloud service
- LAN API is read-only: `GET /api/v1/status`
- Requests require a Bearer Token
- LAN Monitoring is disabled by default
- HTTP traffic is not encrypted; use only on a trusted home LAN
- The LAN API does not expose command execution, file operations, fan control, shutdown/restart, Windows changes, or arbitrary PawnIO/MSR/SMN access
- No proxy, browser, Defender, firewall, Windows security setting, or global port-opening changes are made by the installer
- The installer only adds PawnIO when a compatible PawnIO 2.x installation is absent; uninstalling Performance Monitor keeps PawnIO because other hardware-monitoring applications may share it

开机启动仅在用户主动勾选后创建当前用户的 Windows 计划任务；不使用 HKCU Run，任务只启动本程序且没有远程控制能力。安装器不会自动创建防火墙规则；如用户确实需要跨设备访问，应只为本程序的 TCP `52100` 在 **Private（专用）** 网络中手动放行。

## License

Performance Monitor 自有代码使用 [MIT License](LICENSE)。第三方组件及其许可证见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
