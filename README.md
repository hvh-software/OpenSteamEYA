# SteamEYA

[中文](README.md) | [English](README_EN.md)

用 EYA 令牌登录和管理 Steam 账号的 Windows 桌面工具。

[![][latest-version-shield]][latest-version-link]
[![][github-downloads-shield]][github-downloads-link]
[![][github-stars-shield]][github-stars-link]
[![][github-license-shield]][github-license-link]

SteamEYA 用 EYA 令牌（一种 Steam 登录凭据）代替账号密码登录，不需要手动输入密码或处理令牌验证器。除了上号，它还能查询账号的优先分、CS2 等级和冷却状态，管理用过的账号，并清理创意工坊订阅。

**👉 [前往 Releases 下载最新版本](https://github.com/hvh-software/OpenSteamEYA/releases)**

## 界面截图

<p align="center">
  <img src="https://i.imgur.com/z75EaCd.png" alt="SteamEYA 界面截图 1"><br><br>
  <img src="https://i.imgur.com/Waxspvt.png" alt="SteamEYA 界面截图 2"><br><br>
  <img src="https://i.imgur.com/d2K2HRe.png" alt="SteamEYA 界面截图 3">
</p>

## 功能

**独家功能**
- **一键配装** —— 在「配装」页用拖拽搭好 T / CT 整套武器（仿 CS2 购买菜单界面），自动存为该账号的预设；上号时一键把整套原版武器（含 T / CT 专属枪）装备到账号，全程无需进游戏。

**两种登录方式**

- **手动模式** —— 填入账户名和 EYA 令牌，一键登录到本机 Steam 客户端。
- **自动模式** —— 输入卡密，自动从上游服务器解析出账号并登录。

**账号状态一目了然**

登录或粘贴令牌后，右侧面板会显示：

- Steam64 ID 与 EYA 令牌（JWT）过期时间
- 令牌可用状态
- CS2 优先分、等级与冷却 / 封禁状态

点「一键查询」即可联网刷新这些信息。

**历史账号管理**

- 登录或查询过的账号自动入库，带头像和昵称
- 按账户名、昵称或 Steam64 搜索
- 从剪贴板批量导入（`账户名----令牌` 格式），或把选中账号导出回剪贴板
- 对任意历史账号直接一键查询或重新登录
- 全选当前列表批量查询，或一键查询全部历史账号，查询过程可随时取消
- 按 CS2 VAC 标记、优先分区间、国服状态及分组组合筛选（基于最近一次查询）

**清理创意工坊订阅**

一键取消当前账号在 CS2（AppID 730）下的全部创意工坊订阅。

**自动更新**

启动时检查 GitHub Releases，有新版本会在「关于」页提示，并可直接在应用内下载并启动安装器完成更新。

**多语言界面**

内置简体中文、繁体中文、English，可在「设置」页随时切换，立即生效。

## 安装

1. 在 [Releases](https://github.com/hvh-software/OpenSteamEYA/releases) 下载最新的 `SteamEYA-<版本>-win-x64-setup.exe`。
2. 双击安装包，按安装向导完成安装。
3. 从开始菜单或桌面快捷方式启动 SteamEYA。

环境要求：Windows 10 1809（Build 17763）及以上，并已安装 Steam 客户端。无需额外安装任何运行时。

## 常见问题

**安装器启动失败怎么办？**

请先检查是否被安全软件拦截，或前往 [Releases](https://github.com/hvh-software/OpenSteamEYA/releases) 重新下载最新安装包后重试。

**EYA 令牌是什么？**

一种以 `eyAi` 开头的 Steam 登录凭据（JWT）。手动模式直接粘贴它即可登录，无需账号密码。

**为什么上号后没切换账号？**

上号前需要先完全退出正在运行的 Steam。如果 Steam 以管理员权限运行而本程序没有，会无法结束它，程序会提示你手动退出后重试。

## 从源码构建

需要 .NET 10 SDK 和 Visual Studio 的 C++ 工具链。

```bash
git clone https://github.com/hvh-software/OpenSteamEYA.git
cd OpenSteamEYA
dotnet build SteamEyaWinUI/SteamEyaWinUI.csproj -c Release
```

构建安装包（需先安装 Inno Setup 6）：

```powershell
./scripts/build-installer.ps1 -Version 0.1.0
```

## 🤝 参与贡献

如果您对这个项目感兴趣，欢迎参与贡献，也欢迎 "Star" 支持一下 ^_^ <br>
以下为提 PR 并合并的小伙伴，在此感谢项目中所有的贡献者。

<a href="https://github.com/hvh-software/OpenSteamEYA/graphs/contributors" target="_blank">
  <table>
    <tr>
      <th colspan="2">
        <br><img src="https://contrib.rocks/image?repo=hvh-software/OpenSteamEYA"><br><br>
      </th>
    </tr>
  </table>
</a>

1. Fork 本项目
2. 创建新分支：`git checkout -b feature/amazing-feature`
3. 提交更改：`git commit -m "Add amazing feature"`
4. 推送分支：`git push origin feature/amazing-feature`
5. 发起 Pull Request，等待合并

## 许可证

本项目基于 [MIT 许可证](LICENSE) 开源。

<!-- LINK GROUP -->

[latest-version-shield]: https://img.shields.io/github/v/release/hvh-software/OpenSteamEYA?style=flat-square&label=latest%20version&labelColor=black
[latest-version-link]: https://github.com/hvh-software/OpenSteamEYA/releases
[github-downloads-shield]: https://img.shields.io/github/downloads/hvh-software/OpenSteamEYA/total?style=flat-square&logo=github&label=downloads&labelColor=black
[github-downloads-link]: https://github.com/hvh-software/OpenSteamEYA/releases
[github-stars-shield]: https://img.shields.io/github/stars/hvh-software/OpenSteamEYA?style=flat-square&logo=github&labelColor=black
[github-stars-link]: https://github.com/hvh-software/OpenSteamEYA/stargazers
[github-license-shield]: https://img.shields.io/github/license/hvh-software/OpenSteamEYA?style=flat-square&logo=github&labelColor=black
[github-license-link]: https://github.com/hvh-software/OpenSteamEYA/blob/main/LICENSE
