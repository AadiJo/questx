// three.js scene for the 3D tab. Everything is drawn in WPILib axes (X forward, Y left,
// Z up, meters); the camera's up vector is +Z, so no axis conversion is needed.
//
// One chain is drawn in every view: robot -> headset (robot-to-Quest) -> passthrough
// camera (lens offset) -> detected tags (single-tag solves). Views differ only in where the
// robot is anchored: on its field pose, at the origin, or under a gravity-level headset.

import * as THREE from 'three'
import { OrbitControls } from 'three/examples/jsm/controls/OrbitControls.js'
import type { PoseJson, RobotToQuest, SceneLayout, SceneResponse, SceneTagStatus } from '../types'
import { TAG_CELLS, tag36h11Cells } from './aprilTag36h11'
import { buildFieldModel } from './fieldModels'
import { frustumDirections, mountQuat, yawOf } from './mountMath'
import { BUMPER_COLORS, RobotModel } from './robotModel'
import { makeQuestModel } from './questModel'

export type ViewMode = 'field' | 'headset' | 'robot'
export type FieldCameraPreset = 'perspective' | 'top' | 'follow'

export interface ViewerInput {
  scene: SceneResponse | null
  layout: SceneLayout | null
  // The mount to draw: the saved one, or the pending edit on the Robot view.
  mount: RobotToQuest
  robotSize: { length: number; width: number }
  // Bumper color for the alliance and the number printed on the bumpers.
  robotStyle: { bumperColor: number; teamNumber: number | null }
}

// Fixed dark palette matching the dashboard theme (style.css).
const PALETTE = {
  background: 0x0c0d0f,
  gridMajor: 0x3a3a3a,
  gridMinor: 0x2a2a2a,
  carpet: 0x2e3133,
  structure: 0x55595e,
  frustum: 0xc9ceff,
  trail: 0xf2f2f2,
} as const

const STATUS_COLORS: Record<SceneTagStatus, number> = {
  used: 0x6371f0,
  ignored: 0x8a8a8a,
  unknown: 0xd0453f,
}
const SEEN_COLOR = 0xe3a33b
const TRAIL_POINTS = 600
const MAX_RAYS = 32
// Lens offset to draw before the first detection frame reports the real one.
const DEFAULT_HEADSET_TO_CAMERA: PoseJson = { x: 0.05, y: 0, z: -0.02, qw: 1, qx: 0, qy: 0, qz: 0 }
const DEFAULT_FRUSTUM = { intrinsics: { fx: 880, fy: 880, cx: 640, cy: 640 }, width: 1280, height: 1280 }

export function poseMatrix(pose: PoseJson): THREE.Matrix4 {
  return new THREE.Matrix4().compose(
    new THREE.Vector3(pose.x, pose.y, pose.z),
    new THREE.Quaternion(pose.qx, pose.qy, pose.qz, pose.qw).normalize(),
    new THREE.Vector3(1, 1, 1),
  )
}

function mountMatrix(mount: RobotToQuest): THREE.Matrix4 {
  const q = mountQuat(mount)
  return new THREE.Matrix4().compose(
    new THREE.Vector3(mount.x, mount.y, mount.z),
    new THREE.Quaternion(q.x, q.y, q.z, q.w),
    new THREE.Vector3(1, 1, 1),
  )
}

function place(object: THREE.Object3D, matrix: THREE.Matrix4) {
  matrix.decompose(object.position, object.quaternion, object.scale)
}

// Tags are drawn as FIRST prints them: the tag with a one-cell white margin, and "ID n"
// centered one cell below the black square (2026 AprilTag Images and User Guide).
const SHEET_ROWS = TAG_CELLS + 2
const ID_CAP_TOP = TAG_CELLS // in cells from the top of the sheet
const ID_CAP_HEIGHT = 0.63

