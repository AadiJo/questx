import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import ts from 'typescript'

const source = await readFile(new URL('../src/three/aprilTag36h11.ts', import.meta.url), 'utf8')
const code = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.ESNext } }).outputText
const { tag36h11Cells, TAG_CELLS } = await import(`data:text/javascript;base64,${Buffer.from(code).toString('base64')}`)

const render = id => tag36h11Cells(id).map(row => row.map(white => (white ? '#' : '.')).join(''))

// Transcribed from tag36_11_00000.png, tag36_11_00007.png, and tag36_11_00586.png in
// https://github.com/AprilRobotics/apriltag-imgs ('#' is white).
const EXPECTED = {
  0: ['##########', '#........#', '#.##.#.#.#', '#..###.#.#', '#..##....#', '#.#.#....#', '#..#.##..#', '#....#...#', '#........#', '##########'],
  7: ['##########', '#........#', '#....#...#', '#....##..#', '#..#.#...#', '#.#.###..#', '#....###.#', '#..#.#...#', '#........#', '##########'],
  586: ['##########', '#........#', '#.###.#..#', '#.....##.#', '#.#.####.#', '#.#..#...#', '#.#.##.#.#', '#.##..##.#', '#........#', '##########'],
}

test('tags match the published 36h11 images', () => {
  for (const [id, rows] of Object.entries(EXPECTED)) assert.deepEqual(render(Number(id)), rows, `tag ${id}`)
})

test('every tag has the white and black rings', () => {
  for (let id = 0; id < 587; id++) {
    const cells = tag36h11Cells(id)
    assert.equal(cells.length, TAG_CELLS)
    for (let i = 0; i < TAG_CELLS; i++) {
      assert.ok(cells[0][i] && cells[9][i] && cells[i][0] && cells[i][9], `tag ${id} white ring`)
      if (i >= 1 && i <= 8) assert.ok(!cells[1][i] && !cells[8][i] && !cells[i][1] && !cells[i][8], `tag ${id} black ring`)
    }
  }
})

test('IDs outside the family have no pattern', () => {
  assert.equal(tag36h11Cells(587), null)
  assert.equal(tag36h11Cells(-1), null)
  assert.equal(tag36h11Cells(1.5), null)
})
