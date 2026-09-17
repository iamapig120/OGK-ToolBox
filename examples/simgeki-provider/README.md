# SimGEKI 模拟提供者

用于测试工具箱控制器界面，显示 **SimGEKI（模拟）**、固件 `simulation`。
无需连接手台：每个按键依次亮起 450ms、间隔 350ms，摇杆每 8 秒往返一次。
模式按钮的 `1`（IO4）、`2`（DLL）、`3`（模拟键盘）仅修改内存并返回模拟回读结果，重启恢复 IO4。

在仓库根目录，先按主 README 安装依赖，再运行：

```powershell
$env:OGK_CONTROLLER_MODULE_DIR = (Resolve-Path './examples/simgeki-provider').Path
cd src/OGKToolBox.Electron
npm run dev
```

测试已生成的 Windows 目录包时，在仓库根目录的同一终端设置该变量。
建议用独立配置目录启动，避免改变日常使用的界面偏好和已选游戏目录：

```powershell
$profile = Join-Path (Resolve-Path './artifacts').Path 'simgeki-test-profile'
New-Item -ItemType Directory -Path $profile -Force | Out-Null
& './artifacts/electron/win-unpacked/OGK ToolBox.exe' "--user-data-dir=$profile"
```

新配置目录首次启动仍需选择 package 才能进入工作区。若只测试控制器，可创建一个临时空目录，
在其下建立 `mu3_Data/StreamingAssets/GameData/A000` 和 `mu3_Data/StreamingAssets/assets`
两级目录，再在应用内选择这个临时 package。正常扫描会生成空资源索引，之后可进入控制器页；
索引等工作数据写在该临时目录的 `Tools/OGKToolBox` 下，不需要选择真实游戏目录。

先退出已有工具箱进程，确保新进程收到环境变量。显式指定提供者后，仅启动本模拟器，
不同时启动内置 Host 或 IO4 提供者。结束后关闭工具箱并执行
`Remove-Item Env:OGK_CONTROLLER_MODULE_DIR`，从该终端重新启动即可恢复默认后端。

模块版本匹配当前工具箱 `1.1.8`；旧已发布 v1.1.7 安装包不含此加载器，需要当前源码构建。
与另一个示例相同，本目录引用 `../../sdk/controller-provider/server.cjs`，
单独分发时须保留 `examples/simgeki-provider` 与 `sdk/controller-provider` 的相对布局，
或随模块携带 SDK 并相应修改引用。不要只复制 `provider.cjs` 和 `module.json`。

没有 HID 访问、真实键盘/鼠标注入、读卡、灯光或校准；不支持的命令返回 `Rejected`。
退出会关闭动画定时器。模拟状态和模式切换不能验证 USB 协议、真实硬件、配置持久化或游戏输入。
