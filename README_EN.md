# SteamEYA

[中文](README.md) | [English](README_EN.md)

A Windows desktop tool for Steam account management/login with EYA tokens.

[![][latest-version-shield]][latest-version-link]
[![][github-downloads-shield]][github-downloads-link]
[![][github-stars-shield]][github-stars-link]
[![][github-license-shield]][github-license-link]

SteamEYA uses EYA tokens, a type of Steam login credential, to replace traditional account username and password logins, without the need to manually enter the password or manage Steam authenticators. Besides the login function, it can also check account status such as Premier rank, CS2 account level, and cooldown status, manage previously logged-in accounts, and clear Workshop subscriptions to prevent large downloads after logging in to a new account.

**👉 [Download the latest version from Releases](https://github.com/hvh-software/OpenSteamEYA/releases)**

## Screenshots

<p align="center">
  <img src="https://i.imgur.com/z75EaCd.png" alt="SteamEYA screenshot 1"><br><br>
  <img src="https://i.imgur.com/Waxspvt.png" alt="SteamEYA screenshot 2"><br><br>
  <img src="https://i.imgur.com/d2K2HRe.png" alt="SteamEYA screenshot 3">
</p>

## Features

**Unique feature**
- **One-click loadout** - Build a full T / CT loadout by drag-and-drop on the Loadout page (a CS2 buy-menu-style editor), saved automatically as the account preset. Apply the whole set of base weapons (including CT/T-exclusive guns) to the account with a single click, without launching the game.

**Two Login Modes**

- **Manual mode** - Enter an account username and EYA token, then log in to the local Steam client with one single click.

**Clean UI for account status**

After logging in or inputting a token, the right-side panel shows:

- Steam64 ID and EYA token (JWT) expiration time
- Token availability
- CS2 Premier rank, level, and ban cooldown / status

Click **Refresh** to refresh all the data online.

**History Account Management**

- Accounts that have been logged in to are saved automatically, including avatar and username
- Search by account name, username, or Steam64 ID
- Batch import from the clipboard with `username----token` format, or export selected accounts to clipboard
- Refresh account status or log in the account again directly from any history account
- Select the visible list for batch queries, or query the entire history with one click; cancel anytime
- Combine CS2 VAC flag, Premier rating range, China access, and group filters using the latest query results

**Clear Workshop Subscriptions**

Cancel all Workshop subscriptions for the current account under CS2 (AppID 730) with one click. Prevent massive garbage automatically download into the disk upon login to a new account.

**Automatic Updates**

SteamEYA checks GitHub Releases on startup. If a newer version is available, you can download and launch the installer directly from the About page.

**Multi-language UI**

Ships with Simplified Chinese, Traditional Chinese, and English. Switch anytime on the Settings page; changes take effect immediately.

## Installation

1. Download the latest `SteamEYA-<version>-win-x64-setup.exe` from [Releases](https://github.com/hvh-software/OpenSteamEYA/releases).
2. Run the installer and finish the setup wizard.
3. Launch SteamEYA from Start Menu or Desktop shortcut.

Requirements: Windows 10 1809 (Build 17763) or later, with the Steam client installed. No additional runtime installation is required.

## FAQ

**The installer cannot be launched. What should I do?**

Check whether your security software blocks it, then re-download the latest installer from [Releases](https://github.com/hvh-software/OpenSteamEYA/releases) and try again.

**What is an EYA token?**

An EYA token is a Steam login credential (JWT) that starts with `eyAi`. In manual mode, you can paste it directly to log in without an account password.

**Why did the account not switch after logging in?**

You need to fully exit the running Steam client before logging in. If Steam is running as administrator and this program is not, SteamEYA cannot close it. In that case, the program will ask you to exit Steam manually and retry.

## Build From Source

.NET 10 SDK and the Visual Studio C++ toolchain are required.

```bash
git clone https://github.com/hvh-software/OpenSteamEYA.git
cd OpenSteamEYA
dotnet build SteamEyaWinUI/SteamEyaWinUI.csproj -c Release
```

Build installer (Inno Setup 6 required):

```powershell
./scripts/build-installer.ps1 -Version 0.1.0
```

## 🤝 Contributing

If you are interested in this project, contributions are welcome. A Star is also appreciated ^_^ <br>
Thanks to everyone who has opened and merged PRs for this project.

<a href="https://github.com/hvh-software/OpenSteamEYA/graphs/contributors" target="_blank">
  <table>
    <tr>
      <th colspan="2">
        <br><img src="https://contrib.rocks/image?repo=hvh-software/OpenSteamEYA"><br><br>
      </th>
    </tr>
  </table>
</a>

1. Fork this project.
2. Create a new branch: `git checkout -b feature/amazing-feature`
3. Commit your changes: `git commit -m "Add amazing feature"`
4. Push the branch: `git push origin feature/amazing-feature`
5. Open a Pull Request and wait for it to be merged.

## License

This project is open source under the [MIT License](LICENSE).

<!-- LINK GROUP -->

[latest-version-shield]: https://img.shields.io/github/v/release/hvh-software/OpenSteamEYA?style=flat-square&label=latest%20version&labelColor=black
[latest-version-link]: https://github.com/hvh-software/OpenSteamEYA/releases
[github-downloads-shield]: https://img.shields.io/github/downloads/hvh-software/OpenSteamEYA/total?style=flat-square&logo=github&label=downloads&labelColor=black
[github-downloads-link]: https://github.com/hvh-software/OpenSteamEYA/releases
[github-stars-shield]: https://img.shields.io/github/stars/hvh-software/OpenSteamEYA?style=flat-square&logo=github&labelColor=black
[github-stars-link]: https://github.com/hvh-software/OpenSteamEYA/stargazers
[github-license-shield]: https://img.shields.io/github/license/hvh-software/OpenSteamEYA?style=flat-square&logo=github&labelColor=black
[github-license-link]: https://github.com/hvh-software/OpenSteamEYA/blob/main/LICENSE
