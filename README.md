# CodexLimitShow

Windows 10/11 桌面悬浮组件：本机单进程 WPF 界面，通过 Codex CLI 的 `app-server --stdio` 显示配额。

## 使用

- 折叠态为双圈：内圈 5 小时，外圈为周额度（无周额度时显示月额度），中心分别显示两项剩余百分比；顶部套餐字样采用浅紫色玻璃内雕效果。无活动窗口显示空轨道和“—”，不推断为 100%。
- 单击双圈展开详情；双击强制核对账号并刷新额度。展开后点击左上角的 ‹ 或组件外收起，返回展开前的双圈或停靠条及原位置。
- 拖拽双圈，圆心越过屏幕边缘时预览平直玻璃条；松手停靠。上下边为三行横条（套餐、5h、7d／月），左右边为窄竖条（套餐在上、7d／月靠屏幕边缘、5h 靠内侧），均显示独立进度和剩余百分比。展开面板替换折叠视图，历史快照在面板侧边显示。
- 详情标题栏或托盘菜单可打开 Codex Desktop；详情内还可固定位置、打开只读历史、缩至托盘、退出。额度每 30 秒读取一次；账号资料仅首次、登录文件变化及强制核对时读取；token 活动统计仅启动或跨日读取。
- 订阅日期仅首次识别／账号变化、用户点“手动刷新有效期”、已存日期与当前套餐矛盾时联网核验。数据源是非公开的 ChatGPT 接口，可能返回 403；失败时保留旧日期并显示核验错误，不把日期经过解释为服务不可用。
- “使用重置”要求当前账号、新鲜额度、可用机会和用户二次确认。调用官方 App Server `account/rateLimitResetCredit/consume`，只传一次性幂等键，不传 credit ID。结果不确定时本次运行不再发起新的消费。**构建和验证期间没有实际消费机会。**
- 只有从 `正式版/` 启动且正式版标记与程序版本一致时，详情顶部才显示“升级”。点击后比较本地 `release/versions/` 与公开 GitHub 最新正式 Release；用户确认后才下载、校验 SHA-256、替换并重启。日常额度刷新不会检查或下载更新。

## 隐私与限制

只在订阅有效期核验时短暂读取本机 `.codex/auth.json` 中当前账号的令牌，并仅向 `chatgpt.com` 请求账号绑定的订阅信息。令牌、cookie、完整内部账号 ID、credit ID 和原始服务端 JSON 都不会写入日志或历史。历史快照保存用户要求显示的完整邮箱、套餐、筛选后的配额和订阅日期，路径为 `%LOCALAPPDATA%\CodexLimitShow\snapshots.json`。不要把该文件或 `backups` 目录公开分发。

没有主动切号、后台服务、数据库或开机自启。双圈、停靠玻璃条使用 WPF 透明窗口与矢量绘制，不依赖实时桌面模糊。订阅接口并非公开稳定 API，不保证所有账号或网络环境可用。

## 构建与验证

需要 .NET 9 SDK：

```powershell
dotnet build
dotnet run -- --self-test
dotnet run -- --probe
dotnet run -- --probe-subscription
dotnet run -- --render-preview
```

`--probe` 仅输出配额百分比和机会数量；`--probe-subscription` 仅输出本地时间日期或安全错误类型；`--render-preview` 生成玻璃双圈、停靠玻璃条、面板预览 PNG。它们均不消费重置机会。

发布自包含单文件（项目根目录执行；首次建立正式版才加 `-InstallFormal`）：

```powershell
.\tools\Publish-CodexLimitShow.ps1 -InstallFormal
```

后续先递增 `.csproj` 版本号，再运行同一脚本（不带 `-InstallFormal`）；它写入 `release/versions/` 和 `release/packages/`，正式版用户再点“升级”。版本目录名、程序版本及校验清单必须一致；旧版本不覆盖，供回退。既有 `dist/` 仅作为历史包保留，不参与升级。目标电脑不必单独安装 .NET，但需安装并登录 Codex Desktop。

其他电脑首次安装：从 [GitHub Releases](https://github.com/tk630687770/CodexLimitShow/releases) 下载最新 `CodexLimitShow-<版本>-win-x64.zip`，解压后把内层 `CodexLimitShow-<版本>-win-x64` 文件夹改名为 `正式版`，运行其中的 `CodexLimitShow.exe`。以后点击面板中的“升级”即可远程更新；账户快照保存在本机，不随升级包传输。公开发布不要求其他电脑登录 GitHub。

## 源码与许可证

- `WpfMain.cs`、`GlassWidget.cs`、`RingGeometry.cs`：入口、玻璃界面和双圈几何
- `CodexAppServerClient.cs`：本地 App Server 客户端、数据筛选解析及重置调用
- `SubscriptionLookup.cs`：隔离的非公开订阅日期只读查询
- `SnapshotStore.cs`：各账号最后一次成功快照
- 旧 WinForms 实现仅在本机备份中保留，不参与编译或公开仓库

项目代码继续采用 MIT。未复制 Cockpit Tools 或其他第三方项目源码与素材；仅参考公开的接口调用思路。Cockpit Tools 的 CC BY-NC-SA 许可不会因本项目的独立实现而进入此代码库。
