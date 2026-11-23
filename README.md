# 游戏联机大厅

用 EasyTier 把异地玩家的 Windows 电脑连进同一个虚拟网络，再从《文明 VI》的本地网络大厅加入对局。首版面向 Windows 10/11 x64。实际跨网搜房和回合推进待双机实测。

## 玩家使用

1. 双方从 [Releases](https://github.com/LimingDusk/easytier-game-lobby/releases) 下载 `GameLobby-win-x64.exe`。
2. 双击并接受 Windows 管理员权限提示。首次创建或加入房间时，启动器会将自带的 EasyTier v2.6.4 解包到 `%ProgramFiles%\EasyTierGameLobby\engines`。联机时请保持启动器运行。
3. 房主选择《文明 VI》，点「创建房间」，复制邀请码私发给朋友。朋友粘贴后点「加入房间」。邀请码包含该虚拟网络的密钥。整个游戏过程中保持大厅运行。
4. 双方点「刷新节点」，检查是否看到对方的虚拟 IP。房主在游戏里选「多人游戏 → 本地网络 → 创建游戏」，朋友在同一菜单选择该对局。完成后点「离开房间」。

节点列表用于区分网络连接问题和游戏大厅发现问题。若只有本机节点，先核对邀请码及对公共引导节点的连通性。若已看到队友但搜不到游戏，请在组网后重新进入本地网络菜单，核对游戏版本、模组和游戏在虚拟网卡上的防火墙权限。

## 实现和验证

- `src/Tunnel.cs` 管理独立的 EasyTier 进程，使用公共引导节点、房主建房时配置独立的本机虚拟 IP，加入者由 EasyTier 动态分配地址，并通过同版本 CLI 查询节点。房间密钥通过子进程环境传递。
- `src/Games/GameProfile.cs` 登记游戏标识、显示名称、广播需求及双方的入局指引。新增采用相同联机流程的游戏可以增加一个条目。
- 在 Windows 上用 .NET 8 SDK 运行 `dotnet run --project tests/RoomChecks.csproj` 和 `dotnet build src/GameLobby.csproj`。运行开发构建时，将内核安装到 GameLobby.exe 旁边的 engine 目录。在 GitHub Actions 运行 Windows build 可获取单文件 EXE，推送 v* 标签会自动发布 GitHub 预发行版。发布构建嵌入经过 SHA-256 验证的官方内核压缩包。
- 双机验收使用两条不同的上网线路。确认双方节点可见，记录直连或中转路径，在《文明 VI》本地网络列表发现房间并加入，推进一回合，离开后重新加入。需两台装有兼容版本游戏的 Windows 电脑。

## 上游

[EasyTier v2.6.4 发行包与广播中继说明](https://github.com/EasyTier/EasyTier/releases/tag/v2.6.4) · [EasyTier 对应源码及许可证](https://github.com/EasyTier/EasyTier/tree/v2.6.4) · [文明 VI 官方游戏手册](https://downloads.2kgames.com/civilization/vi/manuals/eu/CIV_VI_25TH_ONLINE_MANUAL_AUS.pdf)

## 连接诊断与日志

房主独自建房也会创建虚拟网卡。默认房主地址为 `10.126.126.1/24`，若与活动网卡冲突会另选私网网段。大厅分别显示本机地址、共享节点和队友。主窗口每 4 秒查询节点、引导连接和 UDP 广播数据。两台电脑使用相同的邀请码，日志里的房间标识应该一致。若只有本机且引导节点均未连接，可在双方离开房间后填写同一个可达的 EasyTier 共享节点地址，再重新加入。双方能看到彼此虚拟 IP 后，重新进入文明 VI 的本地网络大厅，观察建房、搜房前后的广播计数。计数包括其他应用的广播。

在两台电脑上分别点击「导出日志」，保存最近 600 条带时区的记录。日志包含角色、房间标识、节点名称、虚拟 IP、引导地址、内核错误和广播统计。启动器会隐藏 GL1 邀请码和房间密钥。本地日志位于 `%LOCALAPPDATA%\EasyTierGameLobby\Logs`，每次启动单独记录。
