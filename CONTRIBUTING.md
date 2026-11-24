# 贡献 / 发版说明

## 代码结构

```
src/ItemPicker.cs     插件全部逻辑（C# 5，无外部依赖）
build.ps1             本地编译脚本（用系统自带 csc，不需要 SDK）
release.ps1           一条命令发版（改版本 → 编译 → 提交 → 打 tag）
dist/ItemPicker.dll   最近一次编译产物，CI 打包时直接取它
说明.txt              中文使用说明，会一起打进发布包
.github/workflows/release.yml   推 tag 时自动打包并发 Release
```

## 为什么 CI 不编译

插件需要引用游戏自带的 `Assembly-CSharp.dll`、`Unity.TextMeshPro.dll`、`UnityEngine.*.dll`，
这些是**游戏版权程序集，不能放进公开仓库**，GitHub 的 runner 上也没有游戏本体。

所以：**编译在本地做，CI 只负责打包 + 发布**。
`dist/ItemPicker.dll` 是提交进仓库的编译产物，CI 直接使用。

## 日常开发

```powershell
# 1) 改代码
#    src/ItemPicker.cs

# 2) 编译（引用你本机的游戏）
./build.ps1 -GameDir "C:\...\Casualties Unknown" -Out ".\dist\ItemPicker.dll"

# 3) 把 DLL 放进游戏的 BepInEx/plugins 里，启动游戏实测
#    按 F8，确认菜单能开、能刷物品、日志没有 Error

# 4) 提交（源码 + 产物一起）
git add src dist 说明.txt
git commit -m "feat: xxx"
git push
```

## 发版

```powershell
$env:GH_TOKEN = 'github_pat_...'   # 需要 Contents: R/W + Workflows: R/W
./release.ps1 -Version 1.0.4 -Notes "修复 xxx"
```

脚本会：改版本号 → 编译 → 通过 API 提交到 `main` → 创建 tag `v1.0.4`。
tag 一推上去，Actions 就会自动打 zip、建 Release、挂附件。

也可以手动：

```powershell
git tag v1.0.4
git push origin v1.0.4
```

## 版本号约定

- 语义化版本 `MAJOR.MINOR.PATCH`，只改插件与说明文件里的版本字符串
- 插件版本在 `src/ItemPicker.cs` 顶部：`[BepInPlugin("qol.itempicker", "Item Picker", "1.0.4")]`
- tag 必须与之一致（`release.ps1` 会自动同步）

## 提 PR 注意

- 只能保证「代码能编译 + 游戏内可用」；CI 不会编译，请本地验证后附截图/日志
- 不要提交游戏程序集、`BepInEx` 本体、存档文件
- 不要引入付费/闭源依赖，保持插件只依赖 BepInEx + Harmony + 游戏自身
