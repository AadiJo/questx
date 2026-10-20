// Simplified field models for the 3D tab's field view, in WPILib field coordinates.
//
// Game elements are anchored on their own AprilTags from the loaded layout, so welded and
// AndyMark variants line up with the tags the headset actually solves against. An element
// whose tags are missing from the layout is skipped. Sizes are the game manuals' nominal
// dimensions; shapes are simplified. Any other layout gets the carpet and perimeter only.

import * as THREE from 'three'
import type { SceneLayout } from '../types'

const IN = 0.0254
// Saturated alliance colors, as in Limelight's field visualizer.
const BLUE = 0x1438d2
const RED = 0xd21414
const ALUMINUM = 0x9ba1a8
// Structure color; buildFieldModel swaps in a lighter one on dark backgrounds.
let DARK = 0x3a3d42
const POLYCARBONATE = 0xffffff
const EDGE = 0xb8b8b8
const TAPE_WHITE = 0xf2f2ee

type Alliance = 'blue' | 'red'
const allianceColor = (alliance: Alliance) => (alliance === 'blue' ? BLUE : RED)

interface TagPoint {
  x: number
  y: number
  z: number
  yaw: number
}

class Materials {
  private readonly cache = new Map<string, THREE.Material>()

  solid(color: number, opacity = 1): THREE.Material {
    const key = `s${color}:${opacity}`
    let material = this.cache.get(key)
    if (!material) {
      material = new THREE.MeshLambertMaterial({ color, transparent: opacity < 1, opacity, depthWrite: opacity >= 1 })
      this.cache.set(key, material)
    }
    return material
  }

  flat(color: number, opacity = 1): THREE.Material {
    const key = `f${color}:${opacity}`
    let material = this.cache.get(key)
    if (!material) {
      material = new THREE.MeshBasicMaterial({ color, transparent: opacity < 1, opacity, side: THREE.DoubleSide })
      this.cache.set(key, material)
    }
    return material
  }
}

/** Box with its footprint centered on (x, y) and its bottom at z. */
function box(material: THREE.Material, w: number, d: number, h: number, x: number, y: number, z = 0): THREE.Mesh {
  const mesh = new THREE.Mesh(new THREE.BoxGeometry(w, d, h), material)
  mesh.position.set(x, y, z + h / 2)
  return mesh
}

/** Floor tape: a thin quad from (x0, y0) to (x1, y1), 2 in wide. */
function tape(material: THREE.Material, x0: number, y0: number, x1: number, y1: number): THREE.Mesh {
  const length = Math.hypot(x1 - x0, y1 - y0)
  const mesh = new THREE.Mesh(new THREE.PlaneGeometry(length, 2 * IN), material)
  mesh.position.set((x0 + x1) / 2, (y0 + y1) / 2, 0.002)
  mesh.rotation.z = Math.atan2(y1 - y0, x1 - x0)
  return mesh
}

/** Horizontal rod along the local Y axis. */
function rodY(material: THREE.Material, radius: number, length: number, x: number, y: number, z: number): THREE.Mesh {
  const mesh = new THREE.Mesh(new THREE.CylinderGeometry(radius, radius, length, 12), material)
  mesh.position.set(x, y, z)
  return mesh
}

function tagPoints(layout: SceneLayout): Map<number, TagPoint> {
  const points = new Map<number, TagPoint>()
  for (const tag of layout.tags) {
    const q = tag.pose
    const yaw = Math.atan2(2 * (q.qw * q.qz + q.qx * q.qy), 1 - 2 * (q.qy * q.qy + q.qz * q.qz))
    points.set(tag.id, { x: q.x, y: q.y, z: q.z, yaw })
  }
  return points
}

/** Tags with every listed ID, or null if any is missing. */
function find(points: Map<number, TagPoint>, ids: number[]): TagPoint[] | null {
  const found = ids.map(id => points.get(id))
  return found.every(Boolean) ? (found as TagPoint[]) : null
}

function center(points: TagPoint[]) {
  const xs = points.map(p => p.x)
  const ys = points.map(p => p.y)
  return {
    x: (Math.min(...xs) + Math.max(...xs)) / 2,
    y: (Math.min(...ys) + Math.max(...ys)) / 2,
  }
}

/** Carpet markings, guardrails, and alliance walls shared by every field. */
/** Translucent panel with light outline edges, like Limelight's guardrails. */
function panel(m: Materials, w: number, d: number, h: number, x: number, y: number, z = 0): THREE.Group {
  const group = new THREE.Group()
  const mesh = box(m.solid(POLYCARBONATE, 0.07), w, d, h, x, y, z)
  const edges = new THREE.LineSegments(
    new THREE.EdgesGeometry(mesh.geometry),
    new THREE.LineBasicMaterial({ color: EDGE, transparent: true, opacity: 0.7 }),
  )
  edges.position.copy(mesh.position)
  group.add(mesh, edges)
  return group
}

