import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import ts from 'typescript'

// Transpile the production modules; mountMath imports the CameraOrientation enum from types.
async function load(path) {
  const source = await readFile(new URL(path, import.meta.url), 'utf8')
  return ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.ESNext } }).outputText
}
const toUrl = code => `data:text/javascript;base64,${Buffer.from(code).toString('base64')}`
const typesUrl = toUrl(await load('../src/types.ts'))
const mountMath = await import(toUrl((await load('../src/three/mountMath.ts')).replace(`'../types'`, `'${typesUrl}'`)))
const { CameraOrientation } = await import(typesUrl)
const { quatFromRollPitchYaw, mountQuat, rotate, yawOf, mountedOrientation, ORIENTATION_ROLL_DEGREES, frustumDirections, isValidRobotToQuest } = mountMath

const near = (a, b, message) => a.forEach((v, i) => assert.ok(Math.abs(v - b[i]) < 1e-9, `${message}: ${a} vs ${b}`))
const mount = (overrides = {}) => ({ x: 0, y: 0, z: 0, roll: 0, pitch: 0, yaw: 0, ...overrides })

function rx(a, [x, y, z]) { return [x, y * Math.cos(a) - z * Math.sin(a), y * Math.sin(a) + z * Math.cos(a)] }
function ry(a, [x, y, z]) { return [x * Math.cos(a) + z * Math.sin(a), y, -x * Math.sin(a) + z * Math.cos(a)] }
function rz(a, [x, y, z]) { return [x * Math.cos(a) - y * Math.sin(a), x * Math.sin(a) + y * Math.cos(a), z] }

test('roll, pitch, yaw match WPILib Rotation3d (extrinsic X, then Y, then Z)', () => {
  const [roll, pitch, yaw] = [0.3, -0.7, 2.1]
  const q = quatFromRollPitchYaw(roll, pitch, yaw)
  for (const v of [[1, 0, 0], [0, 1, 0], [0, 0, 1], [0.2, -0.5, 0.9]]) {
    near(rotate(q, v), rz(yaw, ry(pitch, rx(roll, v))), 'rotated vector')
  }
  assert.ok(Math.abs(yawOf(q) - yaw) < 1e-9)
})

test('WPILib sign conventions', () => {
  near(rotate(mountQuat(mount({ yaw: 90 })), [1, 0, 0]), [0, 1, 0], 'yaw +90 turns forward to the left')
  assert.ok(rotate(mountQuat(mount({ pitch: 20 })), [1, 0, 0])[2] < 0, 'positive pitch tilts the front down')
  near(rotate(mountQuat(mount({ roll: -90 })), [0, 0, 1]), [0, 1, 0], 'roll -90 points the top to the left')
})

test('the suggested roll for each stream orientation reads back as that orientation', () => {
  for (const orientation of [CameraOrientation.Normal, CameraOrientation.UpsideDown, CameraOrientation.Left, CameraOrientation.Right]) {
    const roll = ORIENTATION_ROLL_DEGREES[orientation]
    assert.equal(mountedOrientation(mount({ roll })), orientation)
    // Still recognized when the headset is tilted down and faces backward.
    assert.equal(mountedOrientation(mount({ roll, pitch: 25, yaw: 180 })), orientation)
  }
  assert.equal(mountedOrientation(mount({ pitch: 90 })), null, 'facing straight down has no meaningful roll')
})

test('frustum rays follow the pinhole model in WPILib camera axes', () => {
  const intrinsics = { fx: 500, fy: 500, cx: 320, cy: 240 }
  const [topLeft, topRight, bottomRight, bottomLeft] = frustumDirections(intrinsics, 640, 480)
  near(topLeft, [1, 0.64, 0.48], 'top-left is up and to the left')
  near(topRight, [1, -0.64, 0.48], 'top-right')
  near(bottomRight, [1, -0.64, -0.48], 'bottom-right')
  near(bottomLeft, [1, 0.64, -0.48], 'bottom-left')
})

test('robot-to-Quest validation matches the headset limits', () => {
  assert.ok(isValidRobotToQuest(mount({ x: 2, roll: -180 })))
  assert.ok(!isValidRobotToQuest(mount({ z: 2.01 })))
  assert.ok(!isValidRobotToQuest(mount({ yaw: 181 })))
  assert.ok(!isValidRobotToQuest(mount({ pitch: NaN })))
})
