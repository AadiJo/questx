// AprilTag preview sizes. Mirrors AprilTagPreviewEncoder.ScaleFactor on the headset, so the
// dashboard can label each choice with the size it will actually produce.

/** Largest preview, in pixels, unless the high-quality stream is enabled. */
export const STANDARD_MAX_PIXELS = 640 * 480

const MAX_SCALE = 16

export const PREVIEW_SCALES = [1, 2, 4, 8] as const
export type PreviewScale = (typeof PREVIEW_SCALES)[number]

const FRACTIONS: Record<PreviewScale, string> = { 1: 'Full', 2: '½', 4: '¼', 8: '⅛' }

/**
 * The per-side scale-down the headset uses for a width x height frame: the requested scale,
 * halved until it divides both sides, then doubled while the preview exceeds maxPixels
 * (0 for no limit).
 */
export function previewFactor(width: number, height: number, requested: number, maxPixels: number): number {
  const divides = (f: number) => width % f === 0 && height % f === 0
  let factor = Math.min(MAX_SCALE, Math.max(1, Math.trunc(requested)))
  while (factor > 1 && !divides(factor)) factor /= 2
  while (maxPixels > 0 && (width / factor) * (height / factor) > maxPixels && factor * 2 <= MAX_SCALE && divides(factor * 2)) {
    factor *= 2
  }
  return factor
}

export interface PreviewOption {
  scale: PreviewScale
  label: string
  /** False when the size needs the high-quality stream (the headset would use a smaller one). */
  available: boolean
}

/**
 * The dashboard's choices for a detector running at width x height. Sizes are as displayed,
 * so they swap for a sideways camera.
 */
export function previewOptions(width: number, height: number, swapAxes: boolean, highQuality: boolean): PreviewOption[] {
  const maxPixels = highQuality ? 0 : STANDARD_MAX_PIXELS
  return PREVIEW_SCALES.map(scale => {
    const factor = previewFactor(width, height, scale, maxPixels)
    const w = width / factor
    const h = height / factor
    const size = swapAxes ? `${h}x${w}` : `${w}x${h}`
    const capped = factor > previewFactor(width, height, scale, 0)
    return {
      scale,
      label: capped ? `${FRACTIONS[scale]} (needs high quality stream)` : `${size} (${FRACTIONS[scale]})`,
      available: !capped,
    }
  })
}
