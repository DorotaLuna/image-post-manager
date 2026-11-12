# Image Post Manager

一个自用的 Windows 本地图片管理小工具，用图组整理 AI 生成的图片，方便像浏览图片网站一样回看自己的作品。

除手动复制 ComfyUI 工作流外，不分析提示词或图片元数据。

## 项目定位与维护

这个项目主要是为了解决我自己的图片整理需求，后续更新维护随缘；如果你有更好的想法，推荐自己实现，记得提pr让我也用用。

目前的功能以我的个人使用场景为主，可能存在尚未发现的问题。首次使用请先用备份图片试用，重要图片请保留独立备份。

## 功能

- 按“图库 → 图组”组织图片，瀑布流浏览、拖动排序和自定义封面。
- 搜索图组名称与原文件名，收藏筛选、跨页选择和批量整理。
- 原图缩放、全屏浏览、双图同步缩放与对比。
- 从 PNG 复制完整 ComfyUI 工作流；复制图片文件或导出未经重新编码的原图。
- 重复文件提示、删除撤销、已移除图组恢复和索引历史。
- 深色、浅色和跟随系统主题。

## 使用前必读

**导入是移动，不是仅建立引用或保留来源的复制。** 程序先写入图库并校验哈希，再移除来源文件。希望保留来源时，请先复制一份用于导入。

- 图库内原图以 GUID 命名，原文件名保存在索引中；导出原图时使用原文件名，同名自动编号。
- 删除图片前会保存图库内的恢复副本，再将原文件送入 Windows 回收站。恢复副本目前不会自动清理，因此删除不一定释放图库占用的空间。
- 索引历史只备份索引，不是完整的图片备份。备份图库时应保留整个图库文件夹。
- 不要用旧版本程序写入已升级的图库，以免丢失旧版本不认识的字段。
- 尚未完成万张真实图片压力测试；设计规模不代表已经验证的性能承诺。

## 运行

仅支持 Windows x64，需要安装 .NET 10 Windows Desktop Runtime x64。

如果从 Releases 下载了程序压缩包，请完整解压后运行 `ImagePostManager.exe`，不要只复制 exe。程序目录需要可写。

设置保存在程序旁的 `settings.json`，图库位置由用户选择。新建或添加图库后，建立图组并导入图片即可。

详细操作见 [使用说明](使用说明.md)。

## 从源码构建

需要 Windows 和 .NET 10 SDK。在仓库根目录执行：

发布输出位于 `app/`，不捆绑运行时。

```powershell
dotnet build src/ImagePostManager.csproj -c Release
powershell -ExecutionPolicy Bypass -File build.ps1 -Output app
```

自检，使用合成图库与替代删除执行器

```powershell
$p = Start-Process -FilePath .\app\ImagePostManager.exe -ArgumentList '--self-test', 'test-output' -WindowStyle Hidden -Wait -PassThru
$p.ExitCode
Get-Content .\test-output\test-results.txt
```