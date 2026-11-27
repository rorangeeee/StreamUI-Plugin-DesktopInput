# Desktop Input

Windows 原生键鼠输入插件。优先使用 Raw Input 读取物理键位、鼠标五个按键、滚轮上下和相对移动量。Windows 低级键鼠钩子补充全屏等场景下 Raw Input 漏掉的事件，钩子会把事件继续传给系统。数据通过 Core 的本地二进制通道发送。插件不转换输入文本、不记录按键历史，也不写入采集数据。

## 构建和启用

需要 Windows x64、.NET 10 SDK 和 StreamUI Core Native SDK 1。把源码放在 Core 的 `plugins/when.desktop-input`（或开发目录 `plugins/desktop-input`）内，并在该目录执行构建命令。

```powershell
powershell.exe -NoProfile -File build.ps1 -SdkPath ../../sdk/native
```

启动 Core，在「设置 → 插件设置」启用「键盘与鼠标输入」。LATENITE 的显示设置位于两个主场景各自的配置页。更新插件前退出 Core，构建后重新启动。

运行 `dotnet run --project tests/StateTests.csproj -c Release` 检查按键状态、滚轮脉冲和帧编码。

## 二进制帧 v1

订阅 `client.binary('when.desktop-input', 'frames', callback)`。每帧 75 字节，按键使用当前状态快照。变化时最多约每秒发送 60 帧，稳定时每约 200 毫秒发送一次。按住时相应位持续置 1。供显示使用的位图为短按、Pause 和滚轮保留约 80 毫秒的亮起时间。

| 偏移 | 数据 |
| --- | --- |
| 0 | 版本号 1 |
| 1 | 鼠标当前按住位图 |
| 2 | 鼠标显示位图（含滚轮） |
| 3–34 | 键盘当前按住位图 |
| 35–66 | 键盘显示位图 |
| 67–74 | `float32` 小端 X、Y 速度，向右和向下为正。Raw Input 相对移动为原始计数每秒，绝对定位和钩子补充的光标移动为像素每秒 |

鼠标位图从低位到高位依次是左、右、中、侧键 1、侧键 2、滚轮上、滚轮下。键盘使用 Windows Scan Code Set 1，`E0` 前缀映射到编号的第 7 位，Pause 使用 `0xC5`。编号 `n` 对应位图的第 `n >> 3` 个字节内的第 `n & 7` 位。物理位置与系统当前文字布局独立。[Windows Raw Input](https://learn.microsoft.com/en-us/windows/win32/inputdev/raw-input) · [Windows 扫描码表](https://learn.microsoft.com/en-us/windows/win32/inputdev/about-keyboard-input#scan-codes)