/** The printed sheet for a tag: its 36h11 pattern, or a numbered square outside the family. */
function tagTexture(id: number): THREE.CanvasTexture {
  const cell = 24
  const canvas = document.createElement('canvas')
  canvas.width = TAG_CELLS * cell
  canvas.height = SHEET_ROWS * cell
  const ctx = canvas.getContext('2d')!
  ctx.fillStyle = '#fff'
  ctx.fillRect(0, 0, canvas.width, canvas.height)
  ctx.fillStyle = '#0d0d0d'
  const cells = tag36h11Cells(id)
  if (cells) {
    cells.forEach((row, y) => row.forEach((white, x) => white || ctx.fillRect(x * cell, y * cell, cell, cell)))
  } else {
    const span = (TAG_CELLS - 2) * cell
    ctx.fillRect(cell, cell, span, span)
    ctx.fillStyle = '#fff'
    ctx.fillRect(2 * cell, 2 * cell, span - 2 * cell, span - 2 * cell)
    ctx.fillStyle = '#0d0d0d'
    ctx.font = `700 ${id >= 100 ? 2.6 * cell : 3.6 * cell}px sans-serif`
    ctx.textAlign = 'center'
    ctx.textBaseline = 'middle'
    ctx.fillText(String(id), canvas.width / 2, (TAG_CELLS / 2) * cell)
  }
  // Cap height is about 0.72 of the font size for these faces.
  ctx.font = `400 ${(ID_CAP_HEIGHT / 0.72) * cell}px "Segoe UI", "Helvetica Neue", Arial, sans-serif`
  ctx.textAlign = 'center'
  ctx.textBaseline = 'alphabetic'
  ctx.fillText(`ID ${id}`, canvas.width / 2, (ID_CAP_TOP + ID_CAP_HEIGHT) * cell)
  const texture = new THREE.CanvasTexture(canvas)
  texture.colorSpace = THREE.SRGBColorSpace
  texture.anisotropy = 4
  return texture
}

/**
 * Text sprite that always faces the camera. `height` is in meters, or with `screenSpace` a
 * constant on-screen size (about 2.4 * height of the viewport at the default field of view).
 */
class Label extends THREE.Sprite {
  private text = ''
  private readonly canvas = document.createElement('canvas')
  private readonly texture: THREE.CanvasTexture

  constructor(
    private readonly height = 0.06,
    screenSpace = false,
  ) {
    super()
    this.canvas.width = 256
    this.canvas.height = 64
    this.texture = new THREE.CanvasTexture(this.canvas)
    this.texture.colorSpace = THREE.SRGBColorSpace
    this.material = new THREE.SpriteMaterial({
      map: this.texture,
      depthTest: false,
      transparent: true,
      sizeAttenuation: !screenSpace,
    })
    this.renderOrder = 10
  }

  setText(text: string) {
    if (text === this.text) return
    this.text = text
    const ctx = this.canvas.getContext('2d')!
    ctx.font = '600 34px sans-serif'
    const width = Math.min(256, Math.ceil(ctx.measureText(text).width) + 24)
    ctx.clearRect(0, 0, 256, 64)
    ctx.fillStyle = 'rgba(12, 12, 12, 0.78)'
    ctx.beginPath()
    ctx.roundRect((256 - width) / 2, 6, width, 52, 10)
    ctx.fill()
    ctx.fillStyle = '#f7f7f4'
    ctx.textAlign = 'center'
    ctx.textBaseline = 'middle'
    ctx.fillText(text, 128, 33)
    this.texture.needsUpdate = true
    this.scale.set(this.height * 4, this.height, 1)
  }
}

/**
 * A tag sheet facing its +X (printed face) with an optional colored outline. `size` is the
 * black square's edge, as in the layout, and the tag's origin is that square's center; the
 * sheet adds the white margin and the ID line below.
 */
class TagMesh extends THREE.Group {
  private readonly outline: THREE.Mesh
  readonly label: Label

