# 软件授权（License）

本分支为 MFAAvalonia 增加离线软件授权。保留现有 Avalonia、SukiUI 和 CommunityToolkit.Mvvm 架构；授权服务独立于 UI，启动入口、公共任务执行器和全局启动入口共享同一授权状态。

## 用户使用

首次启动时只显示授权界面，不创建主工作区、不启动自动任务或全局快捷键。页面提供本机试用码、机器码、激活码输入、复制按钮、授权状态、剩余时间和到期时间。

- **试用**：点击“填入试用码”，再点击“验证激活”。只有验证成功且本地状态保存成功后，才从该时刻开始连续计时 **2 小时**。只打开页面、复制试用码或输入错误激活码都不会开始计时。退出软件后时间继续流逝；重启或重复激活不会延长已经记录的试用。
- **正式授权**：把当前授权窗口的机器码交给作者，把作者返回的完整激活码粘贴到输入框，再点击“验证激活”。正式授权绑定机器码和到期时间。
- **续期**：在“设置 → 软件授权”中输入新的正式激活码。错误的续期输入不会覆盖已有有效授权。已经运行到期的会话会锁定并退出，需要重新打开软件再激活。

启动时会重新验证保存的授权，不能仅靠上次成功标记进入软件。运行期每秒检查一次授权；失效后拒绝新的受控任务入口，关闭快捷键和托盘入口，尝试停止原生任务、截图与 Agent，再退出。停止流程有超时，另设 15 秒进程退出兜底，避免 UI 或原生调用阻塞后无限运行。已经启动的外部第三方应用不属于授权组件控制的进程，不会任意终止这些应用。

## 作者签发正式授权

### 私钥保管

客户端只包含 `MFAAvalonia/Helper/Licensing/LicensePublicKey.cs` 中的 RSA 公钥。对应的作者私钥应单独离线保管，**不得提交 Git、放入应用资源、输出到日志、上传 CI 制品或随客户端发布**。本次配置的公钥对应交付给仓库作者的私钥文件，签发时使用该私钥即可，不要为每位用户重新生成密钥对。

当前公钥的 SHA-256 指纹（DER SubjectPublicKeyInfo）：

```text
98da293a1760085ef198f807214bb88bdc3c3eef0f321684f3bcc0a42b482022
```

私钥文件采用未加密 PKCS#8 PEM；请保存在仅作者可读的目录及加密备份中。Unix 创建的私钥文件权限为 `0600`；Windows 上应由作者设置合适的目录访问权限。

### 签发命令

在仓库根目录使用 .NET 10 SDK，替换下面的私钥路径、机器码和到期时间：

```sh
dotnet run --project tools/MFAAvalonia.LicenseIssuer -- --gen-key "/secure/MFAAvalonia-author-private.pem" "9DCDFDCB9D96EEAB" "2027-12-31T23:59:59+08:00"
```

到期时间必须显式包含时区，例如 `2027-12-31T23:59:59+08:00` 或 `2027-12-31T15:59:59Z`。不接受含糊的无时区时间，也不签发已经过期的授权。标准输出只有激活码；设备和 UTC 到期时间摘要写入标准错误。把完整的一行 `MFA1...` 激活码发送给用户，而不是发送私钥。

也可以先编译签发工具：

```sh
dotnet build tools/MFAAvalonia.LicenseIssuer --configuration Release
```

签发工具与客户端项目分离，不依赖 Avalonia，也不会被客户端的项目引用自动打包。

### 更换作者密钥

仅在初次部署自己的信任根、私钥遗失或需要轮换时生成新的密钥对：

```sh
dotnet run --project tools/MFAAvalonia.LicenseIssuer -- --keygen "/secure/new-author-private.pem" "/secure/new-author-public.pem"
```

命令生成 3072 位 RSA 密钥，不覆盖已有文件。将新公钥内容替换到 `LicensePublicKey.Pem` 后重新构建客户端；**当前实现只信任一个公钥，替换公钥后旧公钥签发的正式授权不再有效，需要重新签发**。仅生成新密钥但不更换客户端公钥，签出的授权不能通过验证。

查询某个机器码对应的新格式试用 UUID：

```sh
dotnet run --project tools/MFAAvalonia.LicenseIssuer -- --trial-code "9DCDFDCB9D96EEAB"
```

## 格式与时间语义

正式授权格式：

```text
MFA1.base64url(payload).base64url(signature)
payload = UTF8("MFAAvalonia|机器码|UTC Unix 秒到期时间")
signature = RSA-PSS / SHA-256(payload)
```

验证包含版本、产品标识、机器码、签名、输入大小和到期时间检查。到期瞬间即失效（`expires <= now`），UI 将 UTC 到期时间转换为本地时间显示，并显示时区偏移。

机器码优先使用 Windows MachineGuid、Linux machine-id、macOS IOPlatformUUID，再退回稳定排序的物理网卡地址；均不可用时使用持久化安装标识。哈希结果为 16 位大写十六进制字符串。试用 UUID 由机器码和固定版本盐派生，采用与 Python `uuid.UUID(bytes=...)` 一致的网络字节序。系统重装、平台标识改变或回退标识变化可能改变机器码，需要重新签发授权。

