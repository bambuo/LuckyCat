# 招财猫前端

Vue 3 + TypeScript + Vite + Arco Design。开发服务器默认 `http://localhost:5173`，`/api` 与 SSE 代理到后端 `http://localhost:5210`（见 `vite.config.ts`）。

## 命令

```bash
bun install
bun run dev      # 开发服务器，HMR
bun run build    # vue-tsc 类型检查 + Vite 生产构建，产出 dist/
bun test         # 纯函数单测（SSE 帧解析）
```

## 页面

| 路由 | 页面 |
| --- | --- |
| `/traders` | 交易员列表：模型/交易所/策略绑定、启停、演练与实盘 |
| `/dashboard?trader=<id>` | 交易看板：净值、持仓、最新周期阶段/提案/裁决/订单、SSE 实时事件 |
| `/strategy` | 策略工作室：币种来源、技术指标、风控、记忆与消息面、五角色指令、预算与复盘、Prompt 预览、试运行 |
| `/cycles?trader=<id>` | 周期历史：阶段详情、工具调用与原始消息 |
| `/reviews` | 复盘经验：复盘记录、经验列表与手动触发 |
| `/providers`、`/exchanges` | 模型提供商与交易所账户配置、连接验证 |

## 实时事件

`src/sse.ts` 提供 `parseSseFrames` 与 `openTraderStream` / `openTestRunStream`。事件名：`state` / `cycle` / `stage` / `proposal` / `verdict` / `tool` / `order` / `equity` / `position-history` / `ping`。断线后重新拉取看板，不做事件重放。

接口契约以 `docs/standards/03-数据模型与接口契约规范.md` 为准；金额、数量、百分比、杠杆在 REST/SSE 中均为字符串。
