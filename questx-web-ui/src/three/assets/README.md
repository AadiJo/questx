# 3D assets

## quest3s.glb

This work is based on "Quest 3S with controllers"
(https://sketchfab.com/3d-models/quest-3s-with-controllers-448519e4ef0b40619592000db3729aaa)
by OG_MATTER (https://sketchfab.com/fe4rmatter), licensed under CC-BY-4.0
(http://creativecommons.org/licenses/by/4.0/).

Changes from the original: the controllers, head strap, and strap arms are removed; the
remaining parts are merged into three meshes (`shell`, `trim`, `lens`) with materials assigned
in code. The model is re-oriented and scaled to meters in QuestX headset axes (X forward,
Y left, Z up), with the origin at the center eye, then compressed with `EXT_meshopt_compression`.
