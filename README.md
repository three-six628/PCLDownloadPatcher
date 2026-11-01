# PCL 下载修复工具 · PCLDownloadPatcher

单文件 .NET 修复工具：恢复新版 PCL（Plain Craft Launcher 2，2.99.0.1 及同结构版本）中被屏蔽的原版游戏下载与整合包安装功能，并支持「更新时自动修复」闭环。修复逻辑从旧版 2.13.1.1 提取，通过 IL 注入实现，不修改任何业务逻辑以外的内容。

## 功能特性

- **恢复「下载 → 原版游戏」入口**：新版构造函数将左侧导航项隐藏（`Visibility=Collapsed`），已删除该屏蔽指令
- **恢复原版游戏版本列表加载**：新版将下载页加载器替换为抛合规异常闭包，已恢复旧版 `DlClientListLoader` 加载链路
- **恢复整合包安装**：与「原版下载」同被屏蔽的有参/无参 `ModpackInstall` 一并恢复，7 平台分发（CurseForge / HMCL / MMC / MCBBS / Modrinth / LauncherPack / 压缩包导入）
- **更新时自动修复**：在 PCL 自动更新方法尾部注入修补器调用，新版本下载完成后自动执行同样的修补，全程无需干预
- **单文件发布**：内嵌 Mono.Cecil，无任何外部依赖，绿色运行

## 修复原理

| 修复点 | 目标 | 说明 |
|---|---|---|
| Fix1 | `PageDownloadInstall::LoaderInit` | 30→18 条 IL，第 5 参恢复为 `ModDownload::DlClientListLoader`，与旧版 `PageLoaderInit` 同构 |
| Fix2 | `ModModpack::ModpackInstall(File,…)` | 删除屏蔽三连（ldstr 屏蔽文本 / newobj Exception / throw），恢复 7 分支分发链 |
| Fix3 | `ModModpack::ModpackInstall()` 无参入口 | 重建文件选择 → 校验 → 线程安装流程，新增闭包类 `_Closure$__DlFix` |
| Fix4 | `PageDownloadLeft::.ctor` | 删除 `ItemInstall.Visibility = Collapsed`，恢复「原版游戏」导航入口 |
| Hook | `ModSecret::DownloadLatestPCL` | 尾部最后调用后注入 `Process.Start(修补器, "--update --inplace \"<主程序>\"")` |

## 使用方法

### 图形界面

1. 双击运行 `PCLDownloadPatcher.exe`
2. 点击「修复」按钮，弹出文件选择框
3. 选中新版 PCL 主程序（`Plain Craft Launcher 2.exe`），确定后自动执行修复
4. 同目录生成 `<文件名>.download_fixed.exe`，用其替换原主程序后启动
5. 可选：勾选「更新自动修复」，PCL 自动更新后将自动对新版本执行修补

> 注意：更新钩子指向修补时的主程序文件名，改名替换后依然生效；本工具需与 PCL 主程序保持同级目录（或相对路径不变）。

### 命令行

```
PCLDownloadPatcher.exe --patch "C:\path\Plain Craft Launcher 2.exe"
# 修补指定文件，输出 <同名>.download_fixed.exe

PCLDownloadPatcher.exe --update --inplace "C:\path\Plain Craft Launcher 2.exe"
# 更新自动修复专用：读取同目录 PCLDownloadPatcher.Settings.json，
# autoFix=true 时原地修补目标文件，false 时跳过；由 PCL 更新流程自动触发。
```

### 设置文件

勾选「更新自动修复」后，在工具同目录动态生成 `PCLDownloadPatcher.Settings.json`：

```json
{"autoFix":true}
```

该文件不进 PCL 缓存，删除或不勾选即关闭自动修复。

## 从源码构建

要求：.NET SDK（含 .NET Framework 4.7.2 目标包，SDK 自动还原 `Microsoft.NETFramework.ReferenceAssemblies`）。

```
dotnet build -c Release
```

产物：`bin\Release\net472\PCLDownloadPatcher.exe`（单文件）。

> `cecil\Mono.Cecil.dll` 为 Mono.Cecil 0.11.6，构建时按嵌入式资源打包进单文件，运行时经 `AssemblyResolve` 自加载。

## 目录结构

```
PCLDownloadPatcher/
├── Program.cs                  # 入口：GUI 启动 / --patch / --update 模式与设置读写
├── MainForm.cs                 # 主界面：文字说明 + 「更新自动修复」复选框 + 「修复」按钮
├── PatchCore.cs                # 核心：四修复点 + 更新钩子 + 内嵌程序集提取与写回
├── PCLDownloadPatcher.csproj
├── cecil/                      # Mono.Cecil 0.11.6（构建依赖，嵌入产物）
├── docs/                       # 文档
└── bin/ obj/                   # 构建产物（已 gitignore）
```

## 免责声明

本项目仅用于个人学习与研究目的。使用者应遵守相关法律法规与软件许可协议；因使用本项目产生的任何后果由使用者自行承担。本工具仅适配与 2.99.0.1 结构一致的 PCL 版本，新版结构变更时需重新适配。
