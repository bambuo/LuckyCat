import { describe, expect, test } from 'bun:test'
import { parseDesktopFlag, routeMessage } from '../src/desktop'

describe('parseDesktopFlag', () => {
  test('查询串命中即桌面模式', () => {
    expect(parseDesktopFlag('?desktop=1', null)).toBe(true)
  })

  test('已写入会话标记后，路由跳转丢失查询串仍为桌面模式', () => {
    expect(parseDesktopFlag('', '1')).toBe(true)
  })

  test('普通浏览器访问不是桌面模式', () => {
    expect(parseDesktopFlag('', null)).toBe(false)
    expect(parseDesktopFlag('?x=1', '0')).toBe(false)
  })
})

describe('routeMessage', () => {
  test('路由回传是带 type 的 JSON 字符串', () => {
    expect(JSON.parse(routeMessage('/cycles'))).toEqual({ type: 'route', path: '/cycles' })
  })
})