  constructor(id: number, size: number, labelHeight = 0.08, screenLabel = false) {
    super()
    const cell = size / (TAG_CELLS - 2)
    const width = TAG_CELLS * cell
    const height = SHEET_ROWS * cell
    // Outline width, drawn over the white margin so its inner edge is the black square's
    // edge: the quad the detector actually finds.
    const outlineWidth = cell * 0.6
    // Plane X -> tag +Y, plane Y -> tag +Z, plane normal -> tag +X, so the tag reads
    // correctly when looking at the printed face. The sheet extends below the square by the
    // two extra rows, so its center sits one cell under the tag's origin.
    const toTagFrame = new THREE.Matrix4().set(0, 0, 1, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1)
    const sheet = (w: number, h: number) =>
      new THREE.PlaneGeometry(w, h).applyMatrix4(toTagFrame).translate(0, 0, (TAG_CELLS - SHEET_ROWS) * cell / 2)
    const face = new THREE.Mesh(sheet(width, height), new THREE.MeshBasicMaterial({ map: tagTexture(id) }))
    const back = new THREE.Mesh(sheet(width, height).rotateZ(Math.PI), new THREE.MeshBasicMaterial({ color: 0x3a3a3a }))
    back.position.x = -0.002
    const outer = size / 2 + outlineWidth
    const inner = size / 2
    const frame = new THREE.Shape().moveTo(-outer, -outer).lineTo(outer, -outer).lineTo(outer, outer).lineTo(-outer, outer)
    frame.holes.push(new THREE.Path().moveTo(-inner, -inner).lineTo(-inner, inner).lineTo(inner, inner).lineTo(inner, -inner))
    this.outline = new THREE.Mesh(
      new THREE.ShapeGeometry(frame).applyMatrix4(toTagFrame),
      new THREE.MeshBasicMaterial({ color: SEEN_COLOR }),
    )
    this.outline.position.x = 0.001
    this.outline.visible = false
    this.label = new Label(labelHeight, screenLabel)
    this.label.position.set(0.02, 0, width / 2 + (screenLabel ? 0.12 : labelHeight * 0.75))
    this.label.visible = false
    this.add(face, back, this.outline, this.label)
  }

  setOutline(color: number | null) {
    this.outline.visible = color !== null
    if (color !== null) (this.outline.material as THREE.MeshBasicMaterial).color.setHex(color)
  }
}

/** Frees the GPU buffers and textures of an object that is no longer drawn. */
function disposeTree(root: THREE.Object3D) {
  root.traverse(object => {
    const mesh = object as THREE.Mesh
    mesh.geometry?.dispose()
    const materials = Array.isArray(mesh.material) ? mesh.material : mesh.material ? [mesh.material] : []
    for (const material of materials) {
      ;(material as THREE.MeshBasicMaterial).map?.dispose()
      material.dispose()
    }
  })
}

function setOpacity(group: THREE.Object3D, opacity: number) {
  group.traverse(object => {
    const material = (object as THREE.Mesh).material as THREE.Material | undefined
    if (material && 'opacity' in material) {
      material.opacity = opacity
      material.transparent = true
    }
  })
}

/** Camera field of view: outline plus a faint fill so it reads at any zoom. */
class Frustum extends THREE.Group {
  private readonly edges = new THREE.LineSegments(
    new THREE.BufferGeometry(),
    new THREE.LineBasicMaterial({ color: 0xc9ceff, transparent: true, opacity: 0.9 }),
  )
  private readonly fill = new THREE.Mesh(
    new THREE.BufferGeometry(),
    new THREE.MeshBasicMaterial({ color: 0xc9ceff, transparent: true, opacity: 0.12, side: THREE.DoubleSide, depthWrite: false }),
  )

  constructor() {
    super()
    this.add(this.fill, this.edges)
  }

  setColor(color: number) {
    ;(this.edges.material as THREE.LineBasicMaterial).color.setHex(color)
    ;(this.fill.material as THREE.MeshBasicMaterial).color.setHex(color)
  }

  setCamera(intrinsics: { fx: number; fy: number; cx: number; cy: number }, width: number, height: number, depth = 0.6) {
    const rays = frustumDirections(intrinsics, width, height).map(d => new THREE.Vector3(...d).multiplyScalar(depth))
    const origin = new THREE.Vector3()
    const lines: THREE.Vector3[] = []
    const faces: THREE.Vector3[] = []
    for (let i = 0; i < 4; i++) {
      const next = rays[(i + 1) % 4]
      lines.push(origin, rays[i], rays[i], next)
      faces.push(origin, rays[i], next)
    }
    this.edges.geometry.dispose()
    this.edges.geometry = new THREE.BufferGeometry().setFromPoints(lines)
    this.fill.geometry.dispose()
    this.fill.geometry = new THREE.BufferGeometry().setFromPoints(faces)
  }
}

