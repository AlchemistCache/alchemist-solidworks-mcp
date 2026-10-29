<p align="center"><img src="assets/banner.png" alt="Alchemist for SOLIDWORKS — 把文字变成零件" width="100%"></p>

<p align="center">
<a href="README.md">English</a> · <a href="README.ru.md">Русский</a> · <b>简体中文</b>
</p>

<p align="center">
<img src="https://img.shields.io/badge/SOLIDWORKS-2024-c8102e" alt="SOLIDWORKS 2024">
<img src="https://img.shields.io/badge/MCP-134%20tools-6b4fbb" alt="134 个 MCP 工具">
<img src="https://img.shields.io/badge/Python-3.11%2B-3776ab" alt="Python 3.11+">
<img src="https://img.shields.io/badge/license-AGPL--3.0-2ea44f" alt="AGPL-3.0">
</p>

# Alchemist for SOLIDWORKS

*把文字变成零件。* Alchemist（炼金术士）是一个 MCP 服务器，让 **Claude**（或任何 MCP 客户端）在 **SOLIDWORKS**
里工作：用日常语言描述需求即可建出零件，拆解大型装配体，为激光切割和折弯准备钣金，并在 **SOLIDWORKS Simulation**
中校核强度——同时它能 **看见** 模型：每个视图、每张应力云图都会以图片形式回到对话中。

<p align="center"><img src="assets/demo.gif" alt="Claude 设计并校核一台可 3D 打印的机械臂" width="100%"></p>
<p align="center"><sub>一次对话，不碰鼠标：Claude 把一台可 3D 打印的机械臂建模到每一颗螺钉（17 个零件、64 个组件），
让它动起来，发现肩部舵机会堵转并换掉它，在 Simulation 和 CalculiX 中校核打印件，最后出图。
完整视频：<a href="assets/demo_zh.mp4">assets/demo_zh.mp4</a></sub></p>

