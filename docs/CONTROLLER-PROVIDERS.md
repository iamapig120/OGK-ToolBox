# 接入其他控制器

控制器扩展采用独立进程接口。开发者实现硬件适配器，返回统一状态并处理命令。
本文介绍模块启动、状态结构和应用层通信 API。SDK 继续支持新增控制器；
内置 IO4 提供者也使用这套进程通信框架。原 C# Host 保留自己的实现，并遵守同一应用层 v1 约定。

## 启动示例

在仓库根目录执行（先按 README 安装 Electron 项目依赖）：

```powershell
$env:OGK_CONTROLLER_MODULE_DIR = (Resolve-Path ./examples/controller-provider).Path
cd src/OGKToolBox.Electron
npm run dev
```

示例显示只读的 `Example Controller (simulation)`，没有实际硬件访问、按键注入或配置写入。
结束测试后用 `Remove-Item Env:OGK_CONTROLLER_MODULE_DIR` 清除当前终端的选择，重新启动应用即恢复默认模块。
变量也适用于从该终端启动的、包含本次源码变更的安装版；先退出已有应用进程。
已发布的旧版 v1.1.7 安装包尚不包含此扩展加载器。

未设置变量时，工具箱在注册的后端中选择当前展示和操作的对象；当前在线选择不会被新出现的设备抢占，
也可以在界面中手动选择。一个快照只代表当前选中的后端，不会合并多个设备的输入。
内置后端可以同时运行以发现设备；普通配置命令只发给当前后端，重新扫描及释放输入等操作可覆盖所有运行中的后端。

设置 `OGK_CONTROLLER_MODULE_DIR` 后进入独占的外部模块模式，只启动指定的提供者，
不再启动内置 Host 或 IO4 提供者。这样可避免测试适配器与内置实现同时打开同一个设备。
若一个外部模块需要支持多个型号，由该模块自行完成识别和路由。

## 开发自己的模块

1. 复制 `examples/controller-provider` 并保留对 `sdk/controller-provider` 的引用，或将 SDK 随模块一起分发。
2. 修改 `module.json` 的 `moduleId`、`moduleVersion`，保留应用 API 版本 1。
3. 实现 `sdk/controller-provider/index.d.ts` 中的 `ControllerAdapter`：`snapshot()`、`command()`、`releaseAll()`，以及可选的 `close()`。
4. 将自己的 HID、串口或其他设备代码放在适配器中。后台读取硬件，`snapshot()` 只返回完整的缓存状态。
5. 先通过 `OGK_CONTROLLER_MODULE_DIR` 验证状态和生命周期，再逐项实现命令、读回和界面所需的能力。

SDK 仅使用 Node 内置模块；`runtime: "node"` 的 `.cjs` 入口由 Electron 的 Node 模式启动，
不需要终端 PATH 上另装 Node。原生 `.exe` 可使用 `runtime: "native"`，自行实现下述 HTTP 接口。
入口必须是模块目录中的普通文件名，不能是路径、命令行或目录外的链接。
发布适配器时需包含其依赖文件；不要依赖维护者本机的 SDK 路径。

### 选择硬件访问方式

| 已有实现 | 接入方式 | 需要补充的工作 |
| --- | --- | --- |
| Node HID / 串口代码 | 在 Node 适配器中调用设备库 | 将运行时依赖安装在模块目录，并验证目标 Electron 与 Windows 架构下的加载和分发 |
| MU3IO 或其他 DLL | 用原生适配进程调用 DLL，再实现下面的 HTTP/SSE 接口 | 按 DLL 的实际导出和位数映射状态；DLL 不提供的配置功能不能由接口自动补出 |
| C# / C++ 等现有设备程序 | 提供 `runtime: "native"` 的 EXE | 实现握手、快照、命令、退出处理，并一起分发其运行依赖 |

SDK 自身不包含硬件驱动。内置 IO4 实现使用 `node-hid`，外部模块仍需声明和携带自己的依赖；
不要假定它能访问工具箱安装目录里的 `node_modules`。例如在自己的 Node 模块目录执行
`npm install --save-exact node-hid@3.4.0`，并把适配器、SDK 及生产依赖一起分发。
该版本提供 Node-API 预编译件，但是否可用仍需在目标运行时、架构和安装布局中验证；
采用其他原生扩展时，按其要求处理预编译件或重新构建。

目前没有“把任意 MU3IO DLL 放入目录即可使用”的通用加载器。已有 DLL 可以复用，
但仍需适配进程；工具箱显示输入和配置设备，也不会自动替代游戏侧的输入驱动。

```json
{
  "moduleId": "your-controller",
  "moduleVersion": "0.1.0",
  "moduleApiVersion": 1,
  "ogkToolBoxVersion": "1.1.7",
  "platform": "win-x64",
  "runtime": "node",
  "entryPoint": "provider.cjs"
}
```

`ogkToolBoxVersion` 当前要求与应用精确匹配；升级前应重测并更新清单。
第三方模块使用独立的模块标识和版本号。`Leonardo` / `Pico` 为现有硬件的保留类型；新硬件请使用自己的类型标识。

## 状态与功能声明

完整结构以 `src/OGKToolBox.Electron/src/controller-models.ts` 为准，示例包含每个必需字段。
`identity.kind` 可以使用自己的标识，`Unknown` 表示没有设备；`displayName` 为显示名称。
原硬件类型 `Leonardo`、`Pico` 是保留值。`input.mappedLever` 的 UI 范围是 0–1023。
无卡时 `card.identifier` 必须为空；不要记录真实卡号或无关设备标识。

