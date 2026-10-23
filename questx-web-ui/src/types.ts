// Type definitions for configuration system

export interface StreamModeModel {
  width: number
  height: number
  framerate: number
  quality: number
}

export interface VideoModeModel {
  width: number
  height: number
  framerate: number
}

// How the headset is rolled on its mount, viewed from behind it (as worn). The stream,
// AprilTag preview, Limelight tx/ty, and reported headset poses are rotated upright to
// match, so the robot-to-headset transform should not include this roll.
export enum CameraOrientation {
  Normal = 0,
  UpsideDown = 1,
  Left = 2,
  Right = 3,
}

// Headset center-eye pose in the robot frame (WPILib axes): meters and degrees.
// Published poses are the robot's pose, so robot code must not apply this again.
export interface RobotToQuest {
  x: number
  y: number
  z: number
  roll: number
  pitch: number
  yaw: number
}

// WPILib pose: meters, unit quaternion (w, x, y, z).
export interface PoseJson {
  x: number
  y: number
  z: number
  qw: number
  qx: number
  qy: number
  qz: number
}

export type SceneTagStatus = 'used' | 'ignored' | 'unknown'

export interface SceneTag {
  id: number
  status: SceneTagStatus
  // Single-tag pose in the camera frame (X forward, Y left, Z up); null if it did not solve.
  cameraToTag: PoseJson | null
  ambiguity: number
  reprojectionErrorPixels: number | null
}

// GET /api/scene: everything the 3D views draw.
export interface SceneResponse {
  // Driver Station alliance from /FMSInfo/IsRedAlliance, or null while unknown.
  alliance: 'red' | 'blue' | null
  robotToQuest: PoseJson
  vio: {
    tracking: boolean
    epoch: number
    ageMs: number
    // Local VIO frame, not field-aligned.
    robotPose: PoseJson
    headsetPose: PoseJson
  } | null
  field: {
    // Display only: VIO carried into the field frame by the last tag solve.
    estimate: PoseJson | null
    lastCorrection: { meters: number; yawRadians: number; intervalSeconds: number } | null
    measurement: {
      ageMs: number
      robotPose: PoseJson
      headsetPose: PoseJson
      tagIds: number[]
      ambiguity: number
      reprojectionErrorPixels: number | null
      averageTagDistanceMeters: number
      stdDevs: {
        xMeters: number
        yMeters: number
        zMeters: number
        rollRadians: number
        pitchRadians: number
        yawRadians: number
      }
    } | null
  }
  detections: {
    ageMs: number
    width: number
    height: number
    intrinsics: { fx: number; fy: number; cx: number; cy: number }
    headsetToCamera: PoseJson
    tags: SceneTag[]
  } | null
  detector: { active: boolean; lastOutcome: string; lastReason: string } | null
}

// GET /api/scene/layout: the field layout loaded at startup.
export interface SceneLayout {
  file: string
  loaded: boolean
  tagSize: number
  field: { length: number; width: number } | null
  tags: { id: number; pose: PoseJson }[]
}

export interface AprilTagFrameMetadata {
  version: 1
  enabled: boolean
  streamEnabled: boolean
  frameId: number
  width: number
  height: number
  ageMs: number
  waitMs: number
  maxAgeMs: number
  tags: {
    id: number
    corners: { x: number; y: number }[]
  }[]
}

export interface AprilTagDetectorMode {
  mode: number
  width: number
  height: number
  framerate: number
  // Blacklist: detections with one of these IDs are dropped before the PoseLib solver runs.
  // Empty array means detect every tag. UI clamps entries to [0, 50] (FRC tag range).
  ignoredIds: number[]
  maxDistance: number
  minimumNumberOfTags: number
  // Field-layout JSON file used at app startup. Restart-on-change: writing this value
  // persists it but the running app keeps using the previously-loaded layout. The UI
  // surfaces the restart-required state with an inline banner + restart button.
  fieldLayoutFile: string
  // Legacy Quest-fusion settings retained only for configuration downgrade compatibility.
  confidencePreset?: number
  noiseScale?: number
  // Tag detector backend: 0 = Napa (default), 1 = libapriltag. Switches on the next frame.
  detector?: number
}

export interface AprilTagFieldLayoutEntry {
  fileName: string
  displayName: string
  source: 'bundled' | 'custom'
  tagCount: number
}

