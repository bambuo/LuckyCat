import { describe, expect, test } from 'bun:test'
import { CYCLE_EVENT_NAMES, parseSseFrames } from '../src/api'

describe('parseSseFrames', () => {
  test('切出 event + data 帧，并保留事件名', () => {
    const { frames, rest } = parseSseFrames('event: stage\ndata: {"role":"analyst"}\n\n')

    expect(frames).toEqual([{ event: 'stage', data: '{"role":"analyst"}' }])
    expect(rest).toBe('')
  })

  test('多行 data 按 SSE 规范用换行连接', () => {
    const { frames } = parseSseFrames('event: proposal\ndata: line1\ndata: line2\n\n')

    expect(frames[0]?.data).toBe('line1\nline2')
  })

  test('忽略注释行与空行字段，缺 data 的帧被跳过', () => {
    const { frames } = parseSseFrames(': keep-alive\n\nevent: ping\n\n')

    expect(frames).toEqual([])
  })

  test('未闭合的半帧留在 rest，下一个 chunk 补齐后成帧', () => {
    const first = parseSseFrames('event: cycle\ndata: {"status":"running"}')

    expect(first.frames).toEqual([])
    expect(first.rest).toBe('event: cycle\ndata: {"status":"running"}')

    const second = parseSseFrames(`${first.rest}\n\n`)
    expect(second.frames).toEqual([{ event: 'cycle', data: '{"status":"running"}' }])
    expect(second.rest).toBe('')
  })

  test('CRLF 边界与字段解析一致', () => {
    const { frames, rest } = parseSseFrames('event: tool\r\ndata: {"tool":"place_order"}\r\n\r\n')

    expect(frames).toEqual([{ event: 'tool', data: '{"tool":"place_order"}' }])
    expect(rest).toBe('')
  })

  test('事件名清单覆盖约定的十种 SSE 事件', () => {
    expect([...CYCLE_EVENT_NAMES].sort()).toEqual(
      [
        'cycle',
        'equity',
        'order',
        'ping',
        'position-history',
        'proposal',
        'stage',
        'state',
        'tool',
        'verdict',
      ].sort(),
    )
  })
})
