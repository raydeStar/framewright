/**
 * Named delivery formats shared by new-project creation and Production setup.
 *
 * Artists pick a shape and a destination, not a pixel count. Each preset already
 * satisfies the server's contract (even dimensions, aspect ratio within 1%), so
 * choosing one can never produce a validation error.
 */
export const DELIVERY_PRESETS = [
  { id: 'streaming-hd', label: 'Widescreen HD', detail: '1920 × 1080 · 16:9 · 24fps', aspectRatio: '16:9', deliveryWidth: 1920, deliveryHeight: 1080, framesPerSecond: 24 },
  { id: 'streaming-uhd', label: 'Widescreen 4K', detail: '3840 × 2160 · 16:9 · 24fps', aspectRatio: '16:9', deliveryWidth: 3840, deliveryHeight: 2160, framesPerSecond: 24 },
  { id: 'scope-uhd', label: 'Cinema scope', detail: '3840 × 1608 · 2.39:1 · 24fps', aspectRatio: '2.39:1', deliveryWidth: 3840, deliveryHeight: 1608, framesPerSecond: 24 },
  { id: 'vertical-hd', label: 'Vertical', detail: '1080 × 1920 · 9:16 · 24fps', aspectRatio: '9:16', deliveryWidth: 1080, deliveryHeight: 1920, framesPerSecond: 24 },
] as const

export type DeliveryPreset = typeof DELIVERY_PRESETS[number]

export function matchPreset(format: { aspectRatio: string; deliveryWidth: number; deliveryHeight: number; framesPerSecond: number }) {
  return DELIVERY_PRESETS.find(preset => preset.aspectRatio === format.aspectRatio
    && preset.deliveryWidth === format.deliveryWidth
    && preset.deliveryHeight === format.deliveryHeight
    && preset.framesPerSecond === format.framesPerSecond)
}
