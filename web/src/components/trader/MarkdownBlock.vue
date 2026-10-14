<script setup lang="ts">
import { computed } from 'vue'
import MarkdownIt from 'markdown-it'

const props = defineProps<{ text: string }>()

// html:false（外网新闻等外部文本必经转义，防注入）；breaks:true 保留单换行观感
const md = new MarkdownIt({ html: false, linkify: false, breaks: true })

const rendered = computed(() => md.render(props.text))
</script>

<template>
  <div class="md-body" v-html="rendered"></div>
</template>

<style scoped>
.md-body {
  font-size: 12px;
  color: var(--color-text-2);
  background: var(--color-bg-1);
  border: 1px solid var(--color-border-2);
  border-radius: 8px;
  padding: 12px;
  line-height: 1.7;
  max-height: 360px;
  overflow-y: auto;
}

.md-body :deep(h1),
.md-body :deep(h2),
.md-body :deep(h3),
.md-body :deep(h4),
.md-body :deep(h5) {
  margin: 10px 0 6px;
  font-size: 13px;
  font-weight: 600;
  color: var(--color-text-1);
}

.md-body :deep(h1:first-child),
.md-body :deep(h2:first-child),
.md-body :deep(h3:first-child) {
  margin-top: 0;
}

.md-body :deep(p) {
  margin: 6px 0;
}

.md-body :deep(ul),
.md-body :deep(ol) {
  margin: 6px 0;
  padding-left: 18px;
}

.md-body :deep(code) {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 11px;
  background: var(--color-fill-2);
  border-radius: 3px;
  padding: 1px 4px;
}

.md-body :deep(pre) {
  background: var(--color-fill-2);
  border-radius: 6px;
  padding: 8px 10px;
  overflow-x: auto;
}

.md-body :deep(pre code) {
  background: transparent;
  padding: 0;
}

.md-body :deep(table) {
  width: 100%;
  border-collapse: collapse;
  margin: 8px 0;
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 11px;
  font-variant-numeric: tabular-nums;
}

.md-body :deep(th),
.md-body :deep(td) {
  border: 1px solid var(--color-border-1);
  padding: 2px 6px;
  white-space: nowrap;
}

.md-body :deep(th) {
  background: var(--color-fill-1);
  font-weight: 600;
  color: var(--color-text-3);
}

.md-body :deep(blockquote) {
  margin: 6px 0;
  padding-left: 10px;
  border-left: 3px solid var(--color-border-2);
  color: var(--color-text-3);
}

.md-body :deep(a) {
  color: rgb(var(--primary-6));
}

.md-body :deep(hr) {
  border: none;
  border-top: 1px solid var(--color-border-1);
  margin: 10px 0;
}

/* 宽表格横向滚动而不是撑破卡片 */
.md-body {
  overflow-x: auto;
}
</style>
