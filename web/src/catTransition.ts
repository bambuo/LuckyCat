import { reactive } from 'vue'

// 招财猫转场：全局单例状态，由 CatTransition 组件渲染。
// 放在 App 根部（而非页面内）才能跨路由切换后继续播完。
const state = reactive({
  active: false,
  leaving: false,
  label: '',
})

const HOLD_MS = 720
const FADE_MS = 380
let holdTimer: number | undefined
let fadeTimer: number | undefined

/** 播放一次招财猫转场；label 为目的地名称（如交易员名），重复调用会重播 */
export function playCatTransition(label: string) {
  window.clearTimeout(holdTimer)
  window.clearTimeout(fadeTimer)
  state.label = label
  state.active = true
  state.leaving = false
  holdTimer = window.setTimeout(() => {
    state.leaving = true
  }, HOLD_MS)
  fadeTimer = window.setTimeout(() => {
    state.active = false
    state.leaving = false
  }, HOLD_MS + FADE_MS)
}

export function useCatTransition() {
  return state
}
