const DESKTOP_KEY = 'luckycat:desktop'

export function parseDesktopFlag(search: string, stored: string | null): boolean {
  return search.includes('desktop=1') || stored === '1'
}

export function isDesktopMode(): boolean {
  if (typeof window === 'undefined') return false
  const flag = parseDesktopFlag(window.location.search, window.sessionStorage.getItem(DESKTOP_KEY))
  if (flag) window.sessionStorage.setItem(DESKTOP_KEY, '1')
  return flag
}

export function routeMessage(path: string): string {
  return JSON.stringify({ type: 'route', path })
}

export function notifyRoute(path: string): void {
  window.invokeCSharpAction?.(routeMessage(path))
}

export function installDesktopNavigation(push: (path: string) => void): void {
  window.__lcNavigate = (path: string) => push(path)
}

declare global {
  interface Window {
    invokeCSharpAction?: (data: string) => void
    __lcNavigate?: (path: string) => void
  }
}
