# 魔兽地图离线游玩注入包（Map Local Suite）

让部分对战平台专属地图（HM3W 加密格式，如《西方世界的劫难7》等 RPG 图）在**纯净版《魔兽争霸 III》1.27a** 上离线单机 / 局域网联机游玩——断网时也能玩平台上的游戏。

> **⚠️ 法律免责声明**
> 本项目仅供**学习研究、断网应急、个人单机游玩**使用，不用于任何商业用途，不提供任何平台服务。
> - 本项目不含任何地图内容，地图版权归原作者 / 原平台所有，请支持正版。
> - 游戏本体、地图、平台组件均为各自版权方资产，本项目仅做本地化兼容改造。
> - 使用本项目产生的任何后果由使用者自行承担。
> - 如涉及版权问题，请权利人联系删除。

---

## 一、这是什么

部分专属地图使用私有 HM3W 封装 + 扩展 JASS API，脱离原环境后在纯净版魔兽中会：
- 创建房间时弹回 / 无法创建
- 或载入时报错崩溃
- 或单位数据缺失（网易格式 SLK 引擎无法解析）

本包通过四个组件的组合解决：

| 组件 | 作用 |
|---|---|
| `00test.w3x`（用户自备/自转换） | 地图本体：还原为原版 JASS + 权威标准 SLK（引擎可直接解析，613 单位完整） |
| `Game_patched2.dll` | 地图大小限制补丁（8MB → 1GB，大图可创建） |
| `bin\` | YDWE 运行库（ydbase / YDWEConfig 等） |
| `plugin\warcraft3\` | JAPI 插件全家（dzclient_api / dz_w3_plugin / yd_lua_engine 等，模拟扩展 API） |
| `tools\keybind_panel.exe` | 改键面板（游戏内 F7 呼出：技能 12 格 3×4 / 物品 6 格点格改键、血条默认开、视距 `0` 拉远 `.` 拉近），由 `war3.bat` 自动启动 |

## 二、环境要求

- 纯净版《魔兽争霸 III》**1.27a**（1.27.0.52240）——绿色版 / 安装版均可
- Windows 7 / 10 / 11
- **重要：游戏目录必须放在浅路径**（如 `E:\War3\`、`D:\Game\Warcraft III\`），**不要放在深层路径**（如 `E:\aiwork\...\test_clean\...\`），否则启动器无法启动游戏（YDWEConfig 路径限制）

## 三、版本自动转换（deploy.bat 自动处理）

`deploy.bat` 会自动检测游戏目录下 `Game.dll` 的 MD5 并决定处理方式：

| 检测到的 Game.dll | 处理方式 |
|---|---|
| `267861A0...`（1.27a 原版） | 备份为 `Game.dll.bak` → 直接打大图补丁 |
| `F05B1A20...`（1.27a 变体） | 同上，直接打大图补丁 |
| `690A546C...`（已打补丁） | 跳过，无需重复处理 |
| 其他任意版本（1.20e ~ 1.27b） | 自动备份旧版核心文件为 `*.ver127bak` → 覆盖 `version127a\` 全套文件转换到 1.27a → 再打大图补丁 |

- 版本转换保证所有玩家的游戏版本完全一致（1.27a），这是局域网联机的前提
- 旧版本备份为 `*.ver127bak`（War3.exe / Game.dll / Storm.dll / Mss32.dll / ijl15.dll / blizzard.ax / worldedit.exe / redist\miles），需要还原时改回文件名即可；`uninstall.bat` 也会还原
- 不想转换版本？先自行把游戏升级到 1.27a 再部署

## 四、使用方法（傻瓜流程）

1. **解压**本包到任意目录（如 `E:\map_local_suite\`）
2. **准备地图**：把要玩的地图（.w3x）放进 `maps\` 目录（可多张，部署时自动复制到游戏 `Maps\Download\`）；不放地图也可，之后手动拷贝
3. **运行 `deploy.bat`**：
   - 用法一：直接把 `war3.exe` **拖到 deploy.bat 图标上**（推荐）
   - 用法二：双击 deploy.bat，按提示输入游戏目录（或自动读注册表）
   - 脚本会：校验游戏版本 → 备份原版 Game.dll → 安装大图补丁 → 复制运行库和插件 → 生成 `war3.bat` → 分发地图
4. **启动游戏**：双击游戏目录下的 **`war3.bat`**（不要直接双击 war3.exe！war3.bat 才会注入插件；它会先拉起 `tools\keybind_panel.exe` 改键面板，再启动游戏）
5. 在游戏里进入 单人游戏 → 自定义游戏 → Download → 选你的地图 → 创建游戏 → 开始

## 五、联机（局域网）

1. 所有玩家使用**完全相同**的包和**完全相同**的地图文件（地图必须逐字节一致）
2. 使用 UU 加速器 / Radmin VPN / 其他虚拟局域网工具把大家组到同一局域网
3. 房主：游戏内 局域网 → 创建游戏 → 选图
4. 其他人：局域网 → 加入游戏

## 六、卸载

双击 `uninstall.bat`：还原原版 Game.dll，删除注入文件，恢复干净。

## 七、验证

`verify.ps1`（PowerShell）：检查 6 项（Game.dll 版本 / bin 完整 / plugin 完整 / war3.bat（YDWEConfig + keybind_panel）/ `tools\keybind_panel.exe` 体积与 MD5 / Maps\Download）；另可加 `-Injector <注入器绿色包目录>` 追加校验解锁载荷：
```
powershell -ExecutionPolicy Bypass -File verify.ps1 "游戏目录"
```

## 八、常见问题

| 问题 | 解决 |
|---|---|
| 启动没反应 | 确认用 `war3.bat` 启动；确认路径是浅路径；杀软加白名单 |
| 创建弹回 | 确认 deploy.bat 跑过（插件已装）；确认图是转换版（不是原版 HM3W 图） |
| 地图过大 | 确认 Game.dll 是补丁版（deploy 自动处理） |
| 载入崩溃 | 确认图内 SLK 是标准格式（用本项目工具链转换） |

## 九、技术原理（简版）

- **HM3W 格式**：512 字节自定义头 + 标准 MPQ（哈希表/块表用暴雪标准算法+标准密钥加密）——剥头 + 表解密即可解包
- **地图锁定机制**：专属地图在 JASS 中调用扩展 native（共 63 个），纯净版无此注册 → 弹回/崩溃
- **解决**：`plugin\warcraft3\` 的 YDWE/JAPI 插件把这些 native 全部注册到引擎（dz_w3_plugin.dll 是关键组件）
- **SLK 单位数据**：网易格式 SLK 与暴雪标准格式列布局不同，引擎直接解析网易格式会死循环/数据错位——用 SlkLib 权威解析后重排为标准列布局（613 单位零丢失，含 Eidm 修复）
- **大小限制**：Game.dll 中 3 处 `cmp 0x800000` 检查 + 2 处 getter → 补丁为 1GB

## 十、文件清单

```
map_local_suite/
├── deploy.bat          # 一键部署（拖入 war3.exe 或按提示）
├── uninstall.bat       # 一键卸载（还原原版）
├── verify.ps1          # 5 项环境检查
├── README.md           # 本文档
├── 使用说明.txt         # 快速上手
├── 联机说明.txt         # 联机教程
├── Game_patched2.dll   # 大图补丁版 Game.dll（1.27.0.52240）
├── version127a\        # 1.27a 版本文件（11 文件，版本自动转换用）
├── patch_game.py       # 从原版重建补丁版 Game.dll 的脚本（逐字节一致）
├── gen_bat.py          # 重新生成 deploy/uninstall 脚本
├── tools\              # 改键面板 keybind_panel.exe（F7 呼出）+ 可选工具说明.txt
├── bin\                # YDWE 运行库（72 文件）
└── plugin\warcraft3\   # JAPI 插件（13 文件，config.cfg 9 插件全开）
```

## 十一、版本

- 兼容魔兽版本：**1.27.0.52240**
- 测试环境：Win10 / 纯净版 1.27a
- 打包时间：2026-11


