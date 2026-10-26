# 发布到 Rhino PackageManager（Yak）

> 结论：**可以**。Colugo 打成 `.yak` 推到 `yak.rhino3d.com`，用户在 Rhino 里 `PackageManager` 搜 **Colugo** 就能装。
> 目前只做了打包脚本，**没有推送**；等你把试用案例写完再执行最后一步。

---

## 1. 现在的状态

```powershell
cd gh
.\pack-yak.ps1          # 只打包，产物在 dist\
.\pack-yak.ps1 -Push    # 打包并推送（需要先 yak login）
```

已经跑通第一部分，产物：

```
dist\colugo-0.30.1-rh8_15-any.yak     0.84 MB
dist\Colugo\                          # 暂存目录：Colugo.gha + manifest.yml
```

文件名三段含义：

| 段 | 含义 | 谁决定 |
|---|---|---|
| `0.30.1` | 包版本 | `Colugo.csproj` 的 `<Version>`，脚本只读这一处 |
| `rh8_15` | **需要 Rhino 8.15 及以上** | Yak 扫 `.gha` 引用的 RhinoCommon 版本，自动算的 |
| `any` | 不区分 CPU | AnyCPU 程序集 |

> 构建时会有一条 `WARNING: Content version doesn't match manifest: '0.30.1.0' != '0.30.1'`。
> 这是 `.gha` 的四段程序集版本（0.30.1**.**0）和清单三段版本之差，**无害**，不影响安装。
> 想彻底消掉就把 manifest 的 `version` 写成 `0.30.1.0` —— 但那样 PackageManager 里也显示四段，不推荐。

---

## 2. 发布前要准备的东西

| 项 | 说明 | 现在 |
|---|---|---|
| **包名 `Colugo`** | **首次 push 就占用这个名字**，之后不能改（改 = 另一个包） | 定好了 |
| **McNeel 账号** | `yak login` 走浏览器授权；以后每个版本都要用**同一个账号** | 你还没登录过 |
| **版本号** | 只改 `gh/Colugo/Colugo.csproj` 的 `<Version>` | 0.30.1 |
| **试用案例 `.gh`** | 放仓库根目录 `examples\`，打包时会自动进 `samples\`（见第 3 节） | **你在写，等它** |
| **描述 / 链接** | 已写在脚本里，`url` 指向 GitHub；要改就在 `pack-yak.ps1` 里改 | 待你确认 |
| **图标** | 不强制。清单支持 `icon:`（图片地址），不填就是默认图标；GH 标签页图标来自 `.gha` 本身 | 已有 |

版本号建议：既然是"1.0 基础预览版"，可以直接定 `1.0.0`（试用者看到的是正式号，心理负担低）；
**不建议**用 `1.0.0-beta.1` —— Yak 能构建，但 PackageManager 对预发布版本的显示策略不受我们控制，
链接分享出去别人搜不到会很尴尬。先在 `csproj` 把 `0.30.1` 改成 `1.0.0`，再打包。

---

## 3. 试用案例怎么随包走（可选）

把 `.gh` 放进仓库根目录新建的 `examples\`，脚本会自动拷到包的 `samples\` 里，
安装后落在这里：

```
%APPDATA%\McNeel\Rhinoceros\packages\8.0\Colugo\<版本>\samples\
```

**GH 不会自动索引它们**，用户要手动打开。所以更实用的做法是：
`examples\` 同步到 GitHub 仓库，`url` 指过去，PackageManager 页面点一下就进仓库。
包内 `samples\` 只当"离线兜底"。

---

## 4. 发布（三步，等你说开始）

```powershell
# 1) 登录（第一次会弹浏览器，用要长期持有这个包的账号）
& "D:\Program Files\Rhino 8\System\Yak.exe" login

# 2) 打包（版本号先在 csproj 改好）
cd gh
.\pack-yak.ps1

# 3) 推送
& "D:\Program Files\Rhino 8\System\Yak.exe" push "..\dist\colugo-1.0.0-rh8_15-any.yak"
```

或者一条命令：`.\pack-yak.ps1 -Push`。

推送成功后会立刻出现在 `https://yak.rhino3d.com/packages/colugo`，
PackageManager 的索引有缓存，搜不到先等几分钟或重启 Rhino。

---

## 5. 用户怎么装

1. Rhino 里输入 `PackageManager`（或菜单 工具 → 包管理器）；
2. 搜 `Colugo` → Install；
3. **重启 Rhino**；
4. Grasshopper 里出现 `Colugo` 标签页，`Runtime` 分组下有 7 只表达式电池。

安装落点：

```
%APPDATA%\McNeel\Rhinoceros\packages\8.0\Colugo\<版本>\
```

---

## 6. ⚠️ 本机特别注意：先删手工装的那份

你现在 `%APPDATA%\Grasshopper\Libraries\Colugo.gha` 有一份手工安装的，
它和 yak 包里的那份**是同一批 GUID**。两份同时存在时 GH 会加载两次 ——
表现为两个 Colugo 标签页、或者电池互相顶掉（`Data conversion failed` 之类）。

**装 yak 版之前，把这个目录里的 `Colugo.gha` 和 `Colugo.*.bak` 全部删掉。**

发布前自查：`gh\probe` 里的 51 个 GUID 唯一性断言仍在跑，能保证包内不撞；但撞不了"两份文件"。

---

## 7. 更新与撤回

| 动作 | 命令 | 说明 |
|---|---|---|
| 发新版本 | 改 `<Version>` → `pack-yak.ps1 -Push` | 同版本号不能重复推，会报 already exists |
| 撤回某个版本 | `yak yank colugo 1.0.0` | 只是从索引隐藏，已装的人不受影响 |
| 加协作者 | `yak owner add colugo <邮箱>` | 团队发布用；`yak owner list colugo` 查当前所有者 |
| 查本地已装 | `yak list` | — |

---

## 8. 兼容性：`rh8_15` 的含义

Yak 从 `.gha` 引用的 `RhinoCommon` 版本推断最低 Rhino 版本。
我们的插件编译时引用的是本机 **8.15.25013**，所以包标签是 `rh8_15` ——
**Rhino 8.0–8.14 的用户装不上**（PackageManager 会提示不兼容）。

我试过在 manifest 里写 `rhino_version: 8.0`，Yak 忽略它，仍然出 `rh8_15`。
要放宽只有一条路：在更低版本的 Rhino SDK 上编译。当前 8.15 已是 8.x 主流，暂时不建议为此返工。

---

## 9. 常见问题

| 现象 | 原因 / 处理 |
|---|---|
| `找不到 Yak.exe` | Rhino 不在 C:。脚本已从注册表读 `InstallPath`，仍失败就手动改脚本里的 `$rhino` |
| `yak push` 报 401 | 没登录或 token 过期，重跑 `yak login` |
| push 报 `already exists` | 版本号重复。改 `<Version>` 再打包 |
| 装完 GH 里没有 Colugo | ① 没重启 Rhino；② `Libraries` 里还有旧的手工副本；③ Rhino 版本 < 8.15 |
| 包体积 | 0.84 MB（内嵌运行时 + three.js r186 + 图标全部现画），正常 |
| 电池图标是空的 | 图标是 GDI+ 现画的，装完首次打开 GH 才生成；GH 缓存过一次就不会再变 |

---

## 10. 等你的三样东西

1. `examples\` 里的试用 `.gh`（可以有多个，自动随包）；
2. 包描述要不要改（现在是英文一段，给国际用户看；要不要加中文版）；
3. 定版本号：`1.0.0` 还是继续 `0.3x`。

这三样定了，`.\pack-yak.ps1 -Push` 一条命令就上线。
