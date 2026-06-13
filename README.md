# SteamEYA

用 EYA 令牌登录和管理 Steam 账号的 Windows 桌面工具。

[![][latest-version-shield]][latest-version-link]
[![][github-downloads-shield]][github-downloads-link]
[![][github-stars-shield]][github-stars-link]
[![][github-license-shield]][github-license-link]

SteamEYA 用 EYA 令牌（一种 Steam 登录凭据）代替账号密码登录，不需要手动输入密码或处理令牌验证器。除了上号，它还能查询账号的优先分、CS2 等级和冷却状态，管理用过的账号，并清理创意工坊订阅。

**👉 [前往 Releases 下载最新版本](https://github.com/tuntun1337/opensteameya/releases)**

## 功能

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

**清理创意工坊订阅**

一键取消当前账号在 CS2（AppID 730）下的全部创意工坊订阅。

**自动更新**

启动时检查 GitHub Releases，有新版本会在「关于」页提示并提供下载。

## 安装

1. 在 [Releases](https://github.com/tuntun1337/opensteameya/releases) 下载最新的 `SteamEYA-<版本>-win-x64.7z`。
2. 用 [7-Zip](https://www.7-zip.org/) 或 NanaZip **完整解压**到一个普通目录。
3. 双击 `SteamEyaWinUI.exe` 运行。

环境要求：Windows 10 1809（Build 17763）及以上，并已安装 Steam 客户端。无需额外安装任何运行时。

## 常见问题

**双击 exe 没反应？**
请确认压缩包已**完整解压**到普通目录，不要在压缩包内直接运行——程序的运行时文件必须和 exe 在同一目录。

**EYA 令牌是什么？**
一种以 `eyAi` 开头的 Steam 登录凭据（JWT）。手动模式直接粘贴它即可登录，无需账号密码。

**为什么上号后没切换账号？**
上号前需要先完全退出正在运行的 Steam。如果 Steam 以管理员权限运行而本程序没有，会无法结束它，程序会提示你手动退出后重试。

## 从源码构建

需要 .NET 10 SDK 和 Visual Studio 的 C++ 工具链。

```bash
git clone https://github.com/tuntun1337/opensteameya.git
cd opensteameya
dotnet build SteamEyaWinUI/SteamEyaWinUI.csproj -c Release
```

## 🤝 参与贡献

如果您对这个项目感兴趣，欢迎参与贡献，也欢迎 "Star" 支持一下 ^_^ <br>
以下为提 PR 并合并的小伙伴，在此感谢项目中所有的贡献者。

<a href="https://github.com/tuntun1337/opensteameya/graphs/contributors" target="_blank">
  <table>
    <tr>
      <th colspan="2">
        <br><img src="https://contrib.rocks/image?repo=tuntun1337/opensteameya"><br><br>
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

[latest-version-shield]: https://img.shields.io/github/v/release/tuntun1337/opensteameya?style=flat-square&label=latest%20version&labelColor=black
[latest-version-link]: https://github.com/tuntun1337/opensteameya/releases
[github-downloads-shield]: https://img.shields.io/github/downloads/tuntun1337/opensteameya/total?style=flat-square&logo=github&label=downloads&labelColor=black
[github-downloads-link]: https://github.com/tuntun1337/opensteameya/releases
[github-stars-shield]: https://img.shields.io/github/stars/tuntun1337/opensteameya?style=flat-square&logo=github&labelColor=black
[github-stars-link]: https://github.com/tuntun1337/opensteameya/stargazers
[github-license-shield]: https://img.shields.io/github/license/tuntun1337/opensteameya?style=flat-square&logo=github&labelColor=black
[github-license-link]: https://github.com/tuntun1337/opensteameya/blob/main/LICENSE
