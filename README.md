# 游戏联机大厅

用 EasyTier 把异地玩家的 Windows 电脑连进同一个虚拟网络，再通过游戏内的局域网列表或虚拟 IP 加入对局。支持 Windows 10/11 x64，内置 EasyTier v2.6.4，首次进房时自动解包。

## 快速开始

1. 双方下载 `GameLobby-win-x64.exe`，双击并接受管理员权限提示。[GitHub 发行版](https://github.com/LimingDusk/easytier-game-lobby/releases)
2. 选择同一个游戏模块。房主点「创建房间」，复制邀请码私发给好友。好友粘贴邀请码后点「加入房间」。
3. 点「刷新节点」，在「房间成员」查看虚拟 IP 和连接方式，再进入游戏。联机期间保持大厅运行，结束后点「离开房间」。

大厅启动时会检查更新。发现新版本后，离开房间再点「安装更新」。下载支持断点续传，校验通过后大厅会重启。底部的「GitHub」可打开项目仓库。

## 游戏内联机

- 在 **《文明 VI》** 中，双方进入「多人游戏 → 本地网络」，房主创建对局，好友选中加入。跨网搜房和回合推进已通过双机实测。
- 使用 **我的世界局域网（Java 版）** 时，房主开放单人世界，分享大厅显示的虚拟 IP 和游戏显示的端口。好友在「多人游戏 → 直接连接」输入 `房主虚拟IP:端口`。
- **自定义联机** 适用于支持局域网搜房或 IP 直连的游戏。房主在游戏内建房，好友搜房或用房主虚拟 IP 和端口直连。

## 网络诊断

- 房主可在建房前通过「网络诊断」指定引导地址。留空时，每次建房随机选一个共享节点，地址随 GL2 邀请码分享。加入旧版 GL1 房间时，可手填房主使用的地址。
- 「网络诊断」显示实际引导地址、连接状态和 UDP 广播统计。若房间里只有本机，先核对邀请码和引导连接。若已看到队友但进不了游戏，检查端口、游戏版本、模组和 Windows 防火墙入站规则。
- 本机 EasyTier 的监听端口由系统分配。引导地址使用公共节点的服务端口，游戏直连使用游戏显示的端口。
- 「导出日志」可保存最近 600 条记录，邀请码和密钥会自动遮挡。本地日志位于 `%LOCALAPPDATA%\EasyTierGameLobby\Logs`。

内置节点为 `tcp://150.158.142.253:11010` 和 `tcp://225284.xyz:11010`。[DreamLife 节点说明](https://easytier.dreamlife.indevs.in/) · [QtEasyTier 公共节点公告](https://info.qtet.cn/)

## 开发与测试

在 Windows 上使用 .NET 8 SDK。运行开发构建时，将 EasyTier 放在 `GameLobby.exe` 同级的 `engine` 目录。

```powershell
dotnet build src/GameLobby.csproj
dotnet run --project tests/RoomChecks.csproj
dotnet run --project tests/updates/UpdateChecks.csproj
dotnet run --project tests/window/WindowChecks.csproj -- previews
```

游戏模块定义在 `src/Games/GameProfile.cs`，EasyTier 进程由 `src/Tunnel.cs` 管理。Windows build 工作流生成单文件 EXE 和界面预览，`v*` 标签触发预发行流程，内置的官方内核包经过 SHA-256 校验。

<details>
<summary>容量与性能复测</summary>

使用 Python 3.8+ 和 EasyTier v2.6.4，将 `PATH_TO_ENGINE` 换成同时包含 core 和 cli 的目录。

```sh
python tests/mesh_capacity.py --engine-dir PATH_TO_ENGINE --nodes 20
python tests/mesh_capacity.py --engine-dir PATH_TO_ENGINE --nodes 20 --relay
```

回环测试覆盖节点同步、离房重进和并发收发。Windows 跨网、广播搜房和游戏回合需双机验证。

对比 Windows 版本性能时，在同一台电脑进房后用管理员 PowerShell 运行 `./scripts/Measure-Room.ps1 -LobbyProcessId 1234 -Seconds 60`，将 `1234` 换成大厅进程 PID。脚本记录 CLI 次数、CPU 时间和工作集。

</details>

[EasyTier 发行包](https://github.com/EasyTier/EasyTier/releases/tag/v2.6.4) · [EasyTier 源码和许可证](https://github.com/EasyTier/EasyTier/tree/v2.6.4) · [《文明 VI》官方手册](https://downloads.2kgames.com/civilization/vi/manuals/eu/CIV_VI_25TH_ONLINE_MANUAL_AUS.pdf)
