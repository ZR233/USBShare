# USBShare

WinUI 3 GUI 工具，用于把本地 `usbipd-win` 设备通过 SSH 隧道分享给 Linux 远端并自动 attach。

## 已实现能力

- 维护 SSH 远程（密码/私钥双认证）
- 保存 SSH 密码与 sudo 密码（Windows DPAPI 加密）
- 展示 USB Hub/Device 全量树形结构（不可分享设备灰显）
- 为 Hub 或 Device 配置分享规则
- 规则优先级：`Hub > Device`
- 多级祖先 Hub 命中不同远程时，标记冲突并跳过
- 开始分享后持续轮询（默认 3 秒，可配置 1-30 秒）
- 自动执行：
  - 本地 `usbipd bind`
  - SSH 反向隧道 `remote:<tunnelPort> -> local:3240`
  - 远端 `sudo usbip attach`
- 停止分享后完整回滚会话管理资源（detach/unbind/关闭会话）

## 先决条件

- Windows 已安装 `usbipd-win`（当前按 5.x 命令行为实现）
- 已安装 OpenSSH 客户端（`ssh.exe`）
- Linux 远端可用 `usbip` 与 `sudo`
- 本工具运行时需要管理员权限（用于 `usbipd bind/unbind`）

## 构建与测试

```powershell
dotnet build USBShare.csproj -c Debug -p:Platform=x64
dotnet test tests\USBShare.Tests\USBShare.Tests.csproj -c Debug
```

注意：Packaged WinUI 项目默认 `AnyCPU` 会触发 MSIX 架构错误，开发与验证请统一使用 `x64` 平台。
