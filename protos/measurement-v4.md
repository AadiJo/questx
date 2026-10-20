# QuestX AprilTag measurement v4

The Quest publishes `/QuestX/v4/apriltag`, an atomic NetworkTables `double[]` that adds
the published pose's full 6×6 covariance to the v3 AprilTag measurement. The app keeps
publishing `/QuestX/v2/apriltag`, `/QuestX/v3/apriltag` (both unchanged in layout), and
the `aprilTagData` protobuf, so existing robot code keeps working. VIO stays at
`/QuestX/v2/vio`.

The Java library's default constructor selects `MeasurementTransport.V4`, which reads
`/QuestX/v2/vio` and `/QuestX/v4/apriltag`. Select `V3` for a Quest app that predates v4;
`AprilTagFrame.hasCovariance()` is then false and `covariance()` is filled with NaN.

## Array layout

Indices 0–25 are identical to v3. v4 contains exactly **53 + 2 × tagCount** doubles:

| Index | Field |
| --- | --- |
| 0–25 | The v3 fields: sequence ID, capture timestamp, app uptime, robot pose, tag and corner counts, inlier ratio, average tag distance, six standard deviations, ambiguity, reprojection error, processing latency |
| 26–46 | Covariance of the pose's error, upper triangle row by row: (0,0), (0,1), …, (0,5), (1,1), …, (5,5) |
| 47–52 | Planar covariance upper triangle: xx, xy, x-heading, yy, y-heading, heading-heading; all zero if unavailable |
| 53 to 52 + N | Tag IDs, detector order |
| 53 + N to 52 + 2N | Per-tag ambiguity, 0–1, same order as the IDs |

N is the tag count at index 12. Every value must be finite and the covariance must be
positive definite (it has a Cholesky factor; singular matrices are rejected). The planar block must also be positive definite, or contain exactly six zeros to indicate an unavailable projection. The
decoder rejects arrays that break these rules or the v3 checks.

## Covariance convention

Rows and columns are ordered `[x, y, z, rx, ry, rz]`, as in ROS's `PoseWithCovariance`
and Napa:

- `x, y, z`: the error in the pose's translation, in meters, along the **field** axes;
- `rx, ry, rz`: a small rotation, in radians, about the **field** axes, applied on the
  left of the published rotation, so the true rotation is `exp([r]×) R`.

For a 6-D filter update, use the covariance as the measurement noise, with the
innovation `[t_measured − t_predicted, log(R_measured R_predictedᵀ)]` so the residual
follows the same convention.

The covariance belongs to the published robot pose. The Quest computes it for the
camera, then moves it through the Meta lens offset and the robot-to-Quest transform,
treating both as exact; measure the mount well.

## Standard deviations (indices 17–22)

From this release, indices 17–22 of every AprilTag array (v2, v3, and v4) and
`pose_std_devs` in the `aprilTagData` protobuf hold the covariance's standard deviations
along the **measured robot's axes**. WPILib 2026 applies its gain along the historical predicted heading, which can differ.

- **Forward, left, yaw** (17, 18, 22) belong to the pose projected onto the field
  floor, as `Pose3d.toPose2d()` gives it, along its heading. Each weighted solution is
  projected exactly before the spread is taken, as Napa's `to_pose2d()` does. On a
  tilted robot, heading uncertainty differs from uncertainty about the body's own up
  axis.
- **Up, roll, pitch** (19, 20, 21) are along the pose's own axes.

`AprilTagFrame.pose2dStdDevs()` exposes these marginal values for compatibility; it approximates the estimator frame only near zero innovation and omits correlations. Prefer `pose2dStdDevs(predictedPoseAtCapture)` for WPILib 2026.2.1. It uses the planar covariance block, rotates it into the prediction's heading and conservatively bounds the discarded correlations. Earlier releases published a hand-tuned distance model here, with equal X and Y values.

## Planar covariance (indices 47–52)

This is the full 3×3 covariance of field X, field Y, and heading, in meters and radians, produced by `TryToPose2d()` after the robot mount. Each weighted solution is projected before taking its spread, including wrapped heading offsets and translation-heading correlation. Keep it separate from the 6×6 summary: projection of a reduced mixture is generally not the same as reduction after projection.