本项目起源于 Çağatay Bakan 的 [SolidPilot](https://github.com/eyfel/mcp-server-solidworks) 分支，工具数量从 47 个
增长到 **134 个**：装配体、钣金、焊件、带 CalculiX 交叉校核的 Simulation、GOST 工程图和明细表。

## 可以让它做什么

| 领域 | 示例 |
|---|---|
| **建模** | “做一块 120×80×10 的板，距边 10 mm 打四个 Ø9 孔，倒 3 mm 圆角”、“绕这条轴旋转这个草图”、“把这个槽绕轮毂阵列 6 个”，扫描、放样、异型孔向导、配置、自定义属性。草图会带着真实的设计尺寸 **完全定义** |
| **装配体** | “从正面显示装配体”、“查找干涉”、“哪些零件彼此距离小于 3 mm？”、“机架的质量和重心”、“把 Distance1 改成 25 mm”、“把螺栓插入板上的孔，并以 30 mm 间距阵列 4 个”、“Pack and Go 到 D:\copy” |
| **钣金** | “按厚度汇总钣金零件”、“按零件代号导出展开 DXF，带激光图层”、“在 Excel 中生成下料清单”、“检查可制造性：靠近折弯的孔、过短的法兰”、“加一个 8 mm 的褶边”、“把这个盒子转换为钣金” |
| **强度** | “校核这个支架：面 4 固定，面 9 向下 500 N，材料 AISI 1020” → 应力、位移、安全系数、最大值位置以及 **应力云图图片**；接触、螺栓和焊缝；螺栓、焊缝和梁的手算校核（EN 1993-1-8）；HTML 报告；在同一网格上用 **CalculiX** 做独立交叉校核 |
| **焊件与明细表** | “沿这个草图用 40×40×2 方管搭一个框架”、修剪、端盖、角撑板、焊件下料清单、明细表（Excel 格式的 GOST 2.106 明细表） |
| **工程图** | 视图按投影关系以标准比例排布在图纸上，尺寸放在视图之外（也可以只标你指定的特征），去除重复尺寸，剖视图、孔标注、BOM 表，导出 PDF / DXF；读取 DXF / DWG 图纸并据此重建零件 |
| **供应商模型** | “把这个文件夹里的所有 STEP 转成 SOLIDWORKS 零件” |

## 工作原理

```
Claude ⇄ MCP ⇄ 适配器 (Python, adapters/claude) ⇄ HTTP ⇄ SolidworksExecution.exe (C#) ⇄ COM ⇄ SOLIDWORKS
```

- **适配器** 向 Claude 描述工具，并在第一次调用时启动执行服务器。
- **SolidworksExecution.exe** 是一个没有窗口的后台服务器，负责调用 SOLIDWORKS API。它在安装时针对 **你本机安装的**
  SOLIDWORKS 的 API 编译，因此始终与你的版本一致。
- 默认情况下，Claude 在 **它自己的隐藏 SOLIDWORKS 实例** 中工作，不会打扰你
  （`install.ps1 -SharedInstance` 则让它在你打开的窗口中工作）。
- 所有工具都使用 **毫米和度**（Simulation：mm、N、MPa），并返回一个 JSON 对象。

## 安装

需要：Windows、**SOLIDWORKS**（在 2024 SP1 上测试；强度工具需要 SOLIDWORKS Simulation）、**Python 3.11+**，
以及 Claude Desktop 或 Claude Code。

```powershell
git clone https://github.com/AlchemistCache/alchemist-solidworks-mcp.git
cd alchemist-solidworks-mcp
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

安装程序会创建 `.venv`、安装依赖、编译 `SolidworksExecution.exe`（C# 编译器只从 nuget.org 下载一次，约 21 MB），
并在 Claude Desktop 中注册 `solidworks` 服务器（会先备份配置）。然后 **重启 Claude**。

参数：`-Test` 在运行中的 SOLIDWORKS 上做冒烟测试，`-SharedInstance`，`-NoRegister`，`-Dev`（pytest、ruff），
如果 SOLIDWORKS 不在 `C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS`，用 `-SwApiDir <SOLIDWORKS 文件夹>\api\redist`。

**更新：** `git pull`，然后再次运行 `install.ps1`（会重新编译 exe）。

## 工具

共 **134 个工具**，分组如下；带参数的完整列表见 [docs/TOOLS.md](docs/TOOLS.md)（英文）。标记为 *按需开启* 的分组
在用到之前保持关闭以节省上下文——Claude 会通过 `tool_help` 找到其中的工具，并通过 `execute_batch` 调用，
该分组随即开启（之后 30 天内默认开启）。

<!-- tools:start -->
| 分组 | 工具 |
|---|---|
| **文档与文件** (16) | `ensure_ready`, `open_new_part`, `open_document`, `export_document`, `verify_state`, `close_document`, `save_document`, `get_selection`, `set_part_material`, `activate_document`, `batch_export_files`, `unreferenced_files`, `run_macro`, `import_step`, `import_step_folder`, `set_document_units` |
| **草图** (10) | `create_sketch`, `add_sketch_entity`, `add_sketch_entities`, `sketch_modify`, `add_sketch_constraint`, `add_dimension`, `define_sketch`, `edit_sketch`, `modify_dimension`, `add_sketch_text` |
| **零件特征** (13) | `extrude_feature`, `create_rib`, `hole_wizard`, `add_edge_feature`, `add_reference_geometry`, `create_pattern`, `edit_feature`, `list_materials`, `add_plane_angle`, `add_axis_two_points`, `sweep_pipe`, `loft`, `set_body_visibility` |
| **工程图** (15) · *按需开启* | `create_drawing`, `add_drawing_view`, `add_flat_pattern_view`, `add_drawing_dimension`, `auto_dimension_drawing`, `auto_center_marks`, `add_hole_callout`, `add_section_view`, `analyze_drawing`, `check_drawing`, `move_drawing_view`, `layout_drawing_views`, `dedupe_drawing_dimensions`, `add_dimension_between`, `insert_bom_table` |
| **读取与检查模型** (4) | `analyze_model`, `compare_parts`, `compare_assemblies`, `dimension_for_face` |
| **后台任务与批处理** (2) | `job_result`, `execute_batch` |
| **装配体** (31) | `list_components`, `delete_components`, `list_open_documents`, `interference_check`, `clearance_check`, `measure_components`, `mass_properties`, `list_references`, `get_custom_properties`, `bom_export`, `bom_gost`, `set_component_state`, `set_appearance`, `move_component`, `replace_component`, `pack_and_go`, `set_custom_properties`, `configurations`, `form_subassembly`, `export_components`, `list_mates`, `edit_mate`, `add_mate_advanced`, `insert_and_mate`, `pattern_components`, `constraint_status`, `open_new_assembly`, `analyze_assembly`, `insert_component`, `add_mate`, `save_body_as_part` |
| **钣金** (10) · *按需开启* | `sheet_metal_cutlist`, `sheet_metal_hem`, `sheet_metal_miter_flange`, `sheet_metal_cross_break`, `sheet_metal_gusset`, `convert_to_sheet_metal`, `sheet_metal_info`, `export_flat_dxf`, `sheet_metal_dfm`, `sheet_metal_feature` |
| **强度：SOLIDWORKS Simulation 与手算校核** (22) · *按需开启* | `sim_status`, `sim_create_study`, `sim_delete_study`, `sim_set_material`, `sim_add_fixture`, `sim_add_load`, `sim_contact`, `sim_mesh`, `sim_run`, `sim_results`, `sim_plot`, `sim_image`, `sim_crosscheck`, `sim_connector`, `sim_contact_set`, `sim_mesh_control`, `sim_options`, `sim_copy_study`, `sim_report`, `bolt_check`, `weld_check`, `beam_check` |
| **焊件** (7) · *按需开启* | `weldment_profiles`, `create_weldment_profiles`, `weldment_member`, `weldment_cut_list`, `weldment_trim`, `weldment_end_cap`, `weldment_gusset` |
| **模型图片** (2) | `view_image`, `view_images` |
| **帮助** (1) | `tool_help` |
| **工具组** (1) | `enable_tools` |
<!-- tools:end -->

## Claude 技能（Skills）

`skills/` 中有现成的工作流程：**sheet-assembly-strength**（钣金零件或装配体的强度：约束、载荷、接触、网格、云图、
手算校核、报告）和 **sheet-metal-production**（钣金投产：可制造性、激光 DXF、下料清单、明细表）。`install.ps1`
会把它们安装到 Claude Code（`%USERPROFILE%\.claude\skills`），并在 `dist\skills` 中生成 zip——在 Claude Desktop 的
“设置 → 功能 → 技能”中上传即可。

## 使用提示

- 会话开始时先说“准备好 SolidWorks”（`ensure_ready`）——隐藏的 SOLIDWORKS 第一次启动最多需要一分钟。
- 操作所需的面和边的索引来自 `analyze_model(faces / edges)` 和 `analyze_assembly`。
- 较长的工具描述会被截短（每次请求约节省三分之一的上下文）；完整文本和主题帮助在 `tool_help` 中。
  如需始终显示完整描述：设置环境变量 `SWMCP_DOCS=full`。
- 工具分组：默认只开启基础工具和装配体；最近 30 天用过的分组会自动开启。
  设置：`adapters/claude/.env` 中的 `SWMCP_TOOLS`（`all`——全部开启）。
- 钣金在 Simulation 中按壳单元划分网格：载荷请加在钣金的大面上，不要加在厚度方向的窄边上。
- VBA 宏（`run_macro`）只能从项目的 `macros\` 文件夹运行。
- Simulation 算例保存在文档中——请保存零件 / 装配体，否则算例会丢失。
- 大型装配体上的长时间操作约 45 秒后会返回 `{"running": true, "job_id": …}` 并继续运行；Claude 用 `job_result`
  取回结果（MCP 客户端大约 60 秒会切断一次调用）。

## 故障排除

| 现象 | 处理方法 |
|---|---|
| 不确定哪里出了问题 | `.venv\Scripts\python.exe tools\doctor.py`——检查 Python 和依赖包、SOLIDWORKS 和 Simulation、exe 是否已编译且为最新、服务器、注册情况 |
| Claude 中没有 `solidworks` 工具 | 重启 Claude；检查 `tools\register_claude.py` |
| `COM_ATTACH_FAILED` | SOLIDWORKS 和 Claude 以不同权限运行（其中一个“以管理员身份”运行），或有对话框 / 许可证提示未关闭 |
| 安装时编译出错 | SOLIDWORKS 不在默认文件夹 → `install.ps1 -SwApiDir ...` |
| `SIMULATION_UNAVAILABLE` | 没有 SOLIDWORKS Simulation 许可证 / 插件 |
| 日志 | `execution\solidworks\SolidworksExecution\bin\Debug\execution.log`、`adapters\claude\adapter.log` |

## 文档（英文）

- [docs/TOOLS.md](docs/TOOLS.md)——所有工具及其参数（自动生成）
- [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md)——项目结构、如何添加工具、测试
- [docs/SOLIDWORKS_API_NOTES.md](docs/SOLIDWORKS_API_NOTES.md)——SOLIDWORKS 2024 API 的实际行为
- [CONTRIBUTING.md](CONTRIBUTING.md)——约定以及如何参与贡献
- [docs/UPSTREAM_README.md](docs/UPSTREAM_README.md)、[docs/master-architecture.md](docs/master-architecture.md)——原 SolidPilot 的文档
- [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)——取自其他分支的代码和思路

## 许可证

AGPL-3.0（见 [LICENSE](LICENSE)），与原 SolidPilot（© Çağatay Bakan）相同。SOLIDWORKS 是 Dassault Systèmes 的商标；
本项目与 Dassault Systèmes 和 Anthropic 无关，也未获得其认可。
