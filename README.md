# 番茄钟 TomatoPomo 🍅

B站白噪音番茄钟桌面小插件。专注学习/工作时,随机播放你 B 站收藏夹或自选列表里的白噪音视频,时间一到自动停止并记录今日番茄数。

![language](https://img.shields.io/badge/language-C%20%23-WinForms-blue) ![platform](https://img.shields.io/badge/platform-Windows%2010%2F11-lightgrey) ![size](https://img.shields.io/badge/exe~336KB-green)

## 功能

- ⏱ **番茄计时**:专注/休息时长自定(默认 25/5 分钟),自动循环
- 🎵 **B站音源**:从本地播放列表随机(或顺序)播放,避免连续重复
- 📃 **播放列表管理**:添加(BV号/链接/b23短链)、删除、排序、批量编辑
- 📥 **收藏夹导入**:一键导入B站收藏夹(含标题/时长,自动过滤失效视频);私有收藏夹支持粘贴 Cookie 导入
- 🤫 **静默后台播放**:播放器自动最小化到任务栏,不弹窗不抢焦点
- 🔀 **阶段切换模式**:默认到点停止音乐并自动进入下一阶段;可选「音乐连续播放,下阶段开始时换下一支」
- 📊 **每日统计**:今日番茄数、累计总数、近 7 天记录,本地持久化
- 🖥 **桌面小窗**:置顶可拖动小部件,关闭即缩到托盘

## 下载

到 [Releases](../../releases) 页面下载 `番茄钟.exe`,绿色单文件,免安装,双击即用。

> 首次运行如遇 Windows SmartScreen 提示,点「更多信息 → 仍要运行」(本程序无代码签名)。

## 从源码构建

任何 Windows 10/11 机器**无需安装任何东西**(使用系统自带的 .NET Framework 编译器):

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

产物:`番茄钟.exe`。

## 技术说明

- 单文件 C# WinForms(`Program.cs`),由 Windows 内置 `csc.exe` 编译,零第三方依赖
- B站接口使用 wbi 签名访问公开收藏夹;视频通过 Edge/Chrome `--app` 模式独立窗口播放,计时结束直接结束该进程实现"到点即停"
- 高 DPI 适配(4K/150% 缩放验证过),配置与统计存于 `%APPDATA%\TomatoPomo\`

## License

MIT