Six zeros mean any weighted solution's forward axis is vertical and heading is undefined. `hasPlanarCovariance()` is then false; skip planar fusion rather than substituting body-axis yaw uncertainty. The 6-D covariance remains usable. Java returns NaN for an unavailable planar block, as it does for an unreported 6-D covariance.

## How the covariance is computed

The model is ported from [Napa](https://github.com/zwatx/NAPA) (`covariance.py` and
`solvers.py` at `c5f25e2`) into `AprilTagCovariance` and `PoseCovariance` in
`unity/Assets/QuestX/AprilTag/`.

Each corner coordinate is modelled as independent Gaussian noise of 1 px, Napa's
deliberately conservative default: motion blur, rolling shutter, and calibration error
all look like corner noise to the solver. The covariance starts from the Gauss–Newton
approximation `σ² (JᵀJ)⁻¹`, where `J` is the Jacobian of the projected corners with
respect to the pose, evaluated at the solved pose: the linearized Cramér–Rao bound. A
distant tag, a steep view, or a lone tag gets a large covariance; many well-spread tags
get a small one. Two adjustments make it safe for a filter:

- **Ambiguity.** A planar target has two poses that fit its corners. A lone tag is
  described by the solved pose and its other IPPE solution, weighted by their tempered
  corner likelihood `exp(−SSR / 2Tσ²)` with T = 2. Several tags on (nearly) one plane
  step along the rotation from the solved pose to the mirror solution of the plane fitted to all their corners (−¼ to 1¼
  of the way), re-solve the other five degrees of freedom at each step, and weight the
  steps by likelihood. The reported covariance is the weighted spread about the
  published pose, so a genuinely ambiguous measurement gets a covariance wide enough
  that a filter downweights it. This is not a guarantee of rejecting the wrong mode; retain ambiguity and innovation gates. Tags at clearly different depths break the ambiguity
  and keep the linearized covariance.
- **Curved error surfaces.** 10% of each variance is added back to the diagonal, which
  caps every correlation at 0.91 and widens each standard deviation by 5%.

Transforms move every weighted solution exactly and recompute the spread in the new
frame, because a spread covering tens of degrees does not survive first-order
propagation. Only tags whose four corners lie within 12 px (PoseLib's RANSAC threshold) of their reprojections contribute. The same selection supplies covariance, ambiguity, fit error, IDs, and observations. Native inlier and candidate corner counts remain diagnostics of the original solve. If no complete tag survives, the measurement is rejected; rejected corners are never restored to calculate confidence. If the corners don't
constrain all six degrees of freedom, the Quest rejects the measurement locally.

In the Monte Carlo checks (`tests/measurement-checks`), a far, nearly face-on single
tag left 48% of errors outside the 95% ellipse of the linearized covariance alone and
none outside the reported one; a far coplanar pair, 16% and 1.3%. Unambiguous
geometries stay within about 1%.

## Tuning

The covariance assumes 1 px of corner noise. If the scatter of a stationary robot's
logged poses differs from the reported standard deviations, scale the covariance by k²
on the robot (k = measured / reported). That is exact for unambiguous measurements;
for widened ones it is approximate, because the ambiguity weights depend on the noise.
Stationary scatter does not establish absolute accuracy: compare against independent ground truth and check moving capture timing as well. Calibration error repeats from frame to frame, which no per-frame covariance can
capture, so don't push the scale much below what you measure.

## Validation

`dotnet run --project tests/measurement-checks/MeasurementChecks.csproj` checks the
covariance math and calibration and the v4 encoder. Add `--export-fixtures` to
regenerate `questx-lib/src/test/resources/measurement-v4/` (and the v3 fixtures):

- `apriltag.txt`: synthetic values in every field, with a covariance whose field-axis
  standard deviations are 0.1, 0.2, 0.3 m and 0.01, 0.02, 0.03 rad.
- `apriltag-single-tag.txt` and `apriltag-two-tags.txt`: the same realistic REBUILT
  frames as v3, with the pose and covariance from the production solver.

Then run `questx-lib/gradlew test` with JDK 17.
