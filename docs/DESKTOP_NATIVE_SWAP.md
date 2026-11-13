# 桌面玩家：手动替换更小的原生内核

trit-tml 模组的原生内核（trit_rt.dll / libtrit_rt.so / libtrit_rt.dylib）有两种
构建形态：

| 版本 | 构建 profile | 体积（Windows 实测） | 特点 |
|---|---|---|---|
| **release**（默认） | thin LTO + opt-level=3 | **2330 KB** | 性能优先，桌面默认打包 |
| **release-mobile** | fat LTO + opt-level=z + codegen-units=1 + strip + no-default-features | **1652 KB** | 体积优先（-29%），性能差 ~2% |

两者 **FFI ABI 完全一致**、**行为完全一致**——可以随时互相替换，
模组代码不感知，不会崩游戏。

如果你在意模组总下载体积（release-mobile 能省 ~678 KB），或者用的是低配
笔记本、想让常驻内存更紧凑，就换成 release-mobile 版。

---

## 步骤

### 第 1 步：下载对应平台的 release-mobile 产物

打开仓库的 **Actions → native lib** 页面，找最新一次运行，下载对应平台的
`trit-rt-mobile-<os>` artifact：

| 你的系统 | 下载哪个 artifact | 里面的文件 |
|---|---|---|
| Windows 10/11 | `trit-rt-mobile-windows-latest` | `trit_rt.dll` |
| Linux | `trit-rt-mobile-ubuntu-latest` | `libtrit_rt.so` |
| macOS (Intel / Apple Silicon 都能直接用) | `trit-rt-mobile-macos-latest` | `libtrit_rt.dylib` |

解压出那个 `.dll` / `.so` / `.dylib`，记下它的路径（比如
`D:\Downloads\trit_rt.dll`）。

### 第 2 步：找到你游戏里的 .tmod 文件

tModLoader 的模组文件放在：

| 系统 | 默认路径 |
|---|---|
| Windows | `%Documents%\My Games\Terraria\ModLoader\Mods\TritRuntimeMod.tmod` |
| Linux | `~/.local/share/Terraria/ModLoader/Mods/TritRuntimeMod.tmod` |
| macOS | `~/Library/Application Support/Terraria/ModLoader/Mods/TritRuntimeMod.tmod` |

### 第 3 步：替换原生内核

.tmod 本质是个 **zip 包**。你可以用任何方式往里面塞文件，只要保持内部路径
正确（模组启动时从包内 `lib/Native/<文件名>` 路径读取，见下文验证）。

#### 方式 A：解压 + 覆盖 + 重打包（推荐，最稳）

```powershell
# Windows PowerShell 示例
$modPath = "$env:USERPROFILE\Documents\My Games\Terraria\ModLoader\Mods\TritRuntimeMod.tmod"
$workDir = Join-Path $env:TEMP "trit-swap"
New-Item -ItemType Directory -Path $workDir -Force | Out-Null

# 1. 解压 tmod
Expand-Archive -Path $modPath -DestinationPath $workDir -Force

# 2. 替换原生内核（把路径换成你第 1 步解压出来的文件位置）
Copy-Item "D:\Downloads\trit_rt.dll" -Destination "$workDir\lib\Native\trit_rt.dll" -Force

# 3. 备份原文件
Copy-Item $modPath "$modPath.bak"

# 4. 重打包（必须保持 zip 内部路径不变！）
Compress-Archive -Path "$workDir\*" -DestinationPath $modPath -Force

# 5. 清理
Remove-Item $workDir -Recurse -Force
```

Linux / macOS：

```bash
MOD="$HOME/.local/share/Terraria/ModLoader/Mods/TritRuntimeMod.tmod"
WORK="/tmp/trit-swap"
rm -rf "$WORK" && mkdir "$WORK"
unzip -q "$MOD" -d "$WORK"
cp ~/Downloads/libtrit_rt.so "$WORK/lib/Native/libtrit_rt.so"
cp "$MOD" "$MOD.bak"
cd "$WORK" && zip -rq "$MOD" . && cd -
rm -rf "$WORK"
```

#### 方式 B：直接用 7-Zip / WinRAR 打开 .tmod（懒人法）

1. 右键 `.tmod` → **7-Zip → 打开压缩包**（.tmod 没被识别成 zip 的话，手动选「所有文件」）
2. 进入包内的 `lib/Native/` 目录
3. 把你第 1 步解压出来的 `.dll` / `.so` / `.dylib` **拖进去覆盖**（7-Zip 会弹确认）
4. 关掉压缩包窗口，它会自动保存

