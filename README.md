# AinaLife.Notes — 温柔纸条插件

一个为 [Alife](https://github.com/BDFFZI/Alife)（C#/.NET 赛博生命框架，4.2.x）开发的**纸条/温柔便签**插件。

## 功能

- 📝 **留纸条**：让 AI 随时给用户留一张纸条（温柔话、提醒、心情、待办…）
- 📋 **看纸条**：列出所有已留的纸条（带时间）
- 🗑️ **删纸条**：按序号删除纸条
- 🧹 **清空**：一键清空所有纸条
- ⏰ **自动温柔纸条**：随机间隔（默认 2~5 小时）由 **AI 自主生成**一句暖心话并保存
- 🖼️ **手写便条图片**：纸条可渲染成手写便条样式图片（横线活页纸 + 红心 + 手写签名），自动发送到配置的群聊/私聊
- 💾 **持久化**：纸条存在 Alife 存储目录（`StorageSystem`），重启不丢

## 安装

1. 下载发布包（release），把解压出的 `AinaLife.Notes` 文件夹放入 Alife 的 `Plugins` 目录
2. 在客户端「系统管理 → 插件环境 → 同步环境」编译加载（会自动拉取 SkiaSharp 依赖）
3. 在角色配置（`{角色目录}/index.json` 的 `Modules`）中启用模块：`AinaLife.Notes.NotesModule`
4. 「角色设定 → 重载配置」后激活角色即可

## 配置

| 配置项 | 说明 | 默认值 |
|---|---|---|
| 自动纸条最小间隔（小时） | 随机间隔下限，0 关闭自动纸条 | 2 |
| 自动纸条最大间隔（小时） | 随机间隔上限，0 关闭自动纸条 | 5 |
| 最大纸条数量 | 最多保留多少张，超出自动丢弃最旧的 | 100 |
| 纸条发送类型 | 纸条图片发送到哪：Group=群聊，Private=私聊，None=仅存档 | None |
| 纸条发送目标 | 群号或QQ号，配合发送类型，0 表示不发送 | 0 |
| 便条签名 | 便条图片右下角的手写签名 | 爱奈丽 |

## AI 可用函数

| 函数 | 说明 |
|---|---|
| `AddNote(内容)` | 留一张纸条（配置了发送目标时同时渲染成便条图片发出） |
| `ListNotes()` | 查看所有纸条 |
| `DeleteNote(序号)` | 删除第 N 张纸条（从 1 开始） |
| `ClearNotes()` | 清空所有纸条 |

## 工作原理

- 模块基于 `ChatBehaviour` + `Interactor<T>`，通过 `XmlFunctionCaller` 向 AI 暴露函数
- 自动纸条：`OnUpdate()` 周期检查时间，到点通过 `ChatBot.ChatAsync`（`breakLast: false`，不打断当前对话）让 **AI 自主生成**纸条内容，每次生成后按配置范围随机掷出下一次触发时间，带防重入保护
- 图片渲染：SkiaSharp 绘制手写便条（楷体优先），存到 `{存储目录}/Notes/Images`，经 `QChatService.QImage` 发送
- 纸条与自动纸条时间存于 `{存储目录}/Notes/state.json`

## 开发

- 框架：Alife 4.2.x
- 依赖：`Alife.Function.FunctionCaller`、`Alife.Function.QChat`、SkiaSharp (NuGet)
- 分类：`AinaLife/实用`（第三方前缀，避开官方保留区）
