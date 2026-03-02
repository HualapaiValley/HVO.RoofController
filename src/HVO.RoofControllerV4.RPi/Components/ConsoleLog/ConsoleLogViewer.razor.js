export function scrollLogToBottom(element) {
  if (!element) {
    return;
  }

  requestAnimationFrame(() => {
    try {
      element.scrollTop = element.scrollHeight;
    } catch {
      // Element might have been removed; ignore.
    }
  });
}
