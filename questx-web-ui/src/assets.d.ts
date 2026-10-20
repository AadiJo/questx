// Models resolve to a URL (a data: URL in the build; see assetsInlineLimit in vite.config.ts).
declare module '*.glb' {
  const src: string
  export default src
}
