# Aether Desktop

Windows 11 桌面美化：顶部**灵动岛**、底部 **Dock + 应用抽屉**、可自由摆放的**桌面卡片**，配色跟着壁纸走。轻量（空闲约 70 MB 内存、整机 CPU 0.2% 左右），全屏游戏时自动全部让路。

## 安装

1. 到 [Releases](../../releases/latest) 下载 `Aether-x.y.z.zip`，解压到任意位置
2. 双击 `Aether.exe` → 点「安装」（装到你的用户目录，不需要管理员权限）
   - 可以选是否用 Dock 代替 Windows 任务栏，之后随时在设置里改
   - 也可以点「先不装，直接试用」
3. 如果提示缺少 .NET，按弹窗里的链接装 **.NET 10 Desktop Runtime（x64）**，再打开一次

**更新**：装好后会自动检查新版本（设置 › 关于 › 更新），点「更新并重启」即可，设置和数据都保留。
**卸载**：Windows 设置 › 应用，或开始菜单「卸载 Aether Desktop」，或 Aether 设置 › 关于 › 卸载。卸载会先把任务栏恢复原样。
**任务栏找不回来了？** 开始菜单 › Aether Desktop › 「Aether 恢复任务栏（应急）」。

## 功能

**灵动岛**（顶部居中，可左右拖动）
- 收起时显示时间 / 正在播放的歌词 / 未读来源 / CPU、内存、电量
- 展开多页：音乐（进度、频谱、歌词）、系统、快捷控制（WiFi、蓝牙、托盘）、计时与闹钟、通知、置物架、AI 额度、邮件
- 微信 / QQ 等任务栏闪烁 → 显示来源图标，点一下切回去（不读取聊天内容）
- Windows 通知上岛（过滤 Windows 自己的系统提示），可复制，自动识别验证码
- **置物架**：把文件拖到岛上「吸入」，之后随时拖出来；`Win+Shift+S` 截图自动放进去，能直接拖进微信
- **剪贴板历史**：最近 30 条 + 固定，跳过密码管理器标记的内容
- **邮件验证码**：连接 QQ / 163 / 126 / Gmail 等邮箱（IMAP 只读、授权码），验证码大字弹出、点一下复制、到时自动隐藏
- **AI 额度**：读取本机 Codex / Claude 登录，显示 5 小时和每周用量
- 音量、亮度、切歌、充电提示；窗口最大化可缩成细线；全屏游戏完全隐藏

**Dock 与应用抽屉**
- 固定应用、正在运行的窗口、智能隐藏；图标大小和外框大小分开调，也可以完全去掉外框
- 拖动排序，从抽屉或文件夹拖进来固定
- 抽屉按类别自动分组，拼音首字母搜索；分组可改名、删除，拖应用到分组标签换组

**桌面卡片**：时钟（多种样式）、日历、天气、音乐、便签 / 待办、音量混音、快速访问，全部自由拖动

**配色**：从桌面壁纸取色（支持 Wallpaper Engine），手动 / 自动跟随 / 固定颜色三种

## 隐私

所有数据都只在本机，存在安装目录的 `Data` 文件夹里：
- 通知只读本机 Windows 通知数据库；邮箱授权码用 Windows DPAPI 加密保存
- AI 额度只把你本机已有的登录发给对应官方接口（chatgpt.com / api.anthropic.com）
- 天气用 Open-Meteo，歌词用 LRCLIB，更新检查用 GitHub API；没有任何统计或上报

## 自己编译

需要 .NET 10 SDK。

```powershell
.\build.ps1             # 编译到 src\Aether\bin\Release
.\build.ps1 -Install    # 安装到 %LOCALAPPDATA%\Programs\Aether Desktop 并启动（保留 Data）
.\build.ps1 -Release -Repo owner/name -Version 1.2.3   # 生成 dist\Aether-1.2.3.zip，用于发布
```

调试用环境变量：`AETHER_DIAG=1`（性能计数写入 error.log）、`AETHER_DISABLE=island,dock,clock`、`AETHER_GPU=1`（硬件渲染）、`AETHER_INSTALL_DIR`（安装测试模式）。

| 目录 | 内容 |
|---|---|
| `Core/` | 路径、日志、设置、安装 / 卸载、更新、开机自启 |
| `Theme/` | 取色与共享画刷 |
| `Shell/` | 桌面窗口基类、DWM 效果、任务栏、托盘、游戏模式 |
| `Island/` | 灵动岛及其服务（媒体、通知、未读、置物架、剪贴板、额度、邮件…） |
| `Dock/` | Dock、应用抽屉 |
| `Apps/` | 应用目录、图标缓存、自动分类、拼音 |
| `Widgets/` | 桌面卡片 |
| `Settings/` | 设置窗口 |

## 致谢

灵动岛的部分交互参考了 [岛仔 xiaotang-daozai](https://github.com/Renly1994/xiaotang-daozai)（MIT）、[BrimDeck](https://github.com/MoFeng2223/BrimDeck)（Apache-2.0）和 [fly-island](https://github.com/flyl1u/fly-island)（MIT）的行为设计，验证码识别的打分思路来自岛仔；代码为独立编写。

## 许可

[MIT](LICENSE)
