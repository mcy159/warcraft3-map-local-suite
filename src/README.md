# src — 联机优化补丁源码

仓库其余部分是**可部署成品**（`bin\` `plugin\` `version127a\` `tools\` `war3.bat`），本目录是它们的**源码与构建脚本**。

## 目录

| 文件 | 说明 |
|---|---|
| `keybind_panel_v3_现役面板.cs` 112,012 B | **当前 `tools\keybind_panel.exe` 的源码**（F7 面板：技能 12 格 3×4 + 物品 6 格、血条、0/. 视距） |
| `keybind_panel_F7版.cs` 94,578 B | F7 面板首个可用版（`PanelHotkeyVk = 0x76`） |
| `keybind.cs` 53,168 B | 纯改键版（无面板，游戏外 ini 直配 + 热重载） |
| `keybind_build.cmd` | C# 编译命令（csc 参数） |
| `keybind_template_v3.ini` | 面板/改键配置模板（`[Mappings]` `[SlotMap]` `[panel]`） |
| `pack_exe.py` 9,730 B | **把 `bin\`+`plugin\`+`tools\` 打包成单个 exe 的脚本**（stub + zip 拼接 + EOCD 绝对化） |
| `core2_new_stub.exe` 14,336 B | 上述打包脚本用的引导 stub |
| `重建说明.md` | 从源码到 exe 的完整步骤、校验项、回滚方式 |
| `历史\` | F8 版面板 / QWER 初代 / 前代 pack_exe |

## 编译产物

`keybind_panel.exe` 59,904 B，放在 `tools\`，由 `war3.bat` 自动拉起。

## 重建

见 [`重建说明.md`](重建说明.md)。

## 历史

- `8761d04` sync 使用说明.txt (keybind QWER 改键内容)
- `9a8fcab` sync: F7 面板版（keybind_panel 替代 SuWar3Tools/WarKey2017b）
- `9a3122e` build: add .gitattributes (* -text) 固定行尾
