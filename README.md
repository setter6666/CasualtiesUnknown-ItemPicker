# Item Picker — 物品生成器（Casualties: Unknown）

一个 BepInEx 5 插件：用图标网格浏览并直接生成游戏内的物品，**不走控制台命令，所以不会禁用成就**。

> A BepInEx 5 plugin for *Casualties: Unknown*: browse every spawnable item as an icon grid
> and spawn it directly — **no console commands, so achievements are not disabled**.

![screenshot](docs/screenshot.png)

## 功能 / Features

- 图标网格列出全部可生成物品（当前游戏版本 301 项），带分类标签页
- 鼠标悬停显示名称、完整说明与物品大图（自绘提示框，跟随鼠标）
- 左键生成 1 个；`Shift`+左键生成 10 个；`Ctrl`+左键生成 100 个
- 直接打字按中文名或英文 ID 筛选
- 菜单打开时临时屏蔽角色操作，关闭后恢复
- 生成位置为角色脚下，2 格内自动拾取（背包满则掉在地上）
- 成就安全：物品直接实例化，不经过 `ConsoleScript.CheckForCheats`

## 安装 / Installation

1. 安装 [BepInEx 5.4.23.4 (x64)](https://github.com/BepInEx/BepInEx/releases) 到游戏根目录（含 `CasualtiesUnknown.exe` 的那层）。
2. 把 `ItemPicker.dll` 放进 `BepInEx/plugins/`。
3. 启动游戏，按 **F8** 打开菜单。

## 使用 / Usage

| 操作 | 效果 |
|---|---|
| `F8` | 打开 / 关闭菜单 |
| 悬停图标 | 显示名称 / 说明 / 大图 |
| 左键 | 生成 1 个 |
| `Shift` + 左键 | 生成 10 个 |
| `Ctrl` + 左键 | 生成 100 个 |
| 打字 | 按名称或 ID 筛选 |
| `Esc` | 清空筛选 → 再按关闭 |

配置文件：`BepInEx/config/qol.itempicker.cfg`

```ini
[General]
## 开关按键（Unity KeyCode 名称，例如 F8 / F9 / Insert）
ToggleKey = F8
## 进图后自动打开菜单的延迟秒数（0 = 关闭）
AutoOpenSeconds = 0
```

## 从源码构建 / Build from source

需要 Windows 自带的 .NET Framework 编译器（`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`），
无需 .NET SDK，也无需 Visual Studio。

```powershell
./build.ps1 -GameDir "C:\Program Files (x86)\Steam\steamapps\common\Casualties Unknown" -Out ".\ItemPicker.dll"
```

`-GameDir` 用来引用游戏自带的 `Assembly-CSharp.dll`、`UnityEngine.*.dll`、`Unity.TextMeshPro.dll`
以及 `BepInEx/core` 下的 `BepInEx.dll`、`0Harmony.dll`。

> 注意：`UnityEngine.CoreModule.dll` 等是 netstandard 2.1 目标，编译时必须额外引用游戏
> `CasualtiesUnknown_Data\Managed\netstandard.dll`，否则会报 CS0012。脚本里已处理。

## 已知限制 / Known limitations

- 多人游戏：只在本地生成，主机与其他玩家看不到。
- 只包含带 `Item` 组件的预制体；建筑 / 实体不在列表内。
- 游戏更新后若物品预制体改动，列表会自动跟随（运行时遍历 `Resources`），一般无需重新编译。

## 许可 / License

MIT，见 [LICENSE](LICENSE)。与游戏开发商及 QoL: Unknown 的作者均无隶属关系。
物品名称与图标归游戏版权方所有。
