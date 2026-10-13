# BrowserAssist 标签扫描器

用于管理 Virt-A-Mate（VaM）VAR 扩展包的 Windows 桌面工具，读取 BrowserAssist 标签及 VaM 收藏、隐藏标记。

## 功能

- 按标签扫描 VAR，查看包名、收藏、隐藏状态、提示标记、路径和同级文件。
- 展开同级文件，按完整路径同步所有位置的勾选状态。
- 智能选择单包目录的附加文件和冗余父目录，提示依赖包及子目录中的收藏包。
- 在最近扫描结果中搜索和过滤隐藏、缺失、依赖包。
- 悬停预览系统缩略图，点击路径或双击文件名定位到资源管理器。
- 将选中的文件和文件夹移入回收站，支持 VaM / vambox 运行时的逐项递归回收。
- 为明确勾选的 VAR 写入 VaM 原生隐藏标记，不递归隐藏勾选文件夹的内容。

## 构建与运行

需要 Windows 8 或以上、.NET Framework 4.5 或以上，以及 Windows PowerShell 或 PowerShell 7。

```powershell
.\Build.ps1
.\dist\BrowserAssistScanner.exe
```

程序可以设置 VaM 根目录和 VAR 扫描目录。详细操作和数据读取规则见 [README.txt](README.txt)。

Windows 的 VAR 缩略图处理器需要由用户自行安装；没有处理器时会显示“无可用缩略图”。

## 验证

```powershell
.\Test.ps1
```

测试创建独立的临时 VAR、标记和目录样本；回收测试会将这些样本移入 Windows 回收站。UI 截图和其他测试产物集中在 `test-artifacts`，不纳入 Git。

可选设置 `BROWSERASSIST_TEST_VAR` 为本机 VAR 路径，验证系统缩略图处理器；默认不访问真实包进行此项测试。

## 数据与隐私

扫描和刷新只读取本机 VaM / BrowserAssist 数据。删除操作使用 Windows 回收站，隐藏操作写入本机 `AddonPackagesFilePrefs` 的 `.hide` 标记。

程序不上传包、标签或配置。仓库仅包含源码、测试源码、构建脚本和文档，不包含 VAR 文件、用户数据、扫描结果、截图或本地设置。

本项目为独立工具，与 VaM 和 BrowserAssist 作者无隶属关系。
