-- ============================================================================
-- 招财猫 LuckyCat 多智能体链路 · SQLite 建表与存量库补列脚本
-- 生成日期：2026-10-26
-- 生成方式：由 EF Core 模型机械生成（AppDbContext.Database.GenerateCreateScript()），
--           CREATE TABLE / CREATE INDEX 统一加 IF NOT EXISTS，保证空库/重复执行幂等。
--
-- 表清单（应用模型共 14 张表；EnsureCreated 只对空库生效，不会给存量库补表/补列）：
--   [本次新建 6] TradingCycles / AgentStageRuns / ToolInvocations /
--                TradeProposals / RiskVerdicts / TraderLessons
--   [存量 8]     Providers / ExchangeAccounts / Strategies / Traders /
--                AccountEquitySamples / TraderOrders / AccountPositionSnapshots /
--                PositionHistories
--   旧链路 DecisionRecords 已从模型删除（可能仍存在于存量库，应用不再读写；
--   如需清理见第 4 节，默认不删数据）。
--
-- 本脚本分四节，必须按顺序执行：
--   1. CREATE TABLE IF NOT EXISTS —— 新建 6 张表；存量 8 张表为 no-op。
--   2. ALTER TABLE —— 仅存量库需要：Providers 补 2 列，TraderOrders 补 12 列并删除 Success。
--   3. CREATE INDEX IF NOT EXISTS —— 必须在第 2 节之后执行（索引引用了新增列）。
--   4. 可选清理旧链路 DecisionRecords（默认注释，不自动删数据）。
--
-- 如何应用（务必先停服务并备份；应用运行中写库会与迁移竞争）：
--   cp luckycat.db luckycat.db.bak-$(date +%Y%m%d%H%M%S)
--   sqlite3 luckycat.db < docs/sql/2026-10-26-多智能体新表.sql
--
-- 注意：
--   * 第 2 节是「一次性」语句：SQLite 的 ADD COLUMN 不支持 IF NOT EXISTS，
--     重复执行会报 duplicate column name / no such column（Success 已删）。逐条执行并跳过
--     已完成的语句即可，不影响第 1/3 节的幂等性。
--   * TraderOrders.Success 是旧链路的 NOT NULL 列，新模型不再写入；不删除它会导致
--     新订单 INSERT 触发 NOT NULL 约束。DROP COLUMN 需要 SQLite >= 3.35
--     （macOS 自带 sqlite3 与 Microsoft.Data.Sqlite 内置 e_sqlite3 均满足）。
--   * TraderOrders.Simulated 是 NOT NULL 新列，补列时必须带 DEFAULT 0。
--   * 全新空库不需要本脚本：直接启动应用，EnsureCreated 会按当前模型建表。
-- ============================================================================

-- ----------------------------------------------------------------------------
-- 1. 建表（幂等）
-- ----------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS "AccountEquitySamples" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_AccountEquitySamples" PRIMARY KEY AUTOINCREMENT,
    "ExchangeAccountId" INTEGER NOT NULL,
    "At" TEXT NOT NULL,
    "Equity" TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS "AccountPositionSnapshots" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_AccountPositionSnapshots" PRIMARY KEY AUTOINCREMENT,
    "ExchangeAccountId" INTEGER NOT NULL,
    "At" TEXT NOT NULL,
    "Symbol" TEXT NOT NULL,
    "Side" TEXT NULL,
    "Quantity" TEXT NOT NULL,
    "EntryPrice" TEXT NULL,
    "Leverage" TEXT NULL
);