export class SceneViewer {
  private readonly renderer: THREE.WebGLRenderer
  private readonly scene = new THREE.Scene()
  private readonly camera = new THREE.PerspectiveCamera(50, 1, 0.01, 200)
  private readonly controls: OrbitControls
  private readonly resizeObserver: ResizeObserver
  private frameRequest: number | null = null
  private active = false
  private disposed = false

  private mode: ViewMode = 'field'
  private fieldPreset: FieldCameraPreset = 'perspective'

  // Field view
  private readonly fieldGroup = new THREE.Group()
  private readonly fieldTags = new Map<number, TagMesh>()
  private fieldDimensions = { length: 16.541, width: 8.069 }
  private layoutKey = ''
  /** Game whose elements are drawn, or null for carpet and perimeter only. */
  fieldGame: string | null = null
  private readonly ghost = new RobotModel(false)
  private readonly rayPositions = new Float32Array(MAX_RAYS * 6)
  private readonly rays = new THREE.LineSegments(
    new THREE.BufferGeometry().setAttribute('position', new THREE.BufferAttribute(this.rayPositions, 3)),
    new THREE.LineBasicMaterial({ color: SEEN_COLOR, transparent: true, opacity: 0.85 }),
  )
  private readonly trail: THREE.Line
  private readonly trailPositions = new Float32Array(TRAIL_POINTS * 3)
  private trailCount = 0
  private trailEpoch = -1
  private followTarget: THREE.Vector3 | null = null
  // Until the user orbits, resizes re-fit the preset, including when returning to the tab.
  private cameraTouched = false

  // The robot -> headset -> camera -> tags chain, shared by every view.
  private readonly robot = new RobotModel(true)
  private readonly headset = makeQuestModel()
  private readonly cameraNode = new THREE.Group()
  private readonly frustum = new Frustum()
  private readonly detectedGroup = new THREE.Group()
  private readonly detectedTags = new Map<number, TagMesh>()
  private readonly grid: THREE.GridHelper
  private tagSize = 0.1651
  private headsetToCamera = poseMatrix(DEFAULT_HEADSET_TO_CAMERA)
  private frustumKey = ''

