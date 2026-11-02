export function canvasToPngBlob(canvas: HTMLCanvasElement, failureMessage: string) {
  return new Promise<Blob>((resolve, reject) => canvas.toBlob(value => {
    if (value && value.size > 0) { resolve(value); return }
    // WebKit can occasionally return a non-null zero-byte Blob for an
    // otherwise valid canvas. Re-encode through its data URL before surfacing
    // an error; a zero-byte guide must never reach the immutable manifest.
    try {
      const encoded = canvas.toDataURL('image/png')
      const separator = encoded.indexOf(',')
      if (separator < 0) throw new Error(failureMessage)
      const binary = window.atob(encoded.slice(separator + 1))
      const bytes = new Uint8Array(binary.length)
      for (let index = 0; index < binary.length; index += 1) bytes[index] = binary.charCodeAt(index)
      const fallback = new Blob([bytes], { type: 'image/png' })
      if (fallback.size === 0) throw new Error(failureMessage)
      resolve(fallback)
    } catch { reject(new Error(failureMessage)) }
  }, 'image/png'))
}
