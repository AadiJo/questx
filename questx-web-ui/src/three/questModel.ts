// Meta Quest 3S for the 3D views, without its strap. The asset is already in headset axes
// (X forward, Y left, Z up) and meters, with the origin at the center eye, the point QuestX
// reports; see assets/README.md for its source and license. The build inlines it into
// main.js (vite.config.ts) because the APK only extracts a fixed list of web UI files.
// Drawn larger than life so it reads next to the robot; the scale is about the center eye, so
// the camera offset and frustum (siblings, not children, of the model) stay true to size.

import * as THREE from 'three'
import { GLTFLoader } from 'three/examples/jsm/loaders/GLTFLoader.js'
import { MeshoptDecoder } from 'three/examples/jsm/libs/meshopt_decoder.module.js'
import questGlb from './assets/quest3s.glb'

const DISPLAY_SCALE = 1.5

const MATERIALS: Record<string, THREE.Material> = {
  shell: new THREE.MeshStandardMaterial({ color: 0xf2f2ef, roughness: 0.45, metalness: 0 }),
  trim: new THREE.MeshStandardMaterial({ color: 0x18191b, roughness: 0.6, metalness: 0.05 }),
  lens: new THREE.MeshStandardMaterial({ color: 0x2a3038, roughness: 0.1, metalness: 0.3 }),
}

let loaded: Promise<THREE.Object3D> | null = null

function loadQuest(): Promise<THREE.Object3D> {
  // A data: URL in the build, a dev-server URL under `vite`; the loader fetches either.
  loaded ??= new GLTFLoader()
    .setMeshoptDecoder(MeshoptDecoder)
    .loadAsync(questGlb)
    .then(gltf => {
      gltf.scene.traverse(object => {
        const mesh = object as THREE.Mesh
        if (mesh.isMesh) mesh.material = MATERIALS[mesh.name] ?? MATERIALS.shell
      })
      return gltf.scene
    })
  return loaded
}

/** The headset group is returned right away; the model fills in once decoded. */
export function makeQuestModel(): THREE.Group {
  const root = new THREE.Group()
  const model = new THREE.Group()
  model.scale.setScalar(DISPLAY_SCALE)
  root.add(model)
  loadQuest().then(
    scene => model.add(scene.clone()),
    error => console.error('Failed to load the Quest 3S model', error),
  )
  return root
}
