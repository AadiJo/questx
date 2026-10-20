// Robot drawn like Limelight's field visualizer: a dark chassis wrapped in bumpers in the
// alliance color, with the team number printed on every side. WPILib axes, origin at the
// robot center on the floor, +X forward.

import * as THREE from 'three'

export const BUMPER_HEIGHT = 0.13
const BUMPER_THICKNESS = 0.085
const BUMPER_BOTTOM = 0.02
const CHASSIS_HEIGHT = 0.2

export const BUMPER_COLORS = {
  red: 0xd21414,
  blue: 0x1438d2,
  // Limelight draws its MegaTag1 robot with yellow bumpers; the raw tag solve uses the same.
  solve: 0xf2c200,
} as const

export interface RobotStyle {
  length: number
  width: number
  bumperColor: number
  teamNumber: number | null
}

/** White bumper numbers on a transparent background, sized for a face of the given aspect. */
function numberTexture(text: string, aspect: number): THREE.CanvasTexture {
  const height = 64
  const canvas = document.createElement('canvas')
  canvas.height = height
  canvas.width = Math.max(64, Math.round(height * aspect))
  const ctx = canvas.getContext('2d')!
  ctx.fillStyle = '#ffffff'
  ctx.font = `800 ${Math.round(height * 0.78)}px "Helvetica Neue", Arial, sans-serif`
  ctx.textAlign = 'center'
  ctx.textBaseline = 'middle'
  ctx.fillText(text, canvas.width / 2, height / 2 + 2)
  const texture = new THREE.CanvasTexture(canvas)
  texture.colorSpace = THREE.SRGBColorSpace
  texture.anisotropy = 4
  return texture
}

function disposeChildren(group: THREE.Group) {
  for (const child of [...group.children]) {
    group.remove(child)
    child.traverse(object => {
      const mesh = object as THREE.Mesh
      mesh.geometry?.dispose()
      const materials = Array.isArray(mesh.material) ? mesh.material : mesh.material ? [mesh.material] : []
      for (const material of materials) {
        ;(material as THREE.MeshBasicMaterial).map?.dispose()
        material.dispose()
      }
    })
  }
}

export class RobotModel extends THREE.Group {
  /** Everything visible; its materials are faded together (see SceneViewer's setOpacity). */
  readonly body = new THREE.Group()
  private styleKey = ''

  constructor(private readonly showChassis = true) {
    super()
    this.body.name = 'body'
    this.add(this.body)
  }

  /** Rebuilds the meshes only when the size, color, or number changes. */
  setStyle(style: RobotStyle) {
    const key = `${style.length}:${style.width}:${style.bumperColor}:${style.teamNumber}`
    if (key === this.styleKey) return
    this.styleKey = key
    disposeChildren(this.body)

    const length = Math.max(0.3, style.length)
    const width = Math.max(0.3, style.width)
    const bumper = new THREE.MeshLambertMaterial({ color: style.bumperColor, transparent: true })
    const z = BUMPER_BOTTOM + BUMPER_HEIGHT / 2

    // Front and back bumpers span the full width; sides fit between them.
    for (const sign of [1, -1]) {
      const frontBack = new THREE.Mesh(new THREE.BoxGeometry(BUMPER_THICKNESS, width, BUMPER_HEIGHT), bumper)
      frontBack.position.set(sign * (length / 2 - BUMPER_THICKNESS / 2), 0, z)
      const side = new THREE.Mesh(
        new THREE.BoxGeometry(length - 2 * BUMPER_THICKNESS, BUMPER_THICKNESS, BUMPER_HEIGHT),
        bumper,
      )
      side.position.set(0, sign * (width / 2 - BUMPER_THICKNESS / 2), z)
      this.body.add(frontBack, side)
    }

    if (this.showChassis) {
      const chassis = new THREE.Mesh(
        new THREE.BoxGeometry(length - 2 * BUMPER_THICKNESS, width - 2 * BUMPER_THICKNESS, CHASSIS_HEIGHT),
        new THREE.MeshLambertMaterial({ color: 0x2b2d30, transparent: true }),
      )
      chassis.position.z = BUMPER_BOTTOM + CHASSIS_HEIGHT / 2
      this.body.add(chassis)
    }

    // Heading arrow on top, pointing along +X.
    const arrow = new THREE.Shape()
    arrow.moveTo(Math.min(0.42, length / 2 - BUMPER_THICKNESS - 0.02), 0)
    arrow.lineTo(0.06, 0.18)
    arrow.lineTo(0.06, -0.18)
    arrow.closePath()
    const arrowMesh = new THREE.Mesh(
      new THREE.ShapeGeometry(arrow),
      new THREE.MeshBasicMaterial({ color: 0xf2f2f2, transparent: true, side: THREE.DoubleSide }),
    )
    arrowMesh.position.z = (this.showChassis ? BUMPER_BOTTOM + CHASSIS_HEIGHT : BUMPER_BOTTOM + BUMPER_HEIGHT) + 0.003
    this.body.add(arrowMesh)

    // Team number on all four bumper faces, reading left to right from outside.
    if (style.teamNumber !== null && style.teamNumber > 0) {
      const text = String(style.teamNumber)
      const faces: { span: number; normal: THREE.Vector3 }[] = [
        { span: width, normal: new THREE.Vector3(1, 0, 0) },
        { span: width, normal: new THREE.Vector3(-1, 0, 0) },
        { span: length, normal: new THREE.Vector3(0, 1, 0) },
        { span: length, normal: new THREE.Vector3(0, -1, 0) },
      ]
      for (const face of faces) {
        const labelWidth = Math.min(face.span * 0.8, BUMPER_HEIGHT * 0.8 * text.length * 0.7 + 0.08)
        const labelHeight = BUMPER_HEIGHT * 0.8
        const label = new THREE.Mesh(
          new THREE.PlaneGeometry(labelWidth, labelHeight),
          new THREE.MeshBasicMaterial({
            map: numberTexture(text, labelWidth / labelHeight),
            transparent: true,
            depthWrite: false,
          }),
        )
        // The plane faces +Z; turn it to face outward, upright.
        const half = face.normal.x !== 0 ? length / 2 : width / 2
        label.position.copy(face.normal).multiplyScalar(half + 0.002)
        label.position.z = z
        label.quaternion.setFromRotationMatrix(
          new THREE.Matrix4().makeBasis(
            new THREE.Vector3(0, 0, 1).cross(face.normal).normalize(),
            new THREE.Vector3(0, 0, 1),
            face.normal,
          ),
        )
        this.body.add(label)
      }
    }
  }
}
