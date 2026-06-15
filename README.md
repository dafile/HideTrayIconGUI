# HideTrayIcon GUI / 系统托盘图标隐藏管理工具
<img width="841" height="608" alt="image" src="https://github.com/user-attachments/assets/f1d44f6e-d2e2-4cad-aabb-d244e479ef92" />
<img width="272" height="100" alt="image" src="https://github.com/user-attachments/assets/21ec4d4b-2091-4baf-beab-eb6cc079befe" />

[中文](#中文说明) | [English](#english)

---

## 中文说明

基于 [hideTrayIcon.exe](https://github.com/LCiZY/HideTrayIcon) 的图形化管理界面，用于隐藏/显示 Windows 系统托盘图标。

### 功能

- **托盘图标管理** - 以表格形式展示所有系统托盘图标（进程名、提示文本、区域、状态）
- **隐藏/显示操作** - 选中图标一键隐藏或显示，支持批量操作
- **自定义规则** - 隐藏时自动保存规则，程序重启后自动应用
- **隐藏列表管理** - 独立窗口查看和管理已隐藏的规则
- **定时自动隐藏** - 可配置间隔的后台轮询，自动隐藏符合条件的图标
- **进程过滤** - 默认过滤 Taskmgr、Idle 等系统进程，可在设置中自定义
- **状态筛选** - 按全部/可见/已隐藏筛选显示
- **右键菜单** - 隐藏此项、移除此项、复制进程名/提示文本
- **重启资源管理器** - 一键恢复所有已隐藏的托盘图标
- **托盘图标** - 最小化到系统托盘，右键菜单快速操作
- **关闭行为配置** - 可选关闭时最小化到托盘或直接退出

### 使用方法

1. 下载 `release` 目录中的最新版本
2. 将整个文件夹复制到任意位置
3. 运行 `HideTrayIconGUI.exe`
4. 勾选要隐藏的图标，点击"隐藏选中"
5. 规则自动保存，重启程序后自动生效

### 文件说明

| 文件 | 说明 |
|------|------|
| `HideTrayIconGUI.exe` | 主程序 |
| `hideTrayIcon.exe` | 隐藏引擎（来自 LCiZY/HideTrayIcon） |
| `rules.txt` | 隐藏规则（格式：进程名 或 进程名\|提示文本） |
| `filter.txt` | 过滤进程列表（不显示在主列表中） |
| `config.txt` | 程序配置 |
| `logs/` | 日志目录 |

### 系统要求

- Windows 10/11 64位
- .NET 8.0 Runtime（如使用非独立发布版本）

---

## English

A graphical management interface for [hideTrayIcon.exe](https://github.com/LCiZY/HideTrayIcon) to hide/show Windows system tray icons.

### Features

- **Tray Icon Management** - Display all system tray icons in a table (process name, tooltip, area, status)
- **Hide/Show** - One-click hide or show for selected icons, batch operations supported
- **Custom Rules** - Auto-save rules on hide, auto-apply on startup
- **Hidden List Manager** - Separate window to view and manage hidden rules
- **Auto Hide** - Configurable interval background polling
- **Process Filter** - Filter out system processes like Taskmgr, Idle
- **Status Filter** - Filter by All / Visible / Hidden
- **Context Menu** - Hide, remove, copy process name/tooltip
- **Restart Explorer** - One-click restore all hidden tray icons
- **System Tray** - Minimize to tray with right-click menu
- **Close Behavior** - Configurable: minimize to tray or exit

### Usage

1. Download the latest release from the `release` directory
2. Copy the entire folder to any location
3. Run `HideTrayIconGUI.exe`
4. Check the icons you want to hide, click "隐藏选中" (Hide Selected)
5. Rules are saved automatically and applied on restart

### System Requirements

- Windows 10/11 64-bit
- .NET 8.0 Runtime (for non-self-contained version)

### Credits

- [LCiZY/HideTrayIcon](https://github.com/LCiZY/HideTrayIcon) - Core hide/show engine