/** Floor grid around the field, guardrails, and alliance walls shared by every field. */
function buildPerimeter(group: THREE.Group, m: Materials, length: number, width: number) {
  const gridSize = Math.ceil(Math.max(length, width) * 2.5)
  const grid = new THREE.GridHelper(gridSize, gridSize, 0x2c2c2c, 0x242424)
  grid.rotateX(Math.PI / 2)
  grid.position.set(length / 2, width / 2, -0.004)
  group.add(grid)

  const railHeight = 20 * IN
  for (const y of [0, width]) {
    const outward = y === 0 ? -1 : 1
    group.add(panel(m, length, 0.012, railHeight, length / 2, y + outward * 0.006))
  }

  // Alliance walls: a solid alliance-colored base (the driver station's diamond plate)
  // under a clear upper panel.
  const baseHeight = 36.8 * IN
  const clearHeight = 42 * IN
  for (const [x, alliance] of [[0, 'blue'], [length, 'red']] as const) {
    const outward = x === 0 ? -1 : 1
    const wallX = x + outward * 0.04
    group.add(box(m.solid(allianceColor(alliance)), 0.08, width, baseHeight, wallX, width / 2))
    group.add(panel(m, 0.02, width, clearHeight, wallX, width / 2, baseHeight))
  }
}