  constructor(private readonly container: HTMLElement) {
    this.renderer = new THREE.WebGLRenderer({ antialias: true })
    this.renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2))
    this.renderer.outputColorSpace = THREE.SRGBColorSpace
    container.appendChild(this.renderer.domElement)

    this.camera.up.set(0, 0, 1)
    this.controls = new OrbitControls(this.camera, this.renderer.domElement)
    this.controls.enabled = false
    this.controls.enableDamping = true
    this.controls.dampingFactor = 0.12
    this.controls.addEventListener('start', () => {
      this.cameraTouched = true
    })

    this.scene.add(new THREE.HemisphereLight(0xffffff, 0x404040, 2.2))
    const sun = new THREE.DirectionalLight(0xffffff, 1.2)
    sun.position.set(3, -4, 8)
    this.scene.add(sun)

    this.grid = new THREE.GridHelper(6, 24, PALETTE.gridMajor, PALETTE.gridMinor)
    this.grid.rotateX(Math.PI / 2)

    const trailGeometry = new THREE.BufferGeometry()
    trailGeometry.setAttribute('position', new THREE.BufferAttribute(this.trailPositions, 3))
    trailGeometry.setDrawRange(0, 0)
    this.trail = new THREE.Line(trailGeometry, new THREE.LineBasicMaterial({ color: PALETTE.trail, transparent: true, opacity: 0.6 }))
    this.trail.frustumCulled = false
    this.rays.frustumCulled = false

    this.cameraNode.add(this.frustum)
    this.headset.add(this.cameraNode)
    this.robot.add(this.headset)
    this.scene.add(this.fieldGroup, this.grid, this.trail, this.ghost, this.rays, this.robot, this.detectedGroup)

    this.scene.background = new THREE.Color(PALETTE.background)
    this.frustum.setColor(PALETTE.frustum)

    this.resizeObserver = new ResizeObserver(() => this.resize())
    this.resizeObserver.observe(container)
    this.resize()
    this.setMode('field')
  }

  /** Pause without losing camera/scene state; a new viewer starts paused. */
  setActive(active: boolean) {
    if (this.disposed || this.active === active) return
    this.active = active
    this.controls.enabled = active
    if (active) {
      this.resize()
      this.frameRequest = requestAnimationFrame(this.loop)
    } else {
      if (this.frameRequest !== null) cancelAnimationFrame(this.frameRequest)
      this.frameRequest = null
    }
  }

  dispose() {
    this.setActive(false)
    this.disposed = true
    this.resizeObserver.disconnect()
    this.controls.dispose()
    disposeTree(this.scene)
    this.renderer.dispose()
    this.renderer.domElement.remove()
  }

  setMode(mode: ViewMode) {
    this.mode = mode
    this.fieldGroup.visible = mode === 'field'
    this.trail.visible = mode === 'field'
    this.grid.visible = mode !== 'field'
    this.resetCamera()
  }

  setFieldPreset(preset: FieldCameraPreset) {
    this.fieldPreset = preset
    if (this.mode === 'field') this.resetCamera()
  }

  clearTrail() {
    this.trailCount = 0
    this.trail.geometry.setDrawRange(0, 0)
  }

  resetCamera() {
    this.followTarget = null
    this.cameraTouched = false
    if (this.mode === 'field') {
      const { length, width } = this.fieldDimensions
      const center = new THREE.Vector3(length / 2, width / 2, 0)
      if (this.fieldPreset === 'top') {
        this.camera.position.set(center.x, center.y - 0.01, Math.max(length, width) * 0.95)
      } else {
        // From the scoring-table side at 40° elevation, far enough back that the whole
        // field length fits the current horizontal field of view.
        const halfVertical = THREE.MathUtils.degToRad(this.camera.fov / 2)
        const halfHorizontal = Math.atan(Math.tan(halfVertical) * this.camera.aspect)
        const elevation = THREE.MathUtils.degToRad(40)
        // Seen at this elevation, the field's depth plus the 2 m walls spans about this much height.
        const apparentHeight = width * Math.sin(elevation) + 2 * Math.cos(elevation)
        const distance = Math.max(
          // The near corners are closer than the center, so they set the width.
          (length / 2 + 0.6) / Math.tan(halfHorizontal) + (width / 2) * Math.cos(elevation),
          (apparentHeight / 2 + 0.4) / Math.tan(halfVertical),
        )
        this.camera.position.set(center.x, center.y - distance * Math.cos(elevation), distance * Math.sin(elevation))
      }
      this.controls.target.copy(center)
    } else if (this.mode === 'headset') {
      this.camera.position.set(-1.5, -1.1, 0.9)
      this.controls.target.set(1.2, 0, 0.1)
    } else {
      this.camera.position.set(-1.7, -1.6, 1.5)
      this.controls.target.set(0.5, 0, 0.35)
    }
    this.controls.update()
  }

  update(input: ViewerInput) {
    const { scene, layout } = input
    this.updateLayout(layout)

    const detections = scene?.detections ?? null
    if (detections) {
      this.headsetToCamera = poseMatrix(detections.headsetToCamera)
      const key = `${detections.width}x${detections.height}:${detections.intrinsics.fx}`
      if (key !== this.frustumKey) {
        this.frustumKey = key
        this.frustum.setCamera(detections.intrinsics, detections.width, detections.height)
      }
    } else if (!this.frustumKey) {
      this.frustumKey = 'default'
      this.frustum.setCamera(DEFAULT_FRUSTUM.intrinsics, DEFAULT_FRUSTUM.width, DEFAULT_FRUSTUM.height)
    }

    const mount = mountMatrix(input.mount)
    const { length, width } = input.robotSize
    this.robot.setStyle({ length, width, ...input.robotStyle })
    this.ghost.setStyle({ length, width, bumperColor: BUMPER_COLORS.solve, teamNumber: null })
    place(this.headset, mount)
    place(this.cameraNode, this.headsetToCamera)

    // Where the robot is drawn in this view.
    let robotWorld: THREE.Matrix4 | null = null
    if (this.mode === 'field') {
      const pose = scene?.field.estimate ?? scene?.field.measurement?.robotPose ?? null
      robotWorld = pose ? poseMatrix(pose) : null
    } else if (this.mode === 'robot') {
      robotWorld = new THREE.Matrix4()
    } else {
      // Gravity-level frame at the headset, turned to the headset's heading.
      const headsetPose = scene?.vio?.headsetPose ?? null
      const level = new THREE.Matrix4()
      if (headsetPose) {
        const q = new THREE.Quaternion(headsetPose.qx, headsetPose.qy, headsetPose.qz, headsetPose.qw).normalize()
        const yaw = yawOf({ w: q.w, x: q.x, y: q.y, z: q.z })
        level.makeRotationFromQuaternion(
          new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(0, 0, 1), -yaw).multiply(q),
        )
      }
      robotWorld = level.multiply(mount.clone().invert())
    }

    this.robot.visible = robotWorld !== null
    if (robotWorld) place(this.robot, robotWorld)
    this.robot.updateMatrixWorld(true)
    // In the headset view the robot box is context, not the subject.
    setOpacity(this.robot.getObjectByName('body')!, this.mode === 'headset' ? 0.25 : 0.9)

    if (this.mode !== 'field' && robotWorld) {
      // Floor under the robot origin.
      this.grid.position.set(robotWorld.elements[12], robotWorld.elements[13], robotWorld.elements[14])
    }

    this.updateDetectedTags(scene, robotWorld !== null && this.mode !== 'field')
    this.updateFieldOverlays(scene, robotWorld)
  }

  private updateLayout(layout: SceneLayout | null) {
    if (!layout) return
    const key = `${layout.file}:${layout.tags.length}:${layout.tagSize}`
    if (key === this.layoutKey) return
    this.layoutKey = key
    this.tagSize = layout.tagSize || this.tagSize
    for (const child of [...this.fieldGroup.children]) {
      this.fieldGroup.remove(child)
      disposeTree(child)
    }
    this.fieldTags.clear()
    if (layout.field) this.fieldDimensions = { length: layout.field.length, width: layout.field.width }
    const model = buildFieldModel(layout, { carpet: new THREE.Color(PALETTE.carpet), structure: PALETTE.structure })
    this.fieldGame = model.game
    this.fieldGroup.add(model.group)

    for (const tag of layout.tags) {
      // Field labels keep a constant on-screen size, readable across the field and up close.
      const mesh = new TagMesh(tag.id, this.tagSize, 0.03, true)
      mesh.label.setText(String(tag.id))
      place(mesh, poseMatrix(tag.pose))
      this.fieldTags.set(tag.id, mesh)
      this.fieldGroup.add(mesh)
    }
    if (this.mode === 'field') this.resetCamera()
  }

  private updateDetectedTags(scene: SceneResponse | null, visible: boolean) {
    const tags = scene?.detections?.tags ?? []
    const seen = new Set<number>()
    if (visible) {
      const cameraWorld = this.cameraNode.matrixWorld
      for (const tag of tags) {
        if (!tag.cameraToTag) continue
        seen.add(tag.id)
        let mesh = this.detectedTags.get(tag.id)
        if (!mesh) {
          mesh = new TagMesh(tag.id, this.tagSize)
          this.detectedTags.set(tag.id, mesh)
          this.detectedGroup.add(mesh)
        }
        const world = cameraWorld.clone().multiply(poseMatrix(tag.cameraToTag))
        place(mesh, world)
        mesh.setOutline(STATUS_COLORS[tag.status])
        const distance = Math.hypot(tag.cameraToTag.x, tag.cameraToTag.y, tag.cameraToTag.z)
        mesh.label.setText(`${tag.id} · ${distance.toFixed(2)} m`)
        mesh.label.visible = true
      }
    }
    for (const [id, mesh] of this.detectedTags) mesh.visible = seen.has(id)
  }

  private updateFieldOverlays(scene: SceneResponse | null, robotWorld: THREE.Matrix4 | null) {
    const field = this.mode === 'field'
    const measurement = scene?.field.measurement ?? null
    const tags = scene?.detections?.tags ?? []

    // Highlight layout tags: green when used by a recent accepted solve, amber when seen now.
    const usedRecently = new Set(measurement && measurement.ageMs < 1500 ? measurement.tagIds : [])
    const seenNow = new Set(tags.map(t => t.id))
    for (const [id, mesh] of this.fieldTags) {
      const highlight = usedRecently.has(id) ? STATUS_COLORS.used : seenNow.has(id) ? SEEN_COLOR : null
      mesh.setOutline(highlight)
      // The pattern does not show the ID, so name the tags the headset is working with.
      mesh.label.visible = field && highlight !== null
    }

    // The raw tag solve, fading over a few seconds.
    const ghostVisible = field && measurement !== null && measurement.ageMs < 5000
    this.ghost.visible = ghostVisible
    if (ghostVisible && measurement) {
      place(this.ghost, poseMatrix(measurement.robotPose))
      setOpacity(this.ghost, Math.max(0.25, 1 - measurement.ageMs / 5000))
    }

    // Lines from the camera to every seen tag that is in the layout.
    let rayCount = 0
    if (field && robotWorld) {
      const camera = new THREE.Vector3().setFromMatrixPosition(this.cameraNode.matrixWorld)
      for (const id of seenNow) {
        const mesh = this.fieldTags.get(id)
        if (!mesh || rayCount === MAX_RAYS) continue
        this.rayPositions.set([camera.x, camera.y, camera.z, mesh.position.x, mesh.position.y, mesh.position.z], rayCount * 6)
        rayCount++
      }
    }
    this.rays.geometry.setDrawRange(0, rayCount * 2)
    ;(this.rays.geometry.getAttribute('position') as THREE.BufferAttribute).needsUpdate = true
    this.rays.visible = rayCount > 0

    // Trail of the field estimate; restarted when the VIO epoch changes.
    const estimate = scene?.field.estimate ?? null
    const epoch = scene?.vio?.epoch ?? -1
    if (estimate) {
      if (epoch !== this.trailEpoch) {
        this.trailEpoch = epoch
        this.clearTrail()
      }
      const last = this.trailCount - 1
      const dx = last >= 0 ? estimate.x - this.trailPositions[last * 3] : 1
      const dy = last >= 0 ? estimate.y - this.trailPositions[last * 3 + 1] : 1
      if (Math.hypot(dx, dy) > 0.02) {
        if (this.trailCount === TRAIL_POINTS) {
          this.trailPositions.copyWithin(0, 3)
          this.trailCount--
        }
        this.trailPositions.set([estimate.x, estimate.y, 0.01], this.trailCount * 3)
        this.trailCount++
        const attribute = this.trail.geometry.getAttribute('position') as THREE.BufferAttribute
        attribute.needsUpdate = true
        this.trail.geometry.setDrawRange(0, this.trailCount)
        this.trail.geometry.computeBoundingSphere()
      }
    }

    // Follow the robot without fighting the user's orbit.
    if (field && this.fieldPreset === 'follow' && robotWorld) {
      const position = new THREE.Vector3().setFromMatrixPosition(robotWorld)
      if (this.followTarget) {
        const delta = position.clone().sub(this.followTarget)
        this.camera.position.add(delta)
        this.controls.target.add(delta)
      } else {
        this.camera.position.set(position.x - 2.6, position.y - 2.2, 2.4)
        this.controls.target.copy(position)
      }
      this.followTarget = position
    }
  }


  private resize() {
    const width = this.container.clientWidth
    const height = this.container.clientHeight
    if (width === 0 || height === 0) return
    this.renderer.setSize(width, height, false)
    this.camera.aspect = width / height
    this.camera.updateProjectionMatrix()
    if (!this.cameraTouched) this.resetCamera()
  }

  private loop = () => {
    if (!this.active || this.disposed) return
    this.frameRequest = requestAnimationFrame(this.loop)
    this.controls.update()
    this.renderer.render(this.scene, this.camera)
  }
}
