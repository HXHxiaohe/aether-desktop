# 交接说明（给接手的开发者 / Codex）

Aether Desktop：WPF + .NET 10，框架依赖，**默认软件渲染**（不唤醒笔记本独显）。源码在 `src/Aether`，结构见 README。

## 必读约定
- **改完一律** `.\build.ps1 -Install`（装到 `%LOCALAPPDATA%\Programs\Aether Desktop`，保留 `Data`）。不要直接跑 bin 里的开发版（单实例互斥）。
- 退出 Aether 用事件 `Local\Aether.Desktop.v1.Quit`（会恢复任务栏），别直接 kill。
- 截图测试只抓 Aether 自己的窗口（PrintWindow），**不要全屏截图**（会截到用户聊天）。
- 发版：先提交，再 `.\publish.ps1 -Version x.y.z -Notes "改了什么"`。它把源码同步到 `..\Aether-public`（公开仓库 HXHxiaohe/aether-desktop，作者用 noreply 邮箱），推送，编译带更新地址的 zip，创建 GitHub Release。本地 `Aether` 仓库的历史含个人路径，**不要推送它**。
- PowerShell 5.1 脚本里有中文时要存成 **UTF-8 带 BOM**。
- 性能底线：空闲整机 CPU ≈0.2%、私有内存 ≈70 MB。新动画只在需要时跑计时器，结束就停。

## 流光（1.0.1 新增）
- `Island/IslandGlow.cs`：沿灵动岛真实轮廓（`IslandSurface.Outline`）等距采样，参数 t∈[0,1]：胶囊从顶部正中顺时针一圈；刘海从右耳绕底部到左耳（顶边是屏幕边，不发光）。
  - 三遍绘制：宽光晕 / 中光 / 细亮芯（`PassWidth`、`PassAlpha`）。
  - 均匀部分 `Base()` 整圈一次 `DrawGeometry`；移动光带 `Band(t, ahead, behind, amp)` 按段画。
  - 持续样式：scan 扫描光带、comet 彗星掠影、dual 双子流光、pulse 声波脉冲（读频谱 `_levels`）、breathe 呼吸、steady 常亮。
  - 一次性：converge 收纳汇合（顶部→两侧→底部正中相撞闪光）、release 释放（反向）、bloom 瞬时绽放（消息）。
- `Island/IslandWindow.Glow.cs`：场景决策（放歌 / 消息 toast / 预览）、颜色（自动 = 封面色 / 微信绿 / QQ 蓝 / 邮件琥珀；主题色；彩虹；固定色）、计时器（有一次性动画 30fps，持续 20fps，静止 10fps，停了就关）。光效运行时给 `_surface` 加 `BitmapCache`（CPU 从 ~21% 降到 ~10% 单核）。
- 触发点：`OnNotification` / 未读 toast → `MarkMessageToast`；邮件验证码 → bloom；置物架拖入 / 截图 → converge；置物架拖出 → release。
- 设置：`Settings/SettingsWindow.Glow.cs`（灵动岛页「流光与动效」），字段在 `IslandSettings.Glow*`、`ReduceMotion`。
- 调试：`AETHER_GLOW_DEMO=scan|comet|…|converge` 启动后一直演示该效果；`AETHER_ISLAND_MODE=Compact|Expanded` 固定岛状态。

## 用户想要、还没做的（参考抖音「流光岛」视频）
1. **更多光效预设**，按类别分组（醒目 / 霓虹 / 自然 / 材质 / 频谱 / 经典），如棱镜折射、电弧跳跃、涡流、曲率加速、余烬升腾；带实时预览。在 `IslandGlow.OnRender` 的 `switch` 里加分支即可，参数化为主。
2. ~~窗口收纳~~ **已做（1.0.11）**：`Island/WindowCollector.cs`（MOVESIZESTART/END 钩子、PrintWindow 截图、ShowWindowAsync 隐藏/恢复、collected.json 崩溃恢复）、`Island/GenieOverlay.cs`（128 条带神灯动画）、`Island/IslandWindow.Collect.cs`（拖到岛上提示、置物架「窗口」标签、收起状态 ▣N 角标）。**对别的程序窗口只用 Async API**（同步 ShowWindow 会和目标线程互等死锁）。调试：`AETHER_COLLECT_DEMO=<窗口标题>`。还没做：「接管最小化」（最小化直接收进岛）。
3. **设置拆页**：像参考那样分「动效」（速度三档 + 收纳/释放时长显示、减少动态效果、消息轻弹）和「光效」（边缘光效 / 收纳与释放 / 颜色与触发 三个场景各自设置）。
4. **消息轻弹**：新消息时岛轻压回弹（置物架已有 gulp 动画可复用），连续消息合并。
5. **音乐封面动效**：封面周围彩色流体光环随节奏流动；切歌时封面翻牌过渡。
6. **静默渐隐**：长时间无事时岛慢慢变淡。
7. 「独立监听微信」：参考里用读本机会话库拿未读数 —— 涉及解密微信数据库，用户说以后再看，需谨慎（隐私/合规）。

## 其他已知待办
- 备份与导出（设置 / 便签 / Dock 分组 / 置物架打包成一个文件，可导入）。
- 剪贴板：可选跳过「应用专用密码」类格式（4×4 小写字母）和令牌样式文本。