function buildRebuilt(group: THREE.Group, m: Materials, points: Map<number, TagPoint>, length: number, width: number): boolean {
  const alliances: {
    alliance: Alliance
    hub: number[]
    trenches: number[][]
    tower: number[]
    outpost: number[]
  }[] = [
    { alliance: 'blue', hub: [18, 19, 20, 21, 24, 25, 26, 27], trenches: [[17, 28], [22, 23]], tower: [31, 32], outpost: [29, 30] },
    { alliance: 'red', hub: [2, 3, 4, 5, 8, 9, 10, 11], trenches: [[6, 7], [1, 12]], tower: [15, 16], outpost: [13, 14] },
  ]
  let built = false
  const tapeWhite = m.flat(TAPE_WHITE)
  group.add(tape(tapeWhite, length / 2, 0, length / 2, width))

  for (const side of alliances) {
    const color = allianceColor(side.alliance)
    const hubTags = find(points, side.hub)
    if (!hubTags) continue
    built = true
    const hub = center(hubTags)
    const half = 47 * IN / 2
    // Toward this alliance's wall.
    const home = side.alliance === 'blue' ? -1 : 1

    // ALLIANCE ZONE line on the HUB's near face.
    group.add(tape(m.flat(color), hub.x + home * half, 0, hub.x + home * half, width))

    // HUB: 47 in square body with a flared hexagonal opening whose front edge is 72 in up.
    const bodyHeight = 1.35
    group.add(box(m.solid(DARK), 2 * half, 2 * half, bodyHeight, hub.x, hub.y))
    group.add(box(m.solid(color, 0.9), 2 * half + 0.006, 2 * half + 0.006, 0.15, hub.x, hub.y, 0.15))
    const funnel = new THREE.Mesh(
      new THREE.CylinderGeometry(0.6, 0.5, 72 * IN - bodyHeight, 6, 1, true).rotateX(Math.PI / 2),
      m.solid(ALUMINUM, 0.55),
    )
    funnel.position.set(hub.x, hub.y, bodyHeight + (72 * IN - bodyHeight) / 2)
    group.add(funnel)

    // TRENCHES (guardrail to BUMP) and BUMPS (TRENCH to HUB), centered on the HUB's x.
    const trenchWidth = 65.65 * IN
    const trenchDepth = 47 * IN
    const legWidth = (trenchWidth - 50.34 * IN) / 2
    const bumpHeight = 6.513 * IN
    for (const ids of side.trenches) {
      const tags = find(points, ids)
      if (!tags) continue
      const x = center(tags).x
      const fromLow = center(tags).y < width / 2
      const wall = fromLow ? 0 : width
      const inward = fromLow ? 1 : -1
      const trench = new THREE.Group()
      trench.add(box(m.solid(ALUMINUM), trenchDepth, legWidth, 40.25 * IN, x, wall + inward * legWidth / 2))
      trench.add(box(m.solid(ALUMINUM), trenchDepth, legWidth, 40.25 * IN, x, wall + inward * (trenchWidth - legWidth / 2)))
      // The arm the tags are mounted on, clearing 22.25 in underneath.
      trench.add(box(m.solid(color, 0.92), 0.06, trenchWidth, (40.25 - 22.25) * IN, x, wall + inward * trenchWidth / 2, 22.25 * IN))
      group.add(trench)

      // BUMP between the TRENCH and the HUB, ramped 15° toward both zones.
      const bumpFrom = wall + inward * trenchWidth
      const bumpTo = hub.y - inward * half
      const bumpWidth = Math.abs(bumpTo - bumpFrom)
      const run = Math.min(bumpHeight / Math.tan(15 * Math.PI / 180), 44.4 * IN / 2 - 0.02)
      const profile = new THREE.Shape()
      profile.moveTo(-44.4 * IN / 2, 0)
      profile.lineTo(-44.4 * IN / 2 + run, bumpHeight)
      profile.lineTo(44.4 * IN / 2 - run, bumpHeight)
      profile.lineTo(44.4 * IN / 2, 0)
      profile.closePath()
      const bump = new THREE.Mesh(
        new THREE.ExtrudeGeometry(profile, { depth: bumpWidth, bevelEnabled: false })
          .rotateX(Math.PI / 2)
          .translate(0, bumpWidth / 2, 0),
        m.solid(color, 0.75),
      )
      bump.position.set(hub.x, (bumpFrom + bumpTo) / 2, 0)
      group.add(bump)
    }

    // TOWER: base out from the TOWER WALL, two uprights, three rungs.
    const towerTags = find(points, side.tower)
    if (towerTags) {
      const anchor = center(towerTags)
      const tower = new THREE.Group()
      tower.position.set(towerTags[0].x, anchor.y, 0)
      tower.rotation.z = towerTags[0].yaw
      const depth = 45 * IN
      tower.add(box(m.solid(DARK), depth, 39 * IN, 0.05, depth / 2, 0))
      // Backing panel just behind the tag plane (local x = 0) so it never covers the tags.
      tower.add(box(m.solid(color, 0.9), 0.05, 49.25 * IN, 78.25 * IN, -0.03, 0))
      const spacing = 32.25 * IN
      const uprightX = depth - 3.5 * IN
      for (const y of [-spacing / 2, spacing / 2]) {
        tower.add(box(m.solid(ALUMINUM), 3.5 * IN, 1.5 * IN, 72.1 * IN, uprightX, y))
      }
      for (const height of [27, 45, 63]) {
        tower.add(rodY(m.solid(ALUMINUM), 0.8 * IN, spacing + 2 * 5.875 * IN, uprightX, 0, height * IN))
      }
      group.add(tower)
    }

    // OUTPOST: chute and openings in the corner where the guardrail meets the ALLIANCE WALL.
    const outpostTags = find(points, side.outpost)
    if (outpostTags) {
      const anchor = center(outpostTags)
      const outpost = new THREE.Group()
      outpost.position.set(outpostTags[0].x, anchor.y, 0)
      outpost.rotation.z = outpostTags[0].yaw
      // Panel and openings stay behind the tag plane (local x = 0) so they never cover the tags.
      outpost.add(box(m.solid(color, 0.9), 0.05, 71 * IN, 1.2, -0.03, 0))
      outpost.add(box(m.flat(0x111111), 0.002, 32 * IN, 7 * IN, -0.004, 0, 1.88 * IN))
      outpost.add(box(m.flat(0x111111), 0.002, 31.8 * IN, 7 * IN, -0.004, 0, 28.1 * IN))
      group.add(outpost)

      // DEPOT: low barrier along the same wall. The manual does not say which end; this
      // places it at the opposite corner from the OUTPOST.
      const depotY = width - anchor.y
      const depot = new THREE.Group()
      depot.position.set(outpostTags[0].x, depotY, 0)
      depot.rotation.z = outpostTags[0].yaw
      const barrier = m.solid(ALUMINUM)
      depot.add(box(barrier, 27 * IN, 3 * IN, 1 * IN, 27 * IN / 2, -42 * IN / 2))
      depot.add(box(barrier, 27 * IN, 3 * IN, 1 * IN, 27 * IN / 2, 42 * IN / 2))
      depot.add(box(barrier, 3 * IN, 42 * IN, 1 * IN, 27 * IN, 0))
      group.add(depot)
    }
  }
  return built
}

