# DownKyi 修复版 - GitHub Actions 自动编译

这是带崩溃 bug 修复的 DownKyi 分支。

## 修复了什么

**4 条独立崩溃路径**（原版多任务下载时频繁自动退出 CLR20r3）：

1. ✅ `PartialDownloader.DownloadProcedure` — 分片流未释放就触发完成事件（FileStream `using` 块内调用事件）
2. ✅ `MultiThreadDownloader.MergeParts` — 并发重入（多线程同时判定"全部完成"）
3. ✅ 后台线程异常裸奔 → 加三道全局异常兜底（`DispatcherUnhandledException` / `AppDomain.UnhandledException` / `TaskScheduler.UnobservedTaskException`）
4. ✅ 集合并发遍历 + 边界隐患

详细报告见 `downkyi-崩溃修复报告.md` 和 `downkyi-crashfix.patch`。

## 怎么用（3 步）

### Step 1: 推到 GitHub

1. 在 https://github.com/new 创建一个新仓库（比如 `downkyi-patched`），**不要**初始化 README
2. 回到本地，把这个目录推到你的仓库：

```bash
cd downkyi-fork
git init
git add .
git commit -m "DownKyi 修复版：多任务崩溃 bug + 全局异常兜底"
git remote add origin https://github.com/<你的用户名>/downkyi-patched.git
git branch -M main
git push -u origin main
```

### Step 2: 触发 Actions

- 进入 GitHub 仓库 → **Actions** 标签 → 选择 **"Build DownKyi (修复多任务崩溃 bug 版)"**
- 点击 **"Run workflow"** → 选择 `main` 分支 → 点绿色按钮
- 等待 5-10 分钟，Actions 自动完成：
  - 还原 NuGet 包
  - 编译 Release 版本
  - 打包 zip
  - 上传 artifact

### Step 3: 下载 exe

- Actions 完成后，**在该 run 页面底部 Artifacts 区域**下载 `DownKyi-patched.zip`
- 解压到任意目录（**注意：本目录里已包含 aria2c.exe 和 ffmpeg.exe**，运行时不需要额外安装）
- **双击 `DownKyi.exe` 即可使用**

> **重要**：本机已装 .NET Framework 4.8 ✅，无需装 .NET 4.7.2。Windows 10/11 默认自带 .NET 4.8。

## 工作原理

`.github/workflows/build.yml` 在 GitHub Actions 的 `windows-latest` runner 上跑：

1. 用 VS 2022 自带的 MSBuild 编译 `src/DownKyi.sln`（runner 预装 VS 2022 + .NET Framework 4.7.2 targeting pack + MSBuild 17）
2. **完全不需要本机有 VS / SDK**——所有编译都在云端跑
3. 产出 zip 包让你下载

> **GitHub Actions 完全免费**（公开仓库无限制，私有仓库每月 2000 分钟免费额度）

## 工程改动说明

- 4 个源文件 bug 修复（详见 `downkyi-崩溃修复报告.md`）
- `StorageCover.cs` / `StorageHeader.cs` 剥除 `WebPSharp` 依赖（WebPSharp 0.5.1 已被 nuget.org unlist）
- Actions workflow 自动创建 WebPSharp stub，让 csproj 的 NuGet 检查通过

## 本机编译行不行？

本机 WIN11 普通用户（`IsAdmin=False`），无 VS / 无 MSBuild / 无 .NET 4.x SDK：

- 装 VS / MSBuild 需要管理员权限
- 用便携 SDK 8 + dotnet build 撞 SDK 8 的 netfx TFM alias bug（已尝试所有已知 workaround 无效）

所以走 **GitHub Actions** 是最省事的方案。

## 替代 B 站下载工具

如果只是想下载 B 站视频、不在乎崩溃修复：

- **DownKyi 原版 release**：https://github.com/leiurayer/downkyi/releases
- **yt-dlp**（命令行）：`yt-dlp "https://www.bilibili.com/video/BVxxxx"`
- **B 站官方客户端**（支持离线缓存）