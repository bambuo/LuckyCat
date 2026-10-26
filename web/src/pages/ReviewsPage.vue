<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { Message } from '@arco-design/web-vue'
import { IconBook, IconPlayArrow } from '@arco-design/web-vue/es/icon'
import {
  ApiRequestError,
  listTraderLessons,
  listTraderReviews,
  listTraders,
  runTraderReview,
} from '../api'
import type {
  LessonDto,
  ReviewListItemDto,
  ReviewRunDto,
  ReviewRunStatus,
  StageStatus,
  Trader,
} from '../types'

const route = useRoute()
const router = useRouter()

const PAGE_SIZE_OPTIONS = [10, 20, 50]
const ERROR_TEXT_MAX = 48

const REVIEW_STATUS_META: Record<StageStatus, { label: string; color: string }> = {
  completed: { label: '完成', color: 'green' },
  failed: { label: '失败', color: 'red' },
  skipped: { label: '跳过', color: 'orange' },
}

const RUN_STATUS_META: Record<ReviewRunStatus, { label: string; color: string }> = {
  completed: { label: '完成', color: 'green' },
  failed: { label: '失败', color: 'red' },
  skipped: { label: '跳过', color: 'orange' },
}

// —— 交易员选择（?trader=<id>，与看板同一约定） ——

const traders = ref<Trader[]>([])
// 首次加载前即为加载态：避免空数组先闪一下「暂无交易员/暂无记录」
const tradersLoading = ref(true)
const errorText = ref<string | null>(null)
const selectedId = computed<number | null>(() => {
  const raw = route.query.trader
  const id = Number(Array.isArray(raw) ? raw[0] : raw)
  return Number.isFinite(id) && id > 0 ? id : null
})

function onSwitch(value: unknown) {
  const id = Number(value)
  if (!Number.isFinite(id) || id <= 0 || id === selectedId.value) return
  void router.replace({ path: '/reviews', query: { trader: String(id) } })
}

async function loadTraders() {
  tradersLoading.value = true
  try {
    traders.value = await listTraders()
    if (traders.value.length === 0) return
    const current = selectedId.value
    if (current === null || !traders.value.some((t) => t.id === current)) {
      await router.replace({ path: '/reviews', query: { trader: String(traders.value[0].id) } })
    }
  } catch (err) {
    errorText.value = err instanceof Error ? err.message : '加载交易员失败'
    // 没有交易员就无法发起列表请求：结束初始加载态，避免 spinner 永久旋转
    reviewLoading.value = false
    lessonLoading.value = false
  } finally {
    tradersLoading.value = false
  }
}

// —— 复盘记录 ——

const activeTab = ref<string>('reviews')
const reviews = ref<ReviewListItemDto[]>([])
const reviewTotal = ref(0)
const reviewPage = ref(1)
const reviewPageSize = ref(10)
const reviewLoading = ref(true)
const reviewError = ref<string | null>(null)
let reviewSeq = 0

async function loadReviews() {
  const traderId = selectedId.value
  if (traderId === null) return
  const seq = ++reviewSeq
  reviewLoading.value = true
  try {
    const paged = await listTraderReviews(traderId, reviewPage.value, reviewPageSize.value)
    if (seq !== reviewSeq) return
    reviews.value = paged.items
    reviewTotal.value = paged.total
    if (paged.page !== reviewPage.value) reviewPage.value = paged.page
    reviewError.value = null
  } catch (err) {
    if (seq !== reviewSeq) return
    reviewError.value = err instanceof Error ? err.message : '加载复盘记录失败'
  } finally {
    if (seq === reviewSeq) reviewLoading.value = false
  }
}

function onReviewPageChange(next: number) {
  if (next === reviewPage.value) return
  reviewPage.value = next
  void loadReviews()
}

function onReviewPageSizeChange(size: number) {
  if (size === reviewPageSize.value) return
  reviewPageSize.value = size
  reviewPage.value = 1
  void loadReviews()
}

// —— 经验 ——

const lessons = ref<LessonDto[]>([])
const lessonTotal = ref(0)
const lessonPage = ref(1)
const lessonPageSize = ref(10)
const lessonLoading = ref(true)
const lessonError = ref<string | null>(null)
let lessonSeq = 0

async function loadLessons() {
  const traderId = selectedId.value
  if (traderId === null) return
  const seq = ++lessonSeq
  lessonLoading.value = true
  try {
    const paged = await listTraderLessons(traderId, lessonPage.value, lessonPageSize.value)
    if (seq !== lessonSeq) return
    lessons.value = paged.items
    lessonTotal.value = paged.total
    if (paged.page !== lessonPage.value) lessonPage.value = paged.page
    lessonError.value = null
  } catch (err) {
    if (seq !== lessonSeq) return
    lessonError.value = err instanceof Error ? err.message : '加载经验失败'
  } finally {
    if (seq === lessonSeq) lessonLoading.value = false
  }
}

function onLessonPageChange(next: number) {
  if (next === lessonPage.value) return
  lessonPage.value = next
  void loadLessons()
}

function onLessonPageSizeChange(size: number) {
  if (size === lessonPageSize.value) return
  lessonPageSize.value = size
  lessonPage.value = 1
  void loadLessons()
}

interface LessonView {
  lesson: LessonDto
  strengths: string[]
  mistakes: string[]
  rules: string[]
  evidence: string[]
}

function cleanItems(items: string[]): string[] {
  return items.map((item) => item.trim()).filter((item) => item.length > 0)
}

const lessonViews = computed<LessonView[]>(() =>
  lessons.value.map((lesson) => ({
    lesson,
    strengths: cleanItems(lesson.strengths),
    mistakes: cleanItems(lesson.mistakes),
    rules: cleanItems(lesson.rules),
    evidence: cleanItems(lesson.evidence),
  })),
)

async function reloadLists() {
  reviewPage.value = 1
  lessonPage.value = 1
  await Promise.all([loadReviews(), loadLessons()])
}

// —— 手动触发复盘（触发成功 ≠ 复盘成功，结果按 ReviewRunDto.status 展示） ——

const runBusy = ref(false)
const runOutcome = ref<ReviewRunDto | null>(null)
const runWarning = ref<string | null>(null)
const runErrorText = ref<string | null>(null)

const runAlertType = computed<'success' | 'warning' | 'error'>(() => {
  if (!runOutcome.value) return 'warning'
  if (runOutcome.value.status === 'completed') return 'success'
  if (runOutcome.value.status === 'failed') return 'error'
  return 'warning'
})

async function onRunReview() {
  const traderId = selectedId.value
  if (traderId === null || runBusy.value) return
  runBusy.value = true
  runOutcome.value = null
  runWarning.value = null
  runErrorText.value = null
  try {
    const result = await runTraderReview(traderId)
    runOutcome.value = result
    if (result.status === 'completed') {
      Message.success('复盘完成')
    } else if (result.status === 'failed') {
      Message.warning(result.errorCode ? `复盘失败：${result.errorCode}` : '复盘失败')
    } else {
      Message.info('复盘窗口内无数据，本轮已跳过')
    }
  } catch (err) {
    // 409 / 422 都是契约内的正常结果：用警告呈现，不按失败弹错误框。
    if (err instanceof ApiRequestError && (err.code === 'review_in_progress' || err.status === 409)) {
      // 后端区分「进行中」与「近期已触发」，直接展示其文案而不是固定 60 秒提示
      const message = err.message || '复盘触发被拒绝，请稍候'
      runWarning.value = message
      Message.warning(message)
    } else if (err instanceof ApiRequestError && (err.code === 'review_no_data' || err.status === 422)) {
      runWarning.value = err.message
      Message.warning(err.message)
    } else {
      const message = err instanceof Error ? err.message : '复盘触发失败'
      runErrorText.value = message
      Message.error(message)
    }
  } finally {
    // 触发结束后重拉两个列表一次（skipped 也会留下阶段行）；交易员已切换则跳过。
    if (selectedId.value === traderId) await reloadLists()
    runBusy.value = false
  }
}

// —— 展示辅助 ——

function fmtTime(iso: string | null): string {
  if (!iso) return '—'
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? '—' : d.toLocaleString('zh-CN', { hour12: false })
}

function formatElapsed(ms: number | null): string {
  if (ms === null || !Number.isFinite(ms) || ms < 0) return '—'
  if (ms < 1000) return `${Math.round(ms)}ms`
  if (ms < 60000) return `${(ms / 1000).toFixed(1)}s`
  return `${Math.floor(ms / 60000)}m${Math.round((ms % 60000) / 1000)}s`
}

function shortError(value: string | null): string {
  if (!value) return '—'
  return value.length > ERROR_TEXT_MAX ? `${value.slice(0, ERROR_TEXT_MAX)}…` : value
}

onMounted(async () => {
  const before = selectedId.value
  await loadTraders()
  // 查询参数被纠正（缺省/失效）时由 selectedId 的 watch 统一拉取，避免重复请求
  if (selectedId.value !== before) return
  if (traders.value.length > 0) await reloadLists()
})

watch(selectedId, (id, previous) => {
  if (id === previous) return
  reviews.value = []
  reviewTotal.value = 0
  reviewPage.value = 1
  reviewError.value = null
  lessons.value = []
  lessonTotal.value = 0
  lessonPage.value = 1
  lessonError.value = null
  runOutcome.value = null
  runWarning.value = null
  runErrorText.value = null
  if (id !== null) void reloadLists()
})
</script>

<template>
  <div class="page-content">
    <div class="page-header">
      <div class="header-icon"><IconBook /></div>
      <div class="header-text">
        <h1>复盘经验</h1>
        <a-tag color="gold" size="small">{{ reviewTotal }} 条复盘 · {{ lessonTotal }} 条经验</a-tag>
      </div>
      <div class="header-actions">
        <span class="trader-label">交易员</span>
        <a-select
          :model-value="selectedId ?? undefined"
          :loading="tradersLoading"
          :disabled="runBusy"
          :style="{ width: '200px' }"
          size="small"
          placeholder="选择交易员"
          @update:model-value="onSwitch"
        >
          <a-option v-for="t in traders" :key="t.id" :value="t.id">{{ t.name }}</a-option>
        </a-select>
        <a-tooltip content="触发后等待模型返回，最长约 120 秒；60 秒内不可重复触发" :disabled="runBusy">
          <a-button
            type="primary"
            size="small"
            :loading="runBusy"
            :disabled="selectedId === null"
            @click="onRunReview"
          >
            <template #icon><IconPlayArrow /></template>
            立即复盘
          </a-button>
        </a-tooltip>
      </div>
    </div>

    <a-alert v-if="errorText" type="error" class="page-alert" closable @close="errorText = null">
      {{ errorText }}
    </a-alert>

    <div v-if="!tradersLoading && traders.length === 0 && !errorText" class="page-empty">
      <a-empty>
        <div class="empty-desc">暂无交易员</div>
        <a-button type="primary" @click="router.push('/traders')">去创建交易员</a-button>
      </a-empty>
    </div>

    <template v-else>
      <a-alert v-if="runWarning" type="warning" class="page-alert" closable @close="runWarning = null">
        {{ runWarning }}
      </a-alert>

      <a-alert
        v-if="runOutcome"
        :type="runAlertType"
        class="page-alert"
        closable
        @close="runOutcome = null"
      >
        <div class="run-outcome">
          <span class="run-outcome-label">复盘结果</span>
          <a-tag size="small" :color="RUN_STATUS_META[runOutcome.status].color">
            {{ RUN_STATUS_META[runOutcome.status].label }}
          </a-tag>
          <code v-if="runOutcome.errorCode" class="error-code">{{ runOutcome.errorCode }}</code>
          <span>耗时 {{ formatElapsed(runOutcome.elapsedMs) }}</span>
          <span v-if="runOutcome.periodStart && runOutcome.periodEnd">
            窗口 {{ fmtTime(runOutcome.periodStart) }} ~ {{ fmtTime(runOutcome.periodEnd) }}
          </span>
          <span v-if="runOutcome.status === 'skipped'" class="run-outcome-note">窗口内无数据</span>
        </div>
      </a-alert>

      <a-alert v-if="runErrorText" type="error" class="page-alert" closable @close="runErrorText = null">
        {{ runErrorText }}
      </a-alert>

      <a-card class="reviews-card">
        <a-tabs v-model:active-key="activeTab" type="rounded" size="small">
          <a-tab-pane key="reviews" :title="`复盘记录（${reviewTotal}）`">
            <a-spin :loading="reviewLoading" class="tab-spin">
              <a-alert
                v-if="reviewError"
                type="error"
                class="tab-alert"
                closable
                @close="reviewError = null"
              >
                {{ reviewError }}
              </a-alert>

              <div v-if="reviews.length === 0 && !reviewLoading && !reviewError" class="tab-empty">
                <a-empty description="暂无复盘记录（可点击「立即复盘」触发）" />
              </div>
              <table v-else class="data-table">
                <thead>
                  <tr>
                    <th>时间</th>
                    <th>状态</th>
                    <th>尝试</th>
                    <th>模型</th>
                    <th class="num">耗时</th>
                    <th class="num">工具调用</th>
                    <th>错误</th>
                  </tr>
                </thead>
                <tbody>
                  <tr
                    v-for="review in reviews"
                    :key="review.id"
                    :class="{ 'review-row-failed': review.status === 'failed' }"
                  >
                    <td class="time-cell">{{ fmtTime(review.createdAt) }}</td>
                    <td>
                      <a-space :size="4">
                        <a-tag size="small" :color="REVIEW_STATUS_META[review.status].color">
                          {{ REVIEW_STATUS_META[review.status].label }}
                        </a-tag>
                        <span v-if="review.status === 'skipped'" class="skip-note">窗口内无数据</span>
                      </a-space>
                    </td>
                    <td>第 {{ review.attempt }} 次</td>
                    <td>
                      <div class="model-name">{{ review.model }}</div>
                      <div class="model-provider">{{ review.providerName }}</div>
                    </td>
                    <td class="num">{{ formatElapsed(review.elapsedMs) }}</td>
                    <td class="num">{{ review.toolCalls }}</td>
                    <td class="error-cell" :title="review.error ?? ''">{{ shortError(review.error) }}</td>
                  </tr>
                </tbody>
              </table>

              <div v-if="reviewTotal > 0" class="table-footer">
                <a-pagination
                  :current="reviewPage"
                  :total="reviewTotal"
                  :page-size="reviewPageSize"
                  :page-size-options="PAGE_SIZE_OPTIONS"
                  size="small"
                  show-total
                  show-page-size
                  @change="onReviewPageChange"
                  @page-size-change="onReviewPageSizeChange"
                />
              </div>
            </a-spin>
          </a-tab-pane>

          <a-tab-pane key="lessons" :title="`经验（${lessonTotal}）`">
            <a-spin :loading="lessonLoading" class="tab-spin">
              <a-alert
                v-if="lessonError"
                type="error"
                class="tab-alert"
                closable
                @close="lessonError = null"
              >
                {{ lessonError }}
              </a-alert>

              <div v-if="lessons.length === 0 && !lessonLoading && !lessonError" class="tab-empty">
                <a-empty description="暂无经验记录（复盘完成后自动生成）" />
              </div>
              <div v-else class="lesson-list">
                <a-card
                  v-for="view in lessonViews"
                  :key="view.lesson.id"
                  size="small"
                  class="lesson-card"
                >
                  <div class="lesson-head">
                    <span class="lesson-title">经验 #{{ view.lesson.id }}</span>
                    <span class="lesson-period">
                      {{ fmtTime(view.lesson.periodStart) }} ~ {{ fmtTime(view.lesson.periodEnd) }}
                    </span>
                    <span class="lesson-created">生成于 {{ fmtTime(view.lesson.createdAt) }}</span>
                  </div>

                  <div class="lesson-summary">{{ view.lesson.summary }}</div>

                  <div class="lesson-sections">
                    <div v-if="view.strengths.length > 0" class="lesson-section">
                      <div class="section-label section-good">优势</div>
                      <ul class="section-list">
                        <li v-for="(item, index) in view.strengths" :key="index">{{ item }}</li>
                      </ul>
                    </div>

                    <div v-if="view.mistakes.length > 0" class="lesson-section">
                      <div class="section-label section-bad">错误</div>
                      <ul class="section-list">
                        <li v-for="(item, index) in view.mistakes" :key="index">{{ item }}</li>
                      </ul>
                    </div>

                    <div v-if="view.rules.length > 0" class="lesson-section">
                      <div class="section-label section-rule">规则</div>
                      <ul class="section-list">
                        <li v-for="(item, index) in view.rules" :key="index">{{ item }}</li>
                      </ul>
                    </div>

                    <div v-if="view.evidence.length > 0" class="lesson-section">
                      <div class="section-label section-evidence">证据</div>
                      <ul class="section-list">
                        <li v-for="(item, index) in view.evidence" :key="index">{{ item }}</li>
                      </ul>
                    </div>
                  </div>
                </a-card>
              </div>

              <div v-if="lessonTotal > 0" class="table-footer">
                <a-pagination
                  :current="lessonPage"
                  :total="lessonTotal"
                  :page-size="lessonPageSize"
                  :page-size-options="PAGE_SIZE_OPTIONS"
                  size="small"
                  show-total
                  show-page-size
                  @change="onLessonPageChange"
                  @page-size-change="onLessonPageSizeChange"
                />
              </div>
            </a-spin>
          </a-tab-pane>
        </a-tabs>
      </a-card>
    </template>
  </div>
</template>

<style scoped>
.page-content {
  width: 100%;
  max-width: 1200px;
  margin: 0 auto;
  padding: 24px;
}

.page-header {
  display: flex;
  align-items: center;
  gap: 16px;
  padding-bottom: 20px;
  margin-bottom: 24px;
  border-bottom: 1px solid var(--color-border-2);
}

.header-icon {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 52px;
  height: 52px;
  border: 1px solid rgba(240, 185, 11, 0.3);
  border-radius: 12px;
  background: var(--color-bg-2);
  color: rgb(var(--primary-6));
  font-size: 26px;
  flex-shrink: 0;
}

.header-text {
  display: flex;
  align-items: center;
  gap: 12px;
  flex: 1;
  min-width: 0;
}

.header-text h1 {
  margin: 0;
  font-size: 24px;
  font-weight: 600;
  color: var(--color-text-1);
}

.header-actions {
  display: flex;
  align-items: center;
  gap: 8px;
}

.trader-label {
  font-size: 12px;
  color: var(--color-text-3);
}

.page-alert {
  margin-bottom: 16px;
}

.page-empty {
  padding: 72px 0;
  text-align: center;
  background: var(--color-bg-2);
  border-radius: 12px;
}

.empty-desc {
  margin-bottom: 12px;
  font-size: 13px;
  color: var(--color-text-3);
}

.reviews-card {
  border-radius: 12px;
}

.tab-spin {
  display: block;
  width: 100%;
  min-height: 160px;
}

.tab-alert {
  margin-bottom: 12px;
}

.tab-empty {
  padding: 40px 0;
}

.run-outcome {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 8px;
  font-size: 12px;
}

.run-outcome-label {
  font-weight: 600;
}

.run-outcome-note {
  color: rgb(var(--warning-6));
}

.data-table {
  width: 100%;
  border-collapse: collapse;
  font-size: 12px;
}

.data-table th {
  text-align: left;
  font-size: 11px;
  font-weight: 600;
  color: var(--color-text-3);
  padding: 4px 4px 10px;
  border-bottom: 1px solid var(--color-border-1);
  white-space: nowrap;
}

.data-table td {
  padding: 10px 4px;
  border-bottom: 1px solid var(--color-border-1);
  color: var(--color-text-1);
  white-space: nowrap;
  vertical-align: top;
}

.data-table .num {
  text-align: right;
  font-variant-numeric: tabular-nums;
}

.time-cell {
  font-variant-numeric: tabular-nums;
}

.review-row-failed td {
  background: var(--color-danger-light-1);
}

.model-name {
  font-size: 12px;
  color: var(--color-text-1);
}

.model-provider {
  font-size: 11px;
  color: var(--color-text-3);
}

.skip-note {
  font-size: 11px;
  color: rgb(var(--warning-6));
}

.error-code {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 11px;
  padding: 1px 5px;
  border-radius: 4px;
  background: var(--color-danger-light-1);
  color: rgb(var(--danger-6));
}

.error-cell {
  max-width: 260px;
  overflow: hidden;
  text-overflow: ellipsis;
  color: var(--color-text-2);
}

.table-footer {
  display: flex;
  justify-content: flex-end;
  padding-top: 16px;
}

.lesson-list {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.lesson-card {
  border-radius: 10px;
}

.lesson-head {
  display: flex;
  align-items: baseline;
  flex-wrap: wrap;
  gap: 10px;
  margin-bottom: 8px;
}

.lesson-title {
  font-size: 13px;
  font-weight: 600;
  color: var(--color-text-1);
}

.lesson-period {
  font-size: 12px;
  color: var(--color-text-2);
  font-variant-numeric: tabular-nums;
}

.lesson-created {
  margin-left: auto;
  font-size: 11px;
  color: var(--color-text-3);
}

.lesson-summary {
  font-size: 13px;
  line-height: 1.7;
  color: var(--color-text-1);
  white-space: pre-wrap;
  word-break: break-word;
  padding: 10px 12px;
  border-radius: 8px;
  background: var(--color-fill-1);
}

.lesson-sections {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 12px;
  margin-top: 12px;
}

.lesson-section {
  min-width: 0;
}

.section-label {
  font-size: 11px;
  font-weight: 600;
  margin-bottom: 4px;
}

.section-good {
  color: rgb(var(--success-6));
}

.section-bad {
  color: rgb(var(--danger-6));
}

.section-rule {
  color: rgb(var(--primary-6));
}

.section-evidence {
  color: var(--color-text-3);
}

.section-list {
  margin: 0;
  padding-left: 18px;
  font-size: 12px;
  line-height: 1.65;
  color: var(--color-text-2);
  word-break: break-word;
}

.section-list li {
  margin-bottom: 2px;
}

@media (max-width: 900px) {
  .page-header {
    flex-wrap: wrap;
  }

  .header-actions {
    width: 100%;
    justify-content: flex-end;
  }

  .lesson-sections {
    grid-template-columns: 1fr;
  }

  .lesson-created {
    margin-left: 0;
  }

  .error-cell {
    max-width: 160px;
  }
}
</style>
