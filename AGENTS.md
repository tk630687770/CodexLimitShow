# CodexLimitShow 发布规则

- 从 2.0.0 起，日常使用和 GitHub Release 均为单个 `CodexLimitShow.exe`；可放在任意当前用户有写权限的目录。不要求 `正式版/`、`formal.marker`、`release.json` 或本地版本库。运行数据及账号快照仍保存在用户数据目录，不随程序升级删除。
- 新发布只通过 `tools/Publish-CodexLimitShow.ps1` 生成 `release/CodexLimitShow.exe`。这是待上传候选文件，不是日常运行入口；脚本只允许版本递增，不覆盖当前正在使用的程序。旧 `release/versions/`、`release/packages/`、`正式版/` 和 `dist/` 均为历史内容，保留但不参与新版升级。
- `.csproj` 的 Version、InformationalVersion、AssemblyVersion、FileVersion、EXE 元数据及 GitHub 标签 `v<版本>` 必须一致。修复递增补丁版本，新功能递增次版本，破坏性变更递增主版本；不得重复发布同一版本。
- 升级按钮不依赖安装目录名或标记文件。每 24 小时仅查询公开 GitHub 仓库 `tk630687770/CodexLimitShow` 的最新正式 Release 元数据，持久化上次尝试时间与 ETag；重启不重复检查，失败不高频重试。点击升级可立即检查；仅用户确认后下载。核对 Release 标签、资产名称与 URL、GitHub 资产 SHA-256、EXE 产品及文件版本。旧程序退出后从临时文件原位替换并重启；失败时尽量保留旧 EXE。不碰 Codex 安装、凭据或账号快照。
- GitHub Release 只上传发布脚本生成并验证的 `release/CodexLimitShow.exe`，标签 `v<版本>`，设为最新正式版；不上传草稿、预发布版或未验证构建。Git 管源码，GitHub Releases 管可下载版本。不要主动推送；得到用户明确授权后再推送和发布。
- 1.x ZIP 升级器不认识 2.x EXE 资产；旧用户需手动从 GitHub 下载 2.x `CodexLimitShow.exe` 一次。旧版本与发布物不主动删除。