macOS 用户：双击 `.tmod` 应该会被归档工具识别为 zip；不行就装 [The Unarchiver](https://theunarchiver.com/) 或用终端 `unzip -q`。

#### 方式 C：模组还没下载 / 首次安装时

tModLoader 会把模组缓存在 `Mods/` 目录。如果你从源码自己 Build 模组
（`TritRuntimeMod.csproj`），`TritStageNativeLibrary` target 已经会自动把
`target/release-mobile/<文件名>` 拷到 `lib/Native/`，然后 BuildMod 打出的
.tmod 就自带 release-mobile 内核了——不用手动再解包。

### 第 4 步：验证替换生效

启动泰拉瑞亚 + tModLoader，加载 TritRuntimeMod，打开游戏内模组调试日志
（或看 `tModLoader.log`）。搜以下内容之一：

```
[trit] 原生内核已加载，大小=1652000...   ← 数字接近 1652000 就是 mobile 版
[trit] 原生内核已加载，大小=2330000...   ← 数字接近 2330000 是 release 版（没替换成功）
```

或者直接数一下文件大小：

| 位置 | 怎么看 |
|---|---|
| 替换前的 artifact 本身 | 第 1 步解压出来的 `.dll` / `.so` / `.dylib` 右键属性 |
| 替换后的 .tmod 内部 | 7-Zip 里能直接看压缩包内单个文件的原始大小 |

release-mobile 应该在 **1500–1700 KB** 范围内；release 在 **2200–2500 KB**。

### 第 5 步（可选）：桌面玩家替换后，清理模组缓存在内存中的副本

tModLoader 加载过一次模组后会在进程里留一份副本。替换 .tmod 后：

1. **完全退出** tModLoader / 泰拉瑞亚（不能只退到主菜单）
2. 重新启动游戏
3. 模组会从新的 .tmod 重新加载原生内核

---

## 回滚

**最安全**：把 `.tmod.bak`（第 3 步 A 里留的备份）改回 `.tmod` 即可。
如果没备份，重新从 tModLoader 官方服务器下载最新版模组，或从 CI artifact
下载 `trit-rt-<os>`（不带 `-mobile` 后缀的那个 release 版）再替换一次。

---

## 风险 & 注意事项

| 场景 | 说明 |
|---|---|
| **FFI ABI 不匹配** | 不可能发生。release-mobile 和 release 编译时用的是同一版 trit-rt 源码，只变 LTO/strip/opt-level。如果源码版本差太大（跨了好几个 release），C# Shim 层的 `TritRuntime.ApiVersion` 会校验——版本对不上直接回退托管内核，不会崩。 |
| **Linux / macOS 替换时文件名大小写** | Linux / macOS 文件系统大小写敏感。包内路径是 `lib/Native/libtrit_rt.so`，你拖进去的文件名必须完全一样（全小写 `lib`、全小写 `libtrit_rt.so`）。Windows / NTFS 不敏感所以没事。 |
| **7-Zip 保存后压缩包损坏** | 极罕见，但万一发生就用 .bak 回滚。不要用 WinRAR 的「自动修复」选项覆盖——WinRAR 偶尔会改变 zip 内部目录顺序导致 tModLoader 读不到文件。 |
| **模组更新后你的替换被冲掉** | tModLoader 更新模组时会覆盖整个 .tmod。更新完后需要重新做一次第 3 步。建议把 release-mobile 原生内核存在单独文件夹里，下次直接拖进去就行。 |
| **Android 玩家** | Android 的 .so 走单独的 ABI 子目录（`lib/Native/android-arm64/libtrit_rt.so`），桌面玩家别碰那些——你替换的是平铺的 `lib/Native/libtrit_rt.so`，和 Android 那份互不干扰。 |

---

## 常见问题

**Q: 替换后游戏启动报错「找不到入口点」？**
A: 你可能把 Android 版的 .so 拖进了桌面 .tmod。两者同名但二进制完全不同——
Android 用 NDK clang 链接、bionic libc；桌面 Linux/macOS 用 gcc/clang + glibc。
确认你下载的 artifact 是 `trit-rt-mobile-<desktop-os>`，
不是 `trit-rt-android-arm64`。

**Q: 性能差多少？**
A: 实测 release-mobile 比 release 慢 **~2%**（桌面 CPU 位并行扫描 n=1 窗口），
人类玩家感知不到。省下的 678 KB 体积在弱网下载或磁盘紧张时可能更重要。

**Q: macOS Intel 能用 Apple Silicon 的 dylib 吗？**
A: 不行，架构不兼容。Intel Mac 下载 `trit-rt-mobile-macos-latest` 时如果
artifact 是 universal binary 就两边都能跑；如果分了 Intel / ARM 两份 artifact，
选对应那个。

**Q: 我有两个泰拉瑞亚安装路径（Steam + 官方独立版）？**
A: 各换各的。每个安装目录下的 `ModLoader/Mods/` 里都有独立的 .tmod，
互不影响。