CREATE TABLE IF NOT EXISTS "AgentStageRuns" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_AgentStageRuns" PRIMARY KEY,
    "CycleId" TEXT NULL,
    "TraderId" INTEGER NULL,
    "Role" TEXT NOT NULL,
    "Attempt" INTEGER NOT NULL,
    "Status" TEXT NOT NULL,
    "ProviderName" TEXT NOT NULL,
    "Model" TEXT NOT NULL,
    "Instructions" TEXT NOT NULL,
    "TranscriptJson" TEXT NOT NULL,
    "OutputJson" TEXT NULL,
    "Reasoning" TEXT NULL,
    "InputTokens" INTEGER NULL,
    "OutputTokens" INTEGER NULL,
    "ElapsedMs" INTEGER NOT NULL,
    "ToolCallCount" INTEGER NOT NULL,
    "Error" TEXT NULL,
    "Truncated" INTEGER NOT NULL,
    "CreatedAt" TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS "ExchangeAccounts" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_ExchangeAccounts" PRIMARY KEY AUTOINCREMENT,
    "ExchangeType" TEXT NOT NULL,
    "AccountName" TEXT NOT NULL,
    "ApiKey" TEXT NOT NULL,
    "ApiSecret" TEXT NOT NULL,
    "Passphrase" TEXT NULL,
    "UserId" TEXT NULL,
    "CreatedAt" TEXT NOT NULL,
    "UpdatedAt" TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS "PositionHistories" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_PositionHistories" PRIMARY KEY AUTOINCREMENT,
    "ExchangeAccountId" INTEGER NOT NULL,
    "DedupeKey" TEXT NOT NULL,
    "Source" TEXT NOT NULL,
    "Symbol" TEXT NOT NULL,
    "Side" TEXT NULL,
    "Quantity" TEXT NULL,
    "EntryPrice" TEXT NULL,
    "ExitPrice" TEXT NULL,
    "RealizedPnl" TEXT NOT NULL,
    "Fee" TEXT NULL,
    "Leverage" TEXT NULL,
    "OpenTime" TEXT NULL,
    "CloseTime" TEXT NOT NULL,
    "SyncedAt" TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS "Providers" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_Providers" PRIMARY KEY AUTOINCREMENT,
    "Name" TEXT NOT NULL,
    "Type" TEXT NOT NULL,
    "BaseUrl" TEXT NOT NULL,
    "ApiKey" TEXT NULL,
    "Model" TEXT NOT NULL,
    "ApiVersion" TEXT NULL,
    "ThinkingMode" TEXT NOT NULL,
    "ContextTokens" INTEGER NOT NULL,
    "MaxOutputTokens" INTEGER NOT NULL,
    "AutoCompressContext" INTEGER NOT NULL,
    "ToolCallingSupported" INTEGER NULL,
    "ToolCallingVerifiedAt" TEXT NULL,
    "CreatedAt" TEXT NOT NULL,
    "UpdatedAt" TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS "RiskVerdicts" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_RiskVerdicts" PRIMARY KEY,
    "CycleId" TEXT NOT NULL,
    "ProposalId" TEXT NOT NULL,
    "Decision" TEXT NOT NULL,
    "ReasonsJson" TEXT NOT NULL,
    "MaxPositionPct" TEXT NULL,
    "MaxLeverage" TEXT NULL,
    "EntryZoneMin" TEXT NULL,
    "EntryZoneMax" TEXT NULL,
    "AllowMarket" INTEGER NULL,
    "ValidUntilUtc" TEXT NULL,
    "RawJson" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS "Strategies" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_Strategies" PRIMARY KEY AUTOINCREMENT,
    "Name" TEXT NOT NULL,
    "Description" TEXT NULL,
    "IsActive" INTEGER NOT NULL,
    "ConfigJson" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "UpdatedAt" TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS "ToolInvocations" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_ToolInvocations" PRIMARY KEY AUTOINCREMENT,
    "CycleId" TEXT NULL,
    "StageRunId" TEXT NOT NULL,
    "ToolName" TEXT NOT NULL,
    "ArgumentsJson" TEXT NOT NULL,
    "ResultJson" TEXT NOT NULL,
    "Success" INTEGER NOT NULL,
    "ErrorCode" TEXT NULL,
    "ElapsedMs" INTEGER NOT NULL,
    "Truncated" INTEGER NOT NULL,
    "CreatedAt" TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS "TradeProposals" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_TradeProposals" PRIMARY KEY,
    "CycleId" TEXT NOT NULL,
    "TraderId" INTEGER NULL,
    "Action" TEXT NOT NULL,
    "Symbol" TEXT NULL,
    "Side" TEXT NULL,
    "Confidence" TEXT NULL,
    "EntryPrice" TEXT NULL,
    "StopLossPrice" TEXT NULL,
    "TakeProfitPrice" TEXT NULL,
    "PositionPct" TEXT NULL,
    "Leverage" TEXT NULL,
    "Thesis" TEXT NOT NULL,
    "Invalidation" TEXT NULL,
    "ReviewChecklistJson" TEXT NULL,
    "RawJson" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS "TraderLessons" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_TraderLessons" PRIMARY KEY AUTOINCREMENT,
    "TraderId" INTEGER NOT NULL,
    "StageRunId" TEXT NOT NULL,
    "PeriodStart" TEXT NOT NULL,
    "PeriodEnd" TEXT NOT NULL,
    "Summary" TEXT NOT NULL,
    "StrengthsJson" TEXT NOT NULL,
    "MistakesJson" TEXT NOT NULL,
    "RulesJson" TEXT NOT NULL,
    "EvidenceJson" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS "TraderOrders" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_TraderOrders" PRIMARY KEY AUTOINCREMENT,
    "TraderId" INTEGER NOT NULL,
    "At" TEXT NOT NULL,
    "Symbol" TEXT NULL,
    "Action" TEXT NOT NULL,
    "Quantity" TEXT NULL,
    "Price" TEXT NULL,
    "StopLossPrice" TEXT NULL,
    "TakeProfitPrice" TEXT NULL,
    "Status" TEXT NULL,
    "CycleId" TEXT NULL,
    "ExchangeAccountId" INTEGER NULL,
    "ProposalId" TEXT NULL,
    "VerdictId" TEXT NULL,
    "ClientOrderId" TEXT NULL,
    "Side" TEXT NULL,
    "FilledQuantity" TEXT NULL,
    "AveragePrice" TEXT NULL,
    "Leverage" TEXT NULL,
    "Simulated" INTEGER NOT NULL,
    "ExchangeOrderId" TEXT NULL,
    "Note" TEXT NULL,
    "Error" TEXT NULL
);

CREATE TABLE IF NOT EXISTS "Traders" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_Traders" PRIMARY KEY AUTOINCREMENT,
    "Name" TEXT NOT NULL,
    "ProviderId" INTEGER NOT NULL,
    "ExchangeAccountId" INTEGER NOT NULL,
    "StrategyId" INTEGER NOT NULL,
    "IsRunning" INTEGER NOT NULL,
    "ExecutionMode" TEXT NOT NULL,
    "IsCrossMargin" INTEGER NOT NULL,
    "ScanIntervalMinutes" INTEGER NOT NULL,
    "InitialBalance" TEXT NULL,
    "StartedAt" TEXT NULL,
    "LastCycleAt" TEXT NULL,
    "LastCycleStatus" TEXT NULL,
    "CreatedAt" TEXT NOT NULL,
    "UpdatedAt" TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS "TradingCycles" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_TradingCycles" PRIMARY KEY,
    "TraderId" INTEGER NULL,
    "StrategyId" INTEGER NOT NULL,
    "ProviderId" INTEGER NULL,
    "ExchangeAccountId" INTEGER NULL,
    "Mode" TEXT NOT NULL,
    "Trigger" TEXT NOT NULL,
    "Status" TEXT NOT NULL,
    "StartedAt" TEXT NOT NULL,
    "CompletedAt" TEXT NULL,
    "ErrorCode" TEXT NULL,
    "Error" TEXT NULL,
    "StrategyConfigJson" TEXT NOT NULL,
    "MemoryFingerprint" TEXT NOT NULL,
    "InputTokens" INTEGER NULL,
    "OutputTokens" INTEGER NULL,
    "ElapsedMs" INTEGER NULL
);

-- ----------------------------------------------------------------------------
-- 2. 存量库补列 / 删列（仅对已存在的旧库执行一次）
-- ----------------------------------------------------------------------------

-- Providers：工具调用能力与验证时间（API-09 能力闸）
ALTER TABLE "Providers" ADD COLUMN "ToolCallingSupported" INTEGER NULL;
ALTER TABLE "Providers" ADD COLUMN "ToolCallingVerifiedAt" TEXT NULL;

-- TraderOrders：周期归属、提案/裁决关联、幂等键与生命周期状态
ALTER TABLE "TraderOrders" ADD COLUMN "Status" TEXT NULL;
ALTER TABLE "TraderOrders" ADD COLUMN "CycleId" TEXT NULL;
ALTER TABLE "TraderOrders" ADD COLUMN "ExchangeAccountId" INTEGER NULL;
ALTER TABLE "TraderOrders" ADD COLUMN "ProposalId" TEXT NULL;
ALTER TABLE "TraderOrders" ADD COLUMN "VerdictId" TEXT NULL;
ALTER TABLE "TraderOrders" ADD COLUMN "ClientOrderId" TEXT NULL;
ALTER TABLE "TraderOrders" ADD COLUMN "Side" TEXT NULL;
ALTER TABLE "TraderOrders" ADD COLUMN "FilledQuantity" TEXT NULL;
ALTER TABLE "TraderOrders" ADD COLUMN "AveragePrice" TEXT NULL;
ALTER TABLE "TraderOrders" ADD COLUMN "Leverage" TEXT NULL;
ALTER TABLE "TraderOrders" ADD COLUMN "Simulated" INTEGER NOT NULL DEFAULT 0;
ALTER TABLE "TraderOrders" ADD COLUMN "ExchangeOrderId" TEXT NULL;

