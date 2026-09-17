const KEYBOARD_FOCUS_CLASS = 'keyboard-focus';

/** 标记焦点来自键盘操作；页面只在此时显示 focus-visible 外框。 */
export function markKeyboardFocus() {
  document.documentElement.classList.add(KEYBOARD_FOCUS_CLASS);
}

/** 清除键盘焦点标记，避免鼠标或程序化聚焦后残留外框。 */
export function clearKeyboardFocus() {
  document.documentElement.classList.remove(KEYBOARD_FOCUS_CLASS);
}

/** 清掉标签页焦点，供窗口隐藏 / 托盘复位时调用。 */
export function clearTabFocus() {
  const active = document.activeElement;
  if (active instanceof HTMLElement && active.closest('.tabs')) active.blur();
}
