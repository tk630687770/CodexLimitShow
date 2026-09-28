# CodexLimitShow 发布规则

- `release/versions/CodexLimitShow-<版本>-win-x64/` 保存不可覆盖的本地版本；`release/packages/` 保存对应 ZIP。新发布只通过 `tools/Publish-CodexLimitShow.ps1`，版本号必须递增，旧版本保留供回退。
- 根目录 `正式版/` 是日常使用的固定入口。首次安装用发布脚本的 `-InstallFormal`；之后只能从正式版界面点击“升级”，不得在发布时覆盖正式版。
- `.csproj` 的 Version、InformationalVersion、AssemblyVersion、FileVersion、发布目录名、`release.json` 和升级比较版本必须一致。修复递增补丁版本，新功能递增次版本，破坏性变更递增主版本。
- 只有路径和 `formal.marker` 均核验通过的正式版显示升级按钮。升级先查本项目 `release/versions/`，再查公开 GitHub 仓库 `tk630687770/CodexLimitShow` 的最新正式 Release；仅用户确认后下载。必须核对 GitHub 包的 SHA-256、目录、EXE 版本和清单 SHA-256；旧进程退出后替换并重启。不碰 Codex 安装、凭据或账号快照。
- GitHub Release 只上传由发布脚本生成的对应版本 ZIP，标签 `v<版本>`，设为最新正式版；不要上传未验证的构建结果、草稿或预发布版作为升级源。其他电脑从 ZIP 解压到名为 `正式版` 的目录即可首次安装。
- 旧 `dist/` 是规则建立前的历史发布物，暂时保留，但不参与升级扫描；不得再向其中发布新版。不要主动推送或删除旧版本。
