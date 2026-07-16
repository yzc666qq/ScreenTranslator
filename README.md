# ScreenTranslator

ScreenTranslator 是一个 Windows 透明置顶实时翻译工具。它可以框选任意屏幕区域，在本机持续截图并使用 Windows OCR 识别文字，然后把发生变化的文本发送给 DeepSeek 或其他 OpenAI 兼容模型。

## 已实现功能

- 全屏透明框选层，支持虚拟桌面和多显示器屏幕坐标。
- 本地屏幕截图与 Windows OCR；超大截图会缩放到 OCR 支持的尺寸。
- 根据 OCR 单词坐标重建相对缩进、词间距和空行。
- 自动判断源语言；OCR 语言仅作为模型线索，并支持多语言混排内容。
- 仅当识别文字发生变化时调用翻译接口，减少延迟和模型费用。
- 可编辑的 OpenAI 兼容接口地址和模型名称，默认配置 DeepSeek。
- 两种置顶显示模式：
  - **覆盖所选区域**：译文直接覆盖在原区域上，窗口点击穿透，不抢占输入焦点。
  - **独立侧边面板**：可拖动、缩放、调整透明度，也可锁定当前位置和大小。
- 译文字体和字号可在主窗口中调整，并即时同步到两种显示模式。
- 应用窗口和译文窗口从屏幕捕获中排除，避免 OCR 反复识别自身输出。

## 环境要求

- Windows 10 版本 2004（10.0.19041）或更新版本，推荐 Windows 11。
- .NET 9 SDK；使用 Visual Studio 时推荐 Visual Studio 2022 17.14 或更新版本。
- Windows 设置中至少安装一个带 OCR 支持的语言包。

## 构建与运行

```powershell
dotnet restore ScreenTranslator.sln
dotnet build ScreenTranslator.sln --configuration Debug
dotnet run --project src/ScreenTranslator.App --configuration Debug
```

## 使用方法

1. 点击“框选区域”，拖动鼠标选择要持续翻译的内容；按 `Esc` 或鼠标右键取消。
2. 选择“覆盖所选区域”或“独立侧边面板”。
3. 选择目标语言、刷新间隔、译文字体和字号；源语言会自动检测。
4. 填写模型接口、模型名称和 API 密钥。
5. 点击“开始实时翻译”。再次点击可停止，译文窗口会保留最后结果。

覆盖模式的透明度由主窗口滑块控制。侧边面板右上角的低透明度控制条会在鼠标移入时显示；可通过拖动手柄移动、窗口边缘缩放、锁定按钮固定位置，并按 `◐` 调整透明度。

## DeepSeek 配置

默认值：

- 接口：`https://api.deepseek.com/chat/completions`
- 模型：`deepseek-v4-flash`

可以直接在界面输入密钥，也可以只为当前终端会话设置环境变量：

```powershell
$env:DEEPSEEK_API_KEY = "你的密钥"
dotnet run --project src/ScreenTranslator.App --configuration Debug
```

接口和模型字段均可编辑，因此也能连接其他采用 Chat Completions 响应结构的 OpenAI 兼容服务。DeepSeek 请求会关闭思考模式以降低实时翻译延迟；其他兼容服务不会收到 DeepSeek 专用字段。

## 格式保持

Windows OCR 会提供每个单词的屏幕坐标。程序利用这些坐标重建：

- 原始行顺序和换行；
- 相对缩进与较大的词间空隙；
- 最多三行连续视觉空行。

翻译提示进一步要求模型保持空行、相对缩进、编号和标点，并且程序不会裁剪模型返回文本两端的空白。由于译文长度可能与原文不同，无法保证像素级逐字对齐，但段落和列表结构会尽量保持。

## 隐私与密钥

- 截图和 OCR 在本机完成。
- 只有 OCR 识别出的文字会在用户启动翻译后发送到所配置的模型接口。
- API 密钥仅保存在当前进程内存中，或由当前用户环境变量提供；程序不会把密钥写入仓库配置。
- 请只框选你有权发送给所选模型服务的内容。

## 验证

项目包含一个不依赖第三方测试框架的测试运行器：

```powershell
dotnet run --project tests/ScreenTranslator.Tests --configuration Debug
```

完整的屏幕截图到 OCR 端到端测试会短暂显示一个无边框测试窗口：

```powershell
dotnet run --project tests/ScreenTranslator.Tests --configuration Debug -- --screen-capture
```

测试覆盖 OCR 格式重建、框选窗口范围、两种译文窗口、侧栏锁定和透明度、DeepSeek/通用 OpenAI 请求差异、响应空白保持、Windows OCR，以及真实屏幕截图到 OCR 的链路。

## 当前边界

- 云端翻译需要用户自行提供对应服务的有效 API 密钥和账户额度。
- 翻译结果能保持段落和列表结构，但不会逐字覆盖到每个原始单词的精确位置。
- 当前版本未包含安装包、托盘图标和全局快捷键，可直接通过 `dotnet run` 或构建后的可执行文件运行。
