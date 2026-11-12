# Image Post Manager

## 项目用途

仅支持 Windows x64 的轻量本地图片管理器。用户手动把 AI 生成的图片整理为图组（Post），像刷图片分享网站一样回看作品。同一组可以跨日期、跨模型追加图片，除右键复制时读取 PNG 中的 ComfyUI 工作流外，不主动解析提示词或元数据，不联网、不运行 AI 模型。格式为 PNG / JPG，设计考虑约 1,000～10,000 张图片，尚未完成万张真实图片压力测试。

此目录包含项目源码和构建脚本。2026-10-23 增加浏览位置记忆、搜索收藏、跨页批量操作、原图缩放、双图对比、导入取消、重复提示、原图导出及恢复中心。构建中间文件位于 src/bin、src/obj，自检数据位于工作区 work/，不属于用户图库。

## 技术与文件

C# / WPF，目标 net10.0-windows；无第三方应用依赖。主界面大部分以 C# 构建，样式在 XAML 中。

| 文件 | 职责 |
| --- | --- |
| src/Program.cs | 入口、加载配置、异常处理、自检入口 |
| src/MainWindow.cs | 三级导航、卡片展示、导入、选择和右键菜单 |
| src/Library.cs | Photo/Post/Catalog 模型、JSON 索引、文件操作及恢复 |
| src/Viewer.cs | 大图显示、左右键/滚轮翻页、Delete 删除 |
| src/BrowseFeatures.cs | 搜索收藏、浏览位置、跨页选择、复制导出、导入重复决策、恢复中心 |
| src/LibraryRecovery.cs | 已移除图组、删除恢复副本、索引历史、重复检测及原图导出 |
| src/PhotoViewport.cs、CompareViewer.cs | 按需原图解码、DPI 对应的 100% 显示、缩放平移及双图同步对比 |
| src/*EnhancementTests.cs | 数据恢复、浏览交互、原图解码和视口回归测试 |
| src/MasonryPanel.cs | 保持原比例的瀑布流排布、图组视口虚拟化与滚动锚点，列宽基准 TileWidth 可调 |
| src/PerformanceFeatures.cs、ThumbnailCache.cs、AspectSurface.cs | 图组卡片复用、后台快照保存、有限缩略图缓存及纵横比占位 |
| src/PerformanceTests.cs | 千图组滚动、复用、重排、缓存与保存失败回归 |
| src/ModernStyles.xaml | 按钮、树形导航、滑块、菜单等控件样式 |
| src/ThemeBrushes.cs、WindowAppearance.cs | 动态主题画刷、Windows 标题栏外观 |
| src/SystemTheme.cs | 读取 Windows 应用颜色模式；启动及选中“系统”时检测，无轮询 |
| src/ComfyWorkflow.cs | 按需读取 PNG 的 workflow JSON，支持 tEXt / zTXt / iTXt |
| src/FeatureTests.cs | 元数据读取、主题解析和原生窗口属性设置检查 |
| src/Preferences.cs | 便携设置文件、图库列表、主题与图像尺寸保存及旧配置迁移 |
| src/ExplorerNavigation.cs | 资源管理器打开目录或定位文件 |
| src/SelfTest.cs | 合成图片、文件操作和 WPF 界面回归检查 |

## 已确定的行为

- 层级为“库 → 图库 → 图组”：库是虚拟汇总，每个图库是用户选择的磁盘文件夹。
- 导入最终是移动原文件；先写入图库、校验 SHA-256，再移除来源。图库内使用 GUID 文件名，索引记录原名。
- 所有图组共享图库的 originals 文件夹，没有独立图组子目录。
- 图库导航右键“移除管理器链接”只移除注册，可重新添加；图组右键“移至已移除图组”保留分组、顺序、封面和图片，在恢复中心还原。
- 图片右键菜单区分“复制 ComfyUI 工作流”“复制图片文件”“导出原图…”。工作流仍只读取右键原始 PNG 中的完整 workflow JSON，不替代为 prompt 参数；失败不改剪贴板。文件复制使用 Windows 文件剪贴板，导出保留原文件名、字节和元数据，同名自动编号，不覆盖目标文件。导出目标必须在图库之外。
- 图库页设置图库封面，图组页设置图组封面；右键移动或删除只作用于目标一张图片。移动限于同一图库，目标选择支持搜索。
- 删除前先校验并保存本地恢复副本，再将原文件送入 Windows 回收站。恢复记录保存原图组、位置和封面。大图 Delete 直接删除，随后显示下一张；删空后关闭查看器，可在主窗口 Ctrl+Z、底部撤销按钮或恢复中心恢复。批量删除以持久化 DeleteBatchId 记录为一次操作，Ctrl+Z 按删除日志逆序恢复整批（包括重启后）；失败时停止，保留剩余记录供重试。恢复到已移除图组时，图组仍保留在已移除列表。
- 选择模式点击图片选中/取消，Shift 连选，Ctrl+A 全选当前筛选结果；翻页保留选择，改变搜索/收藏筛选或导航会清除选择。支持批量收藏、取消收藏、移动、文件复制、原图导出及删除，选择两张可对比。筛选结果禁用手动重排，避免改变隐藏图片的顺序。
- 搜索匹配图组名称和原文件名，图库级搜索展示匹配图片，图组内限定本组；收藏筛选可与搜索组合。图库级搜索、收藏及全部图片打开查看器后，按当前完整结果集跨图组连续浏览（跨分页），标题显示范围和实际所属图组；打开时固定结果集，收藏变化不打断当前浏览。纯图库视图可切换“全部图片”。
- 返回和侧栏切换在当前会话内记住每个图库/图组的页码、滚动位置及筛选。缩略图异步加载后重新应用滚动位置，不写入全局设置。
- 导入前检查完全相同的文件，可跳过一次/全部重复、继续导入一次/全部重复或停止；跳过保留来源。停止导入在当前文件安全完成后生效。完整导入结果可从工具栏查看，不再截断错误列表。
- 查看器默认适合窗口；100%、Ctrl+滚轮或 +/- 可按需加载完整原图并缩放，鼠标拖动平移，双击切换适合窗口与100%，F11 全屏，F 收藏，Ctrl+Z 撤销最近一次删除。双图对比支持独立或同步缩放、拖动。
- 内容区无标题、面包屑和卡片文字栏；保留操作按钮、图组数量角标、选择角标及悬停名称。导航仍显示名称。
- 操作栏最左侧固定为「← 返回」，逐级上退：图组 → 图库 → 库（库为顶层，该按钮不出现）。它与 Esc 走同一条路径。
- 操作栏最右侧为「图像尺寸」滑块，范围 140～420，默认 236，对应瀑布流的列宽基准。调大即单张图片更大、列数更少；同一个面板同时管理库封面、图组封面与图片，窗口缩放时列数自动增减。取值随设置持久化。
- 深色、浅色、系统三种主题，保留已有默认深色。“系统”保存为 system，启动及选中该选项时读取 Windows AppsUseLightTheme，不轮询；读取失败回退深色。主窗口、查看器和自定义对话框使用相同的有效主题。
- 背景、原生标题栏及外边框统一为浅色 #ECEDF0 / 深色 #111113；原生边框和标题栏精确着色依赖 Windows 11 的 DWM 属性，旧版 Windows 保留系统外观。圆角、轻量半透明表面及悬浮反馈；不使用整窗实时模糊。图像区域禁用默认虚线焦点框。
- 图组列表改为连续滚动，保存完整顺序与布局位置，仅实例化视口和上下约500像素缓冲区内的卡片；组内图片、搜索和全部图片视图仍每页最多40项。图组拖动支持边缘自动滚动与全局插入位置，Esc取消不修改顺序。侧栏按稳定ID复用，每段最多60个图组，通过前后入口切换，不随主区滚动反复重建。
- 缩略图并发最多两个任务，按图库路径和图片ID合并重复加载；解码LRU缓存预算64MiB，额外保留最多96张图组卡片（其显示图像引用另占内存）。图片纵横比逐步学习并保存在Photo.AspectRatio，旧图库不在启动时扫描原图；按已知比例预留高度，批量调度布局与滚动恢复。大图仍串行解码。
- 主窗口普通收藏与拖放只更新目标状态或现有控件顺序，采用UI线程复制的独立Catalog快照后台保存；保存期间暂停修改，失败回滚，关闭等待保存结束。保存写入由锁串行执行；导入/删除仍在原有日志时点同步持久化。返回优先复用有限缓存中的图组卡片并恢复滚动位置。

## 数据与修改注意点

图库包含 library.json、library.json.bak、library.lock、originals/、thumbnails/，新版增加 history/ 和 recovery/。Catalog 保存活动图组、已移除图组、删除记录及图库封面 ID，Post 保存封面、创建时间和有序图片列表。Photo.Source/Hash 用于导入恢复，Deleting 用于删除恢复，IsFavorite 为收藏标记，AspectRatio 为按需学习的图片纵横比（旧索引默认0表示未知）。

索引 Version 仍为 1，新字段对已有图库提供默认值；不要用旧程序写入已升级图库，旧程序不认识的新字段会丢失。每次稳定索引保存前保留历史快照，最多30份；未完成导入/删除的中间状态不提供为历史恢复点。恢复历史时保留当前索引快照，快照之后增加的图片保留为已移除图组，不删除其文件。缺失原图优先从恢复副本取回，否则报告缺失。历史只备份索引，不备份全部原图。

recovery/ 中的校验副本会在撤销后继续保留，用于历史还原，当前没有自动清理策略；因此删除图片不会立即释放这部分图库空间。旧版删除若没有恢复副本，需要先从 Windows 回收站还原原文件，再在恢复中心恢复记录。

全局设置按便携方式保存在程序目录的 settings.json（与 ImagePostManager.exe 同级），不写 %LOCALAPPDATA%。Preferences.PortableDirectory 取 AppContext.BaseDirectory；Preferences.MigrateLegacy 在首次启动时把旧的 %LOCALAPPDATA%/ImagePostManager（settings.json 或旧版 library.txt）搬到程序目录，成功后才删除旧文件，且绝不覆盖已存在的程序目录设置。程序目录不可写时启动即报错，不做回退。Preferences 里的 CardWidth 记录图像尺寸滑块。真实图库在程序目录之外，正常启动可能打开用户配置；测试应使用 --self-test 和独立测试目录。

继续调整时，保留索引原子替换、图库独占锁及导入/删除恢复记录。删除和移动后同步维护图组封面、图库封面与查看器当前位置。不要把“移除链接”和“删除原图”合并。主题画刷使用绑定以避免 WPF 冻结后的修改异常。列宽基准只在 MasonryPanel.TileWidth 一处生效，不要再往卡片上写固定宽度。

## 构建与验证

需要 Windows 和 .NET 10 SDK。在此目录执行：

```powershell
dotnet build src/ImagePostManager.csproj -c Release
powershell -ExecutionPolicy Bypass -File build.ps1
powershell -ExecutionPolicy Bypass -File build.ps1 -Output ..\app   # 发布到工作区的 app 目录
```

build.ps1 默认发布到 app/，不捆绑 .NET 运行时。运行端需要 .NET 10 Windows Desktop Runtime x64；缺失时由原生 apphost 提示安装。

发布后自检：

```powershell
$p = Start-Process -FilePath .\app\ImagePostManager.exe -ArgumentList '--self-test', 'test-output' -WindowStyle Hidden -Wait -PassThru
$p.ExitCode
Get-Content .\test-output\test-results.txt
```

SelfTest 会生成合成图片与临时图库，检查文件安全、恢复和 UI 行为。删除测试使用测试目录内的替代执行器，不验证真实 Windows 回收站流程。2026-10-23 发布版带示例图片通过 238 项检查，结果在工作区 work/enhancements-published/test-results.txt，界面截图在同目录。覆盖旧版回归、43项数据增强检查、35项搜索/选择/导入交互检查、原图像素与双图同步检查，以及恢复中心实际操作。DWM 颜色属性仅支持设置，不能通过 DwmGetWindowAttribute 读回；深浅色窗口外观另经渲染截图检查。自检第一个参数为输出目录，第二个可选参数为真实 PNG 示例文件路径。