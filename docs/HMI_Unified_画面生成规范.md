# WinCC Unified 画面生成规范

本规范用于生成 WinCC Unified 画面、标签、按钮动作和动态化绑定。模板文件位于 `templates/hmi/`，均为可读、可修改、可直接应用的 `designJson`。

**HMI 变量与 PLC 对应关系（符号 / 绝对地址、红字排障）** 见 **`docs/hmi-plc-tag-binding-and-addressing.md`**（与 IDE 无关，适用于所有 MCP 客户端）。

## 生成顺序

```text
GetProjectTree
GetHmiProgramInfo
EnsureUnifiedHmiConnection
EnsureUnifiedHmiTagTable
EnsureUnifiedHmiTag
EnsureUnifiedHmiScreen
ApplyUnifiedHmiScreenDesignJson
BindUnifiedHmiTagDynamization
EnsureUnifiedHmiButtonAction
SaveProject
```

## `designJson` 契约

- 顶层键：`screen`、`items`。
- 颜色：`0xAARRGGBB`。
- 控件公共字段：`type`、`name`、`left`、`top`、`width`、`height`。
- 常用控件：`Rectangle`、`Text`、`Button`、`IOField`。
- 同屏控件名唯一。
- `EnsureUnifiedHmiScreen` 的 width/height 与模板尺寸一致。

## 模板文件

| 文件 | 尺寸 | 说明 |
|---|---:|---|
| `unified_overview_1280x800.json` | 1280 x 800 | 总览、导航、命令、状态、过程区、事件摘要 |
| `unified_basic_dashboard_1024x768.json` | 1024 x 768 | Dashboard |
| `unified_control_strip_1024x768.json` | 1024 x 768 | 控制条 |
| `unified_parameter_page_1024x768.json` | 1024 x 768 | 参数页 |
| `unified_trend_page_1024x768.json` | 1024 x 768 | 趋势页 |
| `unified_basic_tag_diagnostics_1024x768.json` | 1024 x 768 | 标签诊断 |
| `unified_basic_event_log_1024x768.json` | 1024 x 768 | 事件列表 |

## 按钮动作

已验证的高层动作：

| 动作 | 事件 | 说明 |
|---|---|---|
| `set-bit` | `Down` | 按下置位 |
| `reset-bit` | `Up` | 松开复位 |
| `toggle-bit` | `Down` 或 `Up` | 翻转位 |

推荐命令：

| 按钮 | HMI Tag |
|---|---|
| `Btn_Enable` / `Btn_Start` | `HMI_CmdEnable` |
| `Btn_Disable` / `Btn_Stop` | `HMI_CmdDisable` |
| `Btn_Reset` | `HMI_CmdReset` |
| `Btn_Apply` | `HMI_CmdApply` |

## 动态化

| 控件 | 属性 | HMI Tag |
|---|---|---|
| `Lamp_Active` / `Lamp_Run` | `BackColor` | `HMI_StatusActive` |
| `Lamp_Error` / `Lamp_Fault` | `BackColor` | `HMI_StatusError` |
| `IO_Setpoint` | `ProcessValue` | `HMI_ValueSetpoint` |
| `IO_Actual` | `ProcessValue` | `HMI_ValueActual` |
| `IO_Output` | `ProcessValue` | `HMI_ValueOutput` |
| `IO_OutputMin` | `ProcessValue` | `HMI_OutputMin` |
| `IO_OutputMax` | `ProcessValue` | `HMI_OutputMax` |
| `IO_CounterPreset` | `ProcessValue` | `HMI_CounterPreset` |

## 视觉规范

- 顶栏使用深色底，标题白色。
- 主页面背景为浅灰，卡片为白底。
- 卡片边框使用浅灰，避免高饱和大面积色块。
- 按钮高度不低于 42 px，关键按钮建议 48 px 以上。
- 状态灯使用浅绿、浅红、浅黄底色，文字使用深色。

## 验收

- `ApplyUnifiedHmiScreenDesignJson` 无不可解释失败。
- 控件读回存在。
- HMI Tag 表存在且标签可读回。
- 按钮动作通过 SyntaxCheck。
- 动态化绑定能读回或返回明确成功状态。
