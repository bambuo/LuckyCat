<script setup lang="ts">
const props = withDefaults(
  defineProps<{
    content: string
    maxHeight?: number
    variant?: 'default' | 'success' | 'gold'
    wrap?: boolean
  }>(),
  { maxHeight: 300, variant: 'default', wrap: false },
)
</script>

<template>
  <div class="code-block" :class="[props.variant, { wrap: props.wrap }]" :style="{ maxHeight: `${props.maxHeight}px` }">
    <pre>{{ content }}</pre>
  </div>
</template>

<style scoped>
.code-block {
  overflow: auto;
  padding: 10px 12px;
  border: 1px solid var(--color-border-2);
  border-radius: 8px;
  background: var(--color-fill-1);
}

.code-block pre {
  margin: 0;
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 11px;
  line-height: 1.6;
  color: var(--color-text-1);
  white-space: pre;
}

.code-block.wrap pre {
  white-space: pre-wrap;
  word-break: break-word;
}

.code-block.success {
  border-color: rgba(var(--success-6), 0.5);
}

.code-block.gold {
  border-color: rgba(240, 185, 11, 0.3);
}
</style>
