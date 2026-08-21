# TIA Portal Openness MCP — V18 兼容版

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT) ![TIA Portal](https://img.shields.io/badge/TIA%20Portal-V18-blue.svg) ![MCP Tools](https://img.shields.io/badge/MCP%20Tools-166-green.svg) [![Derived from](https://img.shields.io/badge/derived%20from-bulaofen0036--coder-lightgrey.svg)](https://github.com/bulaofen0036-coder/TIA_Portal_Openness_MCP)

> 本仓库为 **bulaofen0036-coder/TIA_Portal_Openness_MCP** 的 **V18 兼容衍生版本**，以 MIT 许可证发布，详见下方[上游来源与致谢](#上游来源与致谢)。

在 **Windows + TIA Portal V18** 下，通过 **MCP（stdio）** 驱动博途：建项目、加硬件、生成 PLC（Tag/UDT/DB/SCL/LAD）、生成 **Classic / Comfort HMI** 画面与标签、编译诊断、保存。包内含 **已编译运行时**、Skill、模板、能力矩阵、手册。**不要求**另行克隆源码仓库——下载本仓库根目录的 zip 解压即用。

## 快速上手（3 步）

> 照这 3 步，把 MCP 客户端连上你的 TIA Portal V18。

1. **准备**：装好 **TIA Portal V18** + **.NET Framework 4.8**；把当前 Windows 用户加入本地组 **`Siemens TIA Openness`**，注销重登一次。
2. **下载解压**：从本仓库 `master` 根目录下载 `TIA_Portal_Openness_MCP-V18.zip` 并解压到任意目录。
3. **挂载 MCP**：在 MCP 客户端（WorkBuddy / Cursor / VS Code / Claude Desktop 等）配置里，把 `command` 指向解压目录内的
   `tools\tiaportal-mcp\src\TiaMcpServer\bin-v18\Release\net48\TiaMcpServer.exe`，
   `args` 传 `["--tia-major-version","18","--logging","0"]`，信任该连接器并重启客户端。连接器将暴露 **166 个 V18 安全工具**。

---

## 上游来源与致谢

本项目是 [`bulaofen0036-coder/TIA_Portal_Openness_MCP`](https://github.com/bulaofen0036-coder/TIA_Portal_Openness_MCP) 的 **V18 兼容衍生版本**。

- 上游原作由 **bulaofen0036-coder** 维护，采用 **MIT 许可证**（截至核对时 144 stars），定位为「用任意 MCP 客户端驱动 Siemens TIA Portal V20/V21（STEP 7、WinCC Unified）」。
- 本仓库在其基础上，针对 **TIA Portal V18** 做了兼容性移植：将 WinCC Unified HMI 的 23 个工具以 `#if !TIA_V18` 守卫隐藏，使其在 V18 环境稳定构建与运行，并暴露 **166 个 V18 安全工具**。
- 上游的 **MIT 许可证与版权声明**已随工具包 `TIA_Portal_Openness_MCP-V18.zip` 一并保留（见压缩包内 `LICENSE`）。本衍生版本同样以 MIT 许可证发布；如上游作者另有要求，以原作者声明为准。

> 致谢：感谢 **bulaofen0036-coder** 开源的 TIA Portal Openness MCP 原作，为本 V18 移植分支提供了基础与参考。

---

## V18 兼容说明（本分支重点）

本分支针对 **TIA Portal V18** 做了兼容性处理：

- **WinCC Unified HMI 在 V18 不可用**：V18 的 Openness 全安装不含 `Siemens.Engineering.HmiUnified` 程序集。相关 **23 个 Unified HMI 工具**已用 `#if !TIA_V18` 条件编译守卫在 V18 构建中隐藏（方法体保留，仅不注册为 MCP 工具，从 `tools/list` 中消失）。
- **暴露工具数**：连接器实测 **166 个 V18 安全工具**（基线 189 − 23 个 Unified）。
- **深度审计结论**：除这 23 个 Unified 工具外，其余暴露工具在 V18 全部安全可用；V20 专属文档工具（`Export/Import*Documents`）在本构建中仅以引导提示暴露、不可调用。
- **HMI 自动化路径**：V18 下请走 **Classic / Comfort HMI** 工具族；Unified 需求须使用 V20/V21 构建。

---

## 上手步骤

1. **环境准备**
   - .NET Framework **4.8**、**TIA Portal V18** 已安装；
   - 当前用户加入 **`Siemens TIA Openness`** 本地组，注销重登；
   - 定位博途安装根（任选其一）：
     a) 启动 exe 时传 `--tia-portal-location "C:/Program Files/Siemens/Automation/Portal V18"`（推荐，非标准安装位置必用）；
     b) 用户环境变量 `TiaPortalLocation` 指向博途安装根；
     c) 让程序自动从注册表读取。
   - 首次连接时在 TIA 弹窗中授权 **Openness**。

