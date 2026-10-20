// Dependency-free pose math shared by the 3D views and their tests. WPILib axes throughout:
// X forward, Y left, Z up; quaternions are (w, x, y, z).

import { CameraOrientation, type RobotToQuest } from '../types'

export interface Quat {
  w: number
  x: number
  y: number
  z: number
}

export type Vec3 = [number, number, number]

const DEG = Math.PI / 180

/** Same rotation as WPILib's `new Rotation3d(roll, pitch, yaw)`: extrinsic X, then Y, then Z. */
export function quatFromRollPitchYaw(roll: number, pitch: number, yaw: number): Quat {
  const cr = Math.cos(roll / 2), sr = Math.sin(roll / 2)
  const cp = Math.cos(pitch / 2), sp = Math.sin(pitch / 2)
  const cy = Math.cos(yaw / 2), sy = Math.sin(yaw / 2)
  return {
    w: cr * cp * cy + sr * sp * sy,
    x: sr * cp * cy - cr * sp * sy,
    y: cr * sp * cy + sr * cp * sy,
    z: cr * cp * sy - sr * sp * cy,
  }
}

export function mountQuat(mount: RobotToQuest): Quat {
  return quatFromRollPitchYaw(mount.roll * DEG, mount.pitch * DEG, mount.yaw * DEG)
}

export function rotate(q: Quat, v: Vec3): Vec3 {
  // v' = v + 2w(u × v) + 2u × (u × v)
  const ux = q.x, uy = q.y, uz = q.z
  const cx = uy * v[2] - uz * v[1]
  const cy = uz * v[0] - ux * v[2]
  const cz = ux * v[1] - uy * v[0]
  return [
    v[0] + 2 * (q.w * cx + uy * cz - uz * cy),
    v[1] + 2 * (q.w * cy + uz * cx - ux * cz),
    v[2] + 2 * (q.w * cz + ux * cy - uy * cx),
  ]
}

/** Yaw (rotation about Z) of a quaternion, as WPILib's Rotation3d.getZ(). */
export function yawOf(q: Quat): number {
  return Math.atan2(2 * (q.w * q.z + q.x * q.y), 1 - 2 * (q.y * q.y + q.z * q.z))
}

/** The roll each camera orientation implies for a headset mounted level and facing forward. */
export const ORIENTATION_ROLL_DEGREES: Record<CameraOrientation, number> = {
  [CameraOrientation.Normal]: 0,
  [CameraOrientation.UpsideDown]: 180,
  [CameraOrientation.Left]: -90,
  [CameraOrientation.Right]: 90,
}

/**
 * Which way the headset is rolled according to the mount, judged by which headset axis points
 * closest to the robot's up. Null when the headset faces mostly up or down, where roll is moot.
 */
export function mountedOrientation(mount: RobotToQuest): CameraOrientation | null {
  const q = mountQuat(mount)
  const top = rotate(q, [0, 0, 1])[2]
  const left = rotate(q, [0, 1, 0])[2]
  if (top > 0.5) return CameraOrientation.Normal
  if (top < -0.5) return CameraOrientation.UpsideDown
  // Rolled left (counter-clockwise from behind) puts the headset's left side down.
  if (left < -0.5) return CameraOrientation.Left
  if (left > 0.5) return CameraOrientation.Right
  return null
}

/**
 * Pixel-corner ray directions of a pinhole camera in WPILib camera axes, unit depth along X.
 * Order: top-left, top-right, bottom-right, bottom-left of the raw image.
 */
export function frustumDirections(
  intrinsics: { fx: number; fy: number; cx: number; cy: number },
  width: number,
  height: number,
): Vec3[] {
  const corners: [number, number][] = [[0, 0], [width, 0], [width, height], [0, height]]
  return corners.map(([u, v]) => [1, -(u - intrinsics.cx) / intrinsics.fx, -(v - intrinsics.cy) / intrinsics.fy])
}

export function isValidRobotToQuest(mount: RobotToQuest): boolean {
  const translation = [mount.x, mount.y, mount.z]
  const angles = [mount.roll, mount.pitch, mount.yaw]
  return translation.every(v => Number.isFinite(v) && Math.abs(v) <= 2) &&
    angles.every(v => Number.isFinite(v) && Math.abs(v) <= 180)
}