## 本地存储与故障处理

授权数据位于 `.NET Environment.SpecialFolder.LocalApplicationData` 返回目录下的 `MFAAvalonia/Licensing`，不放在工作目录或用户可切换的任务配置中。Windows 通常为 `%LOCALAPPDATA%\MFAAvalonia\Licensing`。

文件包括 `license-state.json`（授权与试用起点）、`state-auth.key`（本地状态认证密钥）及 `state.lock`（进程间锁）；仅在硬件/系统标识均不可用的回退场景另有 `installation-id`。

状态使用 HMAC 完整性校验、临时文件写入后原子替换和进程间锁。Windows 使用当前用户 DPAPI 保护本地认证密钥；Unix 使用用户私有目录和文件权限。这里的本地状态密钥不是作者签发私钥。

授权文件损坏、记录缺少试用起点、只删除部分文件、持久化失败等情况按失败处理，不以“修复”为由重新开始试用。请恢复一致的原始授权数据并检查目录权限；没有自动重置试用的恢复按钮。已经发现有效记录的运行进程也会拒绝随后消失的状态。

时间使用 UTC、单调计时和持久化的最后已知时间组合。运行中小幅调整时钟不会延长试用；明显时钟回退会锁定。持久化时间每 30 秒检查更新，允许 2 分钟时钟校正容差。请校正系统时间后重新启动，不能把此措施当成可信在线授时。

## 与提供的 Python 示例的迁移差异

本实现参考示例的交互和使用约束，但不是旧授权文件的二进制兼容移植：

1. 示例说明写“试用 2 小时”，而 `TRIAL_DURATION = 3` 实际为 3 秒。本实现遵循说明，使用 `TimeSpan.FromHours(2)`。
2. 示例正式码使用客户端也持有的 HMAC 共享密钥。本实现改为作者私钥签发、客户端公钥验证，避免把正式授权签发秘密放进客户端。
3. 新机器码的来源、试用 UUID 的版本盐、正式码格式及状态目录均有改变。**旧 Python 的机器码、UUID、正式激活码和本地试用文件不会自动导入**。迁移时读取新窗口中的机器码，由作者重新签发；不要把旧授权文件复制到新目录。
4. 示例到期后主要更新 UI。本实现同时关闭软件会话和受控任务入口，不能仅关闭授权窗口继续使用。

## 安全边界

这是离线授权机制，而不是授权服务器。签名能验证作者签发的正式码是否被修改，却不能阻止拥有本机控制权的人修改开源代码、替换二进制、恢复磁盘快照、删除整套状态或更换系统账户。试用历史按本地用户数据保存，不能保证跨系统账户、重装、完整状态删除后的“物理机器终身只试用一次”。仅本地保存的 HMAC 密钥在有权限访问该用户数据的攻击者面前也不是秘密。

目前没有在线撤销、中央设备注册、并发席位限制或可信网络授时。需要严格的一机一次试用、远程封禁或防止离线时间重放时，应由独立授权服务保存设备和授权状态，并使用有时效的签名租约。当前实现不作无法兑现的防破解承诺。

## 架构与验证

| 位置 | 职责 |
| --- | --- |
| `Helper/Licensing/LicenseService.cs` | 与 UI 无关的激活、试用、正式码、到期和时钟策略 |
| `Helper/Licensing/LicenseToken.cs` | 版本化令牌编码、公钥验签与固定试用 UUID |
| `Helper/Licensing/FileLicenseStore.cs` | 原子持久化、完整性校验与多进程互斥 |
| `Helper/Licensing/LicenseRuntime.cs` | 共享执行检查、后台检查与会话锁定 |
| `App.Licensing.cs` / `App.axaml.cs` | 授权前阻止主工作区启动，到期停止与退出 |
| `Helper/TaskManager.cs` / `Extensions/GlobalStartManager.cs` | 在执行边界重新验证，覆盖排队后的任务 |
| `ViewModels/Windows/LicenseViewModel.cs` | MVVM 激活命令、提示及倒计时 |
| `Views/UserControls/LicensePanel.axaml` | 启动页与设置页复用的主题化授权面板 |
| `tools/MFAAvalonia.LicenseIssuer` | 作者端独立签发命令行 |

回归测试直接链接生产授权源文件，测试用签发私钥在内存中临时生成，不使用作者私钥。运行：

```sh
dotnet run --project tests/MFAAvalonia.Licensing.Tests --configuration Release
```

测试项目是返回非零退出码表示失败的独立控制台测试器，使用 `dotnet run`，不是 `dotnet test`。27 个用例涵盖试用起点、重启、精确到期边界、错误输入、错误设备和签发者、篡改、续期、存储故障、时钟回退、原子文件读写和并发激活。

`.github/workflows/licensing.yml` 在 Windows、Linux、macOS 运行上述测试，构建签发工具，并构建完整桌面程序及编译 XAML。构建通过不等于所有设备行为都已验收；发布前仍应在实际桌面环境验证复制、主题、窗口切换、多个运行实例和真实原生任务到期停止。共享单视图入口已接入授权，但 Android 设备行为不在上述桌面 CI 覆盖范围内。