2. **挂载 MCP（手动配置，最稳）**
   复制压缩包内的 `.mcp.json` 片段（或参考下例），把 `command` 指向本包内的 V18 exe：

   ```json
   {
     "mcpServers": {
       "tia-portal": {
         "command": "C:/path/to/unzip/tools/tiaportal-mcp/src/TiaMcpServer/bin-v18/Release/net48/TiaMcpServer.exe",
         "args": ["--tia-major-version", "18", "--logging", "0", "--with-ui"],
         "env": { "TiaPortalLocation": "C:/Program Files/Siemens/Automation/Portal V18" }
       }
     }
   }
   ```

   （若使用受管 MCP 客户端，升级 `TiaMcpServer.exe` 前需先在连接器配置中将 `disabled` 设为 `true` 释放文件锁，复制完成后再恢复。同款 exe 也支持 `TiaMcpServer.exe config` 尝试自动发现并写入宿主配置，手动配置更稳。）

3. **首次调用顺序**
   - `Bootstrap` → `Connect` → `OpenProject`（或 `CreateProject`）→ `GetProjectTree`，从树中读取真实的 `PLC_xxx` / `HMI_RT_xxx` 路径再继续。

---

## 能力范围与边界

**可做**：工程与硬件组态、PROFINET、PLC 声明式导入（Tag/UDT/DB/SCL/LAD）、Classic / Comfort HMI 连接/变量/画面、交叉引用与影响面分析、批量分组与自动分类、编译诊断、保存。

**V18 下不可做**：
- WinCC **Unified** HMI（23 个工具已隐藏，V18 Openness 不含 `HmiUnified` 程序集）——需 V20/V21。
- S7DCL 人类可读 SCL 文本导出（`Export/Import*Documents` 系列为 V20+ 专属，本构建仅以引导提示暴露、不可调用）。可改走 `ExportBlockSourceUtf8` + `RegenerateBlockFromSource`（SimaticML XML，强制 UTF-8+BOM 编码安全）。

**包内不含**：西门子安装介质、现场导出工程、业务专用工艺。

---

## 版本对照

| 能力 | V18 本分支 | V20 / V21（上游主线） |
|------|-----------|------------------------|
| 通用 PLC / Classic HMI 工具 | ✅ | ✅ |
| WinCC Unified HMI 工具 | ❌（已守卫隐藏） | ✅ |
| 文档导入导出（`*Documents` / S7DCL） | ⚠️ 仅引导提示 | ✅ 可调用 |
| 暴露工具数 | **166** | ~180–189 |

---

## 构建（如需自行编译 V18）

```bat
dotnet build TiaMcpServer.V18.csproj -c Release ^
  -p:TiaPortalLocation="C:/Program Files/Siemens/Automation/Portal V18" ^
  -p:BaseOutputPath=bin-v18/ -p:BaseIntermediateOutputPath=obj-v18/
```

`TiaMcpServer.V18.csproj` 已定义 `TIA_V18` 编译符号，自动隐藏 Unified HMI 工具。

---

## 交付内容

`master` 分支根目录提供：

```
TIA_Portal_Openness_MCP-V18.zip   ← 完整工具（约 26 MB，663 个文件）
```

压缩包内含：已编译运行时 `bin-v18/`、源码 `src/`、文档 `docs/`、模板 `templates/`、Skill、能力矩阵 `manifest/`、配置 `.mcp.json`、以及更详细的 `README.md` 与 `LICENSE`。解压后所有文档与模板均在包内，无需另行克隆源码仓库。

> 本仓库以「打包交付」形式提供，不单独提交源码树，以避免与构建产物混淆。需要源码请解压该 zip。

---

## 文档地图（压缩包内）

| 路径 | 说明 |
|------|------|
| `tools/tiaportal-mcp/skill/SKILL.md` | 主规范：工具分层、参数陷阱、HMI schema、LAD/SCL 边界 |
| `manifest/tools-list.json` | 静态工具名与层级；运行时权威列表以连上后的 `tools/list` 为准 |
| `docs/tool-capability-matrix.md` | 能力矩阵（静态索引） |
| `docs/scl-instruction-library.md` / `docs/lad-instruction-library.md` | SCL / LAD 指令库 |
| `docs/hmi-plc-tag-binding-and-addressing.md` | HMI↔PLC 绑定与寻址 |
| `templates/plc/` · `templates/hmi/` | PLC / HMI 模板索引 |
| `手册/` | 速启、Openness 限制、错误模型等 |

---

## 常见问题

**Q：连接器只显示部分工具 / 看不到某个工具？**
属于客户端工具描述符/缓存问题，不是交付包裁剪能力；重启客户端或清空工具缓存即可。运行时权威列表以 `tools/list` 为准。

**Q：为什么没有 WinCC Unified？**
TIA Portal V18 的 Openness 全安装不含 `Siemens.Engineering.HmiUnified` 程序集，Unified HMI 工具在 V18 构建中被 `#if !TIA_V18` 守卫隐藏。V18 下请走 Classic / Comfort HMI 工具族；Unified 需求使用上游 V20/V21 构建。

**Q：升级 `TiaMcpServer.exe` 时被「文件被占用」？**
受管 MCP 连接器的进程由宿主托管，会被立即重生。升级前先在连接器配置中将 `disabled` 设为 `true` 释放文件锁，复制完成后再恢复。

**Q：与 IDE 无关吗？**
是。凡支持 MCP 的客户端（WorkBuddy、Cursor、VS Code、Claude Desktop、自研 HTTP 客户端等）均可使用同一 `TiaMcpServer.exe`。

---

## 许可证

本衍生版本以 **MIT 许可证**发布。上游原作的 MIT 许可证与版权声明见压缩包内 `LICENSE`，请在使用与再分发时一并保留。
