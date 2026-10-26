# 提示词与交易风控

> 旧单轮链路说明已失效。现行规则以 [LC-SPEC-001](superpowers/specs/2026-10-25-招财猫多智能体重构-设计.md)、[LC-STD-02](standards/02-工具契约与交易安全规范.md) 与 [LC-STD-04](standards/04-提示词与记忆规范.md) 为准；本文只做现状索引。

## 角色与提示词

- 五个角色：分析师、交易员、风控官、执行员、复盘员；交易员与复盘员之外的角色不注入账户或记忆。
- 系统指令按固定块拼接：身份、职责边界、策略摘要、角色追加指令（≤2,000 字）、工具规则、记忆/经验、输出契约、安全块；安全块必须在末尾且不可被策略覆盖。
- 模型调用走 `ChatClientAgent.RunAsync(string)` + 纯文本 JSON 解析；不使用框架 `RunAsync<T>`（DeepSeek 不支持其强制的 `json_schema`）。
- 交易员契约失败重试 1 次；其他阶段失败直接收尾，不重试超时。

## 交易风控

- `open` 必须取得 `approved` 裁决且未过期，参数在批准边界内；`close` 是降险动作，跳过风控裁决。
- 执行层在账户锁内重读账户并重跑风控，再经过写工具 12 步硬闸；模型不能指定账户或交易员。
- 任何无法确认裁决的路径 fail-closed：周期 `failed`、不写裁决、不下单。
- 风控只能收紧不能放宽；行情年龄 ≤3 分钟、有效期 `now ≤ validUntilUtc` 才允许提交。
- 阶段自身超时记 `stage_timeout` 并落真实耗时；`risk_stage_failed` 只表示风控阶段非超时异常。

## 记忆与消息面

- 策略工作室「记忆与消息面」两个开关：历史决策记忆（最近 8 条提案/裁决/执行结果）与实时消息面（`get_news`）。
- 新闻由 11 个内置源提供，策略不再配置自定义外部源；`newsEnabled=false` 时 `get_news` 不拉取，Agent 也拿不到新闻。
- 经验注入最近 3 份复盘，与提案记忆合计受上下文预算限制；所有外部数据按不可信内容包裹。

## 验证

- `POST /api/strategies/preview-prompts`：五角色提示词预览，不调用模型。
- `POST /api/strategies/test-run`：合成账户运行的完整工作流演练，不触达交易所写接口。
- 后端回归：`dotnet test Tests/LuckyCat.Tests.csproj`；前端构建：`cd web && bun run build`。