function buildReefscape(group: THREE.Group, m: Materials, points: Map<number, TagPoint>, length: number, width: number): boolean {
  const reefs: [Alliance, number[]][] = [['blue', [17, 18, 19, 20, 21, 22]], ['red', [6, 7, 8, 9, 10, 11]]]
  let built = false
  group.add(tape(m.flat(TAPE_WHITE), length / 2, 0, length / 2, width))

  for (const [alliance, ids] of reefs) {
    const tags = find(points, ids)
    if (!tags) continue
    built = true
    const color = allianceColor(alliance)
    const reef = center(tags)
    const apothem = Math.hypot(tags[0].x - reef.x, tags[0].y - reef.y)
    // A cylinder's first vertex points along -Y after rotateX, which puts flats at 0°, 60°, ...
    const base = new THREE.Mesh(
      new THREE.CylinderGeometry(apothem / Math.cos(Math.PI / 6), apothem / Math.cos(Math.PI / 6), 0.3, 6).rotateX(Math.PI / 2),
      m.solid(color, 0.85),
    )
    base.position.set(reef.x, reef.y, 0.15)
    group.add(base)
    const column = new THREE.Mesh(new THREE.CylinderGeometry(0.28, 0.32, 1.4, 6).rotateX(Math.PI / 2), m.solid(DARK))
    column.position.set(reef.x, reef.y, 0.3 + 0.7)
    group.add(column)
    // Two branch posts per face, 13 in apart.
    for (const tag of tags) {
      for (const offset of [-6.5 * IN, 6.5 * IN]) {
        const post = box(m.solid(0x8a5fb8), 0.04, 0.04, 1.83, 0, 0)
        const along = tag.yaw + Math.PI / 2
        post.position.x = reef.x + Math.cos(tag.yaw) * (apothem - 0.12) + Math.cos(along) * offset
        post.position.y = reef.y + Math.sin(tag.yaw) * (apothem - 0.12) + Math.sin(along) * offset
        group.add(post)
      }
    }
    // Starting line on the near side of the BARGE zone.
    const home = alliance === 'blue' ? -1 : 1
    group.add(tape(m.flat(color), length / 2 + home * 1.6, 0, length / 2 + home * 1.6, width))
  }

  // BARGE across the center line, with its tags on both faces.
  const barge = find(points, [4, 5, 14, 15])
  if (barge) {
    const c = center(barge)
    const depth = Math.max(...barge.map(t => t.x)) - Math.min(...barge.map(t => t.x))
    const span = Math.max(...barge.map(t => t.y)) - Math.min(...barge.map(t => t.y)) + 1.2
    group.add(box(m.solid(ALUMINUM), depth - 0.1, span, 0.25, c.x, c.y, 1.75))
    for (const y of [c.y - span / 2 + 0.15, c.y + span / 2 - 0.15]) {
      group.add(box(m.solid(ALUMINUM), depth - 0.1, 0.12, 1.75, c.x, y))
    }
  }

  // CORAL STATIONS across the corners and PROCESSORS in the side walls, on their tags.
  for (const [id, alliance] of [[1, 'red'], [2, 'red'], [12, 'blue'], [13, 'blue'], [3, 'red'], [16, 'blue']] as const) {
    const tag = points.get(id)
    if (!tag) continue
    const station = id !== 3 && id !== 16
    const panel = new THREE.Group()
    panel.position.set(tag.x, tag.y, 0)
    panel.rotation.z = tag.yaw
    panel.add(box(m.solid(allianceColor(alliance), 0.85), 0.06, station ? 1.9 : 0.75, station ? 1.9 : 1.6, -0.04, 0))
    panel.add(box(m.flat(0x111111), 0.03, station ? 1.6 : 0.6, station ? 0.18 : 0.35, -0.005, 0, station ? 0.9 : 0.2))
    group.add(panel)
  }
  return built
}

/**
 * Carpet, perimeter, tape, and (for REBUILT and REEFSCAPE layouts) game elements.
 * Returns the group and which game was recognized, if any.
 */
export function buildFieldModel(
  layout: SceneLayout,
  colors: { carpet: THREE.Color; structure: number },
): { group: THREE.Group; game: string | null } {
  DARK = colors.structure
  const group = new THREE.Group()
  const materials = new Materials()
  const length = layout.field?.length ?? 16.541
  const width = layout.field?.width ?? 8.069

  const carpet = new THREE.Mesh(new THREE.PlaneGeometry(length, width), new THREE.MeshLambertMaterial({ color: colors.carpet }))
  carpet.name = 'carpet'
  carpet.position.set(length / 2, width / 2, -0.001)
  group.add(carpet)
  buildPerimeter(group, materials, length, width)

  const points = tagPoints(layout)
  const elements = new THREE.Group()
  // REBUILT uses IDs 1-32; REEFSCAPE stops at 22, so the upper IDs tell them apart.
  let game: string | null = null
  const hasUpperIds = layout.tags.some(tag => tag.id >= 23)
  if (hasUpperIds && buildRebuilt(elements, materials, points, length, width)) {
    game = 'REBUILT'
  } else if (!hasUpperIds && buildReefscape(elements, materials, points, length, width)) {
    game = 'REEFSCAPE'
  } else {
    elements.clear()
  }
  group.add(elements)
  return { group, game }
}