只声明实际实现的 `capabilities`。只读模块使用 `canWrite: false`；已完成必要同步才设置
`readbackComplete: true`。写入后必须更新对应状态及配置 revision，不能用 `Verified` 表示仅已发送报文。
不支持的命令返回 `Rejected`。设备断开、异常或退出时，`releaseAll()` 必须能够反复调用并释放所有合成输入。

当前通用 UI 可显示设备名、输入监视，复用已有模式、基础亮度及摇杆能力；
磁轴与多区域灯光等部分界面仍含原硬件专用逻辑。仅设置 capability 不会自动生成新配置界面。
新硬件特有功能应在 fork 中按既有 Electron 组件样式添加页面和相应 API，不能复用不存在的硬件语义。
对游戏的输入驱动配置也由新硬件开发者自行提供；接口不会替第三方写入 NYAGEKI_IO 配置。

### 可选输入模式

需要多种输入模式的提供者可以在快照中增加：

```json
{
  "inputModes": {
    "current": "native",
    "options": [
      { "id": "native", "label": "原生输入" },
      { "id": "keyboard", "label": "模拟键盘" }
    ]
  }
}
```

`id` 是提供者定义的稳定字符串，`label` 用于显示；`current` 应对应一个有效选项。
同时声明已实现的模式能力，配置可写时界面按选项调用 `input-mode`，请求体为 `{ "modeId": "native" }`。
提供者须验证 ID，完成设备写入与读回后更新 `inputModes.current` 和配置 revision。

这是 v1 的可选扩展。没有 `inputModes` 的旧提供者继续使用 `mode` 的
`{ "keyboardMouse": false }` / `{ "keyboardMouse": true }` 语义，不需要增加新命令。
仅设置模式选项不会为灯光、磁轴等其他硬件特性生成专用设置页。

## 原生进程通信协议

应用启动入口并传入：`--port`、`--session-token`、`--instance-id`、`--parent-pid`、
`--software-version`、`--manifest`。令牌每次应用启动随机生成，不得硬编码、记录或发送到外部。
仅监听指定的 `127.0.0.1` 端口；准备好后 stdout 输出一行：

```text
OGK_CONTROLLER_READY {"address":"http://127.0.0.1:<指定端口>","instanceId":"<传入值>","moduleApiVersion":1}
```

每个请求都必须校验 `X-OGK-Controller-Session`。拒绝浏览器 Origin，不启用 CORS，不返回重定向。
`GET /health` 返回 `instanceId`、`moduleApiVersion: 1`、`ogkToolBoxVersion`。
`GET /api/v1/snapshot` 返回完整快照。
`GET /api/v1/stream` 使用 SSE：`data: <完整快照 JSON>\n\n`，持续推送，建议每 50–100ms 一次。
父进程退出后释放输入并停止服务。提供的 SDK 已实现上述传输、会话检查和退出处理。

所有命令使用 `POST /api/v1/commands/<名称>`，JSON body，响应为
`{ commandId, status, message, snapshot }`。应用命令超时通常为 5 秒；长任务返回 `Accepted` 并通过快照跟踪进度。
`release-all` 必须快速完成实际释放并返回 `Verified`；当前释放请求的超时为 500ms，
不能用 `Accepted` 代替释放确认。切换后端前，若原运行中提供者未确认释放，工具箱会保留原选择。
Node SDK 在 `releaseAll()` 成功返回后生成 `Verified` 响应；释放失败应抛出错误，不能静默吞掉。
没有合成输入的只读适配器也需提供此方法，可立即完成。
`shutdown` 也必须快速响应，应用退出时仅给予较短的宽限时间，随后可能终止进程。

| 命令 | 请求体 |
| --- | --- |
| `rescan`, `retry-sync`, `release-all`, `hall-query`, `device-query`, `bootloader`, `shutdown` | 空对象或空 body |
| `virtual-key` | `{ key: string, pressed: boolean }`，键名取自界面按钮与输入字段映射 |
| `mode` | `{ keyboardMouse: boolean }` |
| `input-mode` | `{ modeId: string }`，仅用于声明了 `inputModes` 的提供者 |
| `brightness` | `{ brightness: 0..255 }` |
| `custom-color` | `{ red: 0..255, green: 0..255, blue: 0..255 }` |
| `pico-lighting` | `PicoLightingRequest`，仅用于实现同等功能的适配器 |
| `hall` | `HallRequest` |
| `lever` | `LeverRequest` |
| `hall-calibration` | `{ action: "baseline" / "start" / "stop" }` |
| `lever-calibration` | `{ action: "left" / "right" / "stop" }`；保留硬件另有 `start` / `complete` 流程 |

请求类型和数值范围见 `controller-models.ts` 与 `electron/controller-module-manager.ts`。
模块必须自行验证参数、能力、设备状态和命令顺序，不能把 UI 检查当成硬件安全校验。

## 接入验收

模拟示例用于验证进程握手、状态、命令与退出流程，不访问真实设备。接入硬件时至少检查：

1. 无设备、插入、拔出、重新插入时，身份与缓存输入正确更新，旧按键状态不残留。
2. 每个受支持的输入和模式映射正确；没有实现的命令返回 `Rejected`。
3. 配置写入、保存、读回分别处理失败；失败不能显示为已验证成功。
4. 切换后端及窗口失焦时释放合成输入；应用退出或提供者异常清理时关闭设备。未选中的后端仍可保持连接用于发现和状态更新。
5. 从模块分发目录以及打包后的工具箱启动成功，不依赖开发机路径或未分发依赖。

测试记录应注明使用的是模拟器还是真实设备、硬件/固件版本和开发态或安装态。
本指南不宣称尚未测试的第三方手台已经兼容。