export interface ConfigResponse {
  success: boolean
  teamNumber: number
  debugIpOverride: string
  allowedPoseResetTimeoutMs: number
  enableAutoStartOnBoot: boolean
  enablePassthroughStream: boolean
  enableHighQualityStream: boolean
  cameraOrientation: CameraOrientation
  robotToQuest: RobotToQuest
  enableDebugLogging: boolean
  streamMode: StreamModeModel
  enableAprilTagDetector: boolean
  aprilTagDetectorMode: AprilTagDetectorMode
  timestamp: number
}

export interface ConfigUpdateRequest {
  teamNumber?: number
  debugIpOverride?: string
  allowedPoseResetTimeoutMs?: number
  enableAutoStartOnBoot?: boolean
  enablePassthroughStream?: boolean
  enableHighQualityStream?: boolean
  cameraOrientation?: CameraOrientation
  robotToQuest?: RobotToQuest
  enableDebugLogging?: boolean
  streamMode?: StreamModeModel
  enableAprilTagDetector?: boolean
  aprilTagDetectorMode?: AprilTagDetectorMode
}

export interface SimpleResponse {
  success: boolean
  message: string
}

export interface ServerInfo {
  appName: string
  version: string
  unityVersion: string
  buildDate: string
  platform: string
  deviceModel: string
  operatingSystem: string
  connectedClients: number
  serverPort: number
  timestamp: number
}

export interface HeadsetStatus {
  // Pose
  position: { x: number, y: number, z: number }
  rotation: { x: number, y: number, z: number, w: number }
  eulerAngles: { pitch: number, yaw: number, roll: number }

  // Tracking
  isTracking: boolean
  trackingLostEvents: number
  trackingEpoch: number

  // Battery
  batteryPercent: number
  batteryLevel: number
  batteryStatus: string
  batteryCharging: boolean

  // Network
  networkConnected: boolean
  ipAddress: string
  teamNumber: number
  robotIpAddress: string

  // Performance
  fps: number
  frameCount: number
  vioPublishCount: number

  // Latest field-relative AprilTag measurement and pipeline health. This is not
  // a fusion decision; the robot decides whether to accept each measurement.
  aprilTag: {
    detectorActive: boolean
    cameraFrameCount: number
    cameraFailureCount: number
    solveCount: number
    measurementCount: number
    publishCount: number
    measurementAgeMs: number
    tagIds: number[]
    inlierCount: number
    cornerCount: number
    inlierRatio: number
    averageTagDistanceMeters: number
    linearStdDevMeters: number
    lastOutcome: string
    lastReason: string
    // Backend actually running ("Napa" or "AprilTag"); empty while disabled.
    detector?: string
    // Grayscale conversion plus detection: latest frame and moving average, milliseconds.
    lastDetectMs?: number
    averageDetectMs?: number
    // Other pipeline stages, moving averages in milliseconds: GPU readback of the camera
    // frame, pose solve with covariance, preview encoding, and exposure-to-publish latency.
    averageReadbackMs?: number
    // Where frames come from: "Native" (QuestX's camera client) or "Mruk" (Meta's camera
    // access). Native frames skip the GPU readback; averageDeliveryMs is exposure to arrival.
    captureBackend?: string
    averageDeliveryMs?: number
    averageSolveMs?: number
    averagePreviewMs?: number
    averageLatencyMs?: number
    // Camera images delivered, frames detected and measurements published, per second.
    cameraFps?: number
    processedFps?: number
    measurementFps?: number
    // Due camera frames dropped because detection was still busy.
    busyFrameCount?: number
  }

  passthroughStream: {
    captureFailureCount: number
    consecutiveCaptureFailures: number
    lastFrameAgeMs: number
    connectedClients: number
  }

  // Web Interface
  connectedClients: number

  // Camera arbitration: AprilTag detector wins over the passthrough stream when
  // both are enabled. The passthrough video keeps showing whatever frames the
  // camera is producing, but the resolution dropdown is locked.
  passthroughResolutionLockedByAprilTag: boolean
  effectivePassthroughResolution: { width: number, height: number } | null

  timestamp: number
}

// Pages in the icon rail, in rail order.
export type PageId = 'camera' | '3d' | 'status' | 'field' | 'logs' | 'settings'

// One entry in the Ctrl/Cmd+K command palette.
export interface Command {
  group: string
  label: string
  shortcut?: string
  run: () => void
}