-- 旧链路 Success 列：新模型只认 Status 生命周期，保留该 NOT NULL 列会让新插入失败。
ALTER TABLE "TraderOrders" DROP COLUMN "Success";

CREATE INDEX IF NOT EXISTS "IX_AccountEquitySamples_ExchangeAccountId" ON "AccountEquitySamples" ("ExchangeAccountId");

CREATE INDEX IF NOT EXISTS "IX_AccountPositionSnapshots_ExchangeAccountId" ON "AccountPositionSnapshots" ("ExchangeAccountId");

CREATE INDEX IF NOT EXISTS "IX_AgentStageRuns_CreatedAt" ON "AgentStageRuns" ("CreatedAt");

CREATE INDEX IF NOT EXISTS "IX_AgentStageRuns_CycleId_Role" ON "AgentStageRuns" ("CycleId", "Role");

CREATE UNIQUE INDEX IF NOT EXISTS "IX_PositionHistories_DedupeKey" ON "PositionHistories" ("DedupeKey");

CREATE INDEX IF NOT EXISTS "IX_PositionHistories_ExchangeAccountId" ON "PositionHistories" ("ExchangeAccountId");

CREATE INDEX IF NOT EXISTS "IX_RiskVerdicts_CycleId" ON "RiskVerdicts" ("CycleId");

CREATE UNIQUE INDEX IF NOT EXISTS "IX_RiskVerdicts_ProposalId" ON "RiskVerdicts" ("ProposalId");

CREATE INDEX IF NOT EXISTS "IX_ToolInvocations_CreatedAt" ON "ToolInvocations" ("CreatedAt");

CREATE INDEX IF NOT EXISTS "IX_ToolInvocations_CycleId" ON "ToolInvocations" ("CycleId");

CREATE INDEX IF NOT EXISTS "IX_ToolInvocations_StageRunId" ON "ToolInvocations" ("StageRunId");

CREATE INDEX IF NOT EXISTS "IX_TradeProposals_CycleId" ON "TradeProposals" ("CycleId");

CREATE INDEX IF NOT EXISTS "IX_TradeProposals_Symbol_CreatedAt" ON "TradeProposals" ("Symbol", "CreatedAt");

CREATE INDEX IF NOT EXISTS "IX_TradeProposals_TraderId_CreatedAt" ON "TradeProposals" ("TraderId", "CreatedAt");

CREATE INDEX IF NOT EXISTS "IX_TraderLessons_TraderId_CreatedAt" ON "TraderLessons" ("TraderId", "CreatedAt");

CREATE INDEX IF NOT EXISTS "IX_TraderOrders_CycleId" ON "TraderOrders" ("CycleId");

CREATE UNIQUE INDEX IF NOT EXISTS "IX_TraderOrders_CycleId_ClientOrderId" ON "TraderOrders" ("CycleId", "ClientOrderId");

CREATE INDEX IF NOT EXISTS "IX_TraderOrders_TraderId" ON "TraderOrders" ("TraderId");

CREATE INDEX IF NOT EXISTS "IX_TradingCycles_Status" ON "TradingCycles" ("Status");

CREATE INDEX IF NOT EXISTS "IX_TradingCycles_StrategyId_StartedAt" ON "TradingCycles" ("StrategyId", "StartedAt");

CREATE INDEX IF NOT EXISTS "IX_TradingCycles_TraderId_StartedAt" ON "TradingCycles" ("TraderId", "StartedAt");

-- ----------------------------------------------------------------------------
-- 4. 可选：清理旧链路 DecisionRecords（默认不执行；确认不再需要历史决策原文后再启用）
-- ----------------------------------------------------------------------------
-- DROP TABLE IF EXISTS "DecisionRecords";
