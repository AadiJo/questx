import { QuestXApi } from './questx'
import type { VideoModeModel } from '../types'
import { parseAprilTagFrame, type AprilTagFrame } from './aprilTagFrame'

class VideoApi extends QuestXApi {
  async getAprilTagFrame(after: number, signal: AbortSignal): Promise<AprilTagFrame> {
    const started = performance.now()
    const response = await fetch(`${this.baseUrl}/api/apriltag-frame?after=${after}`, {
      signal,
      cache: 'no-store',
    })
    if (!response.ok) throw new Error(`HTTP ${response.status}`)
    const frame = parseAprilTagFrame(await response.arrayBuffer())
    // Exclude the server's long-poll wait, but conservatively include the entire
    // remaining round trip. No wall-clock synchronization with the Quest needed.
    if (frame.jpeg) frame.metadata.ageMs += Math.max(0,
      performance.now() - started - frame.metadata.waitMs)
    return frame
  }

  async getVideoModes(): Promise<VideoModeModel[]> {
    return this.request<VideoModeModel[]>('/api/video-modes')
  }

  /**
   * Returns the resolution x framerate cross-product the AprilTag detector can use.
   * Same shape as getVideoModes() but the AprilTag list does not gate on
   * EnableHighQualityStreams - the detector benefits from higher resolution.
   */
  async getAprilTagVideoModes(): Promise<VideoModeModel[]> {
    return this.request<VideoModeModel[]>('/api/apriltag-video-modes')
  }
}

export const videoApi = new VideoApi()
