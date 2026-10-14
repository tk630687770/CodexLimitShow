# 订阅网页核验独立验证工具

仅验证 WebView2 的正常登录、会话持久化和账单页面可见订阅文字是否可读。尚未接入组件，也不判断账号一致性，不保存日期或写入任何账号快照。

```powershell
dotnet build tools/SubscriptionBrowserProbe/SubscriptionBrowserProbe.csproj -c Release
dotnet run --project tools/SubscriptionBrowserProbe/SubscriptionBrowserProbe.csproj -c Release --no-build -- --self-test
dotnet run --project tools/SubscriptionBrowserProbe/SubscriptionBrowserProbe.csproj -c Release --no-build
```

自行完成登录和网站验证，然后打开账单页并点击“读取订阅文字”。重开工具可验证登录是否仍可复用。浏览器数据固定保存在 `%LOCALAPPDATA%\CodexLimitShow\SubscriptionBrowserProbe`，不随重启或升级清除；退出登录、网站过期或清除此目录会使会话失效，不能承诺固定有效天数。

不读取 Codex 凭据或 Edge 用户资料，不复制／导出 Cookie、令牌，不访问私有订阅 API，不保存密码、页面正文或诊断文件。读取动作只允许 `https://chatgpt.com/settings/billing` 的可见套餐说明段落，拒绝邮箱、多个不一致结果或没有日期的内容。下载和额外网站权限请求均禁用。

仅使用微软官方 WebView2 包和本机 WebView2 Runtime；不会自动安装 Runtime。嵌入浏览器可能仍被网站限制。登录、验证码或安全提示由用户本人处理；不要在聊天中发送密码、验证码或令牌。程序不能自动阻止用户在完整网页里更改订阅或付款设置，请勿在本次只读验证中操作这些设置。

当前不支持所有网页语言／布局，只读验证成功后再设计账号核对与正式接入。不得把此工具当正式发布物上传 GitHub Release。
