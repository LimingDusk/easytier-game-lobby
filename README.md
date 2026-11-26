# 游戏联机大厅

基于 EasyTier 的 Windows 10/11 x64 联机工具，内置 EasyTier v2.6.4。 [下载 Windows x64](https://github.com/LimingDusk/easytier-game-lobby/releases)

## 快速开始

1. 下载 `GameLobby-win-x64.exe` 并双击运行，按提示授予管理员权限。
2. 双方选择同一个游戏模块。房主点「创建房间」并分享邀请码，好友粘贴后点「加入房间」。
3. 在「房间成员」查看虚拟 IP，联机期间保持大厅运行。

- **《文明 VI》** 在「多人游戏 → 本地网络」中建房或加入。
- **我的世界 Java 版** 房主在单人世界选「对局域网开放」，好友在「直接连接」输入 `房主虚拟IP:端口`。端口以游戏聊天栏本次显示为准。
- **自定义联机** 房主在游戏内建房，好友搜房或用房主的虚拟 IP 和游戏端口直连。

## 更新

大厅启动时自动检查更新。离开房间后点「安装更新」，校验通过后大厅会重启。

下载中断会自动重试，暂停后可继续下载。旧版反复更新失败时，先手动下载并运行最新版。

## 排查与构建

「网络诊断」可查看节点连接、设置引导地址和导出日志。建房时默认随机选引导节点，本机监听端口由系统分配。

Windows 上用 .NET 8 SDK 执行 `dotnet build src/GameLobby.csproj`。运行开发版时，把 `easytier-core.exe` 和 `easytier-cli.exe` 放到 EXE 同级的 `engine` 目录。 [构建与测试流程](.github/workflows/windows.yml) · [EasyTier 发行包](https://github.com/EasyTier/EasyTier/releases/tag/v2.6.4) · [EasyTier 源码和许可证](https://github.com/EasyTier/EasyTier/tree/v2.6.4)
