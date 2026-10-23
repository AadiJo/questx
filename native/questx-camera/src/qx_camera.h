/* QuestX passthrough camera: NDK Camera2 capture straight into an 8-bit luma buffer. */
#ifndef QX_CAMERA_H
#define QX_CAMERA_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define QX_API __attribute__((visibility("default")))

/* Static calibration of one camera, as Camera2 reports it at the active array size. */
typedef struct {
    float intrinsics[5];       /* fx, fy, cx, cy, skew (LENS_INTRINSIC_CALIBRATION) */
    float pose_translation[3]; /* LENS_POSE_TRANSLATION, metres */
    float pose_rotation[4];    /* LENS_POSE_ROTATION, x y z w */
    int32_t active_width;      /* SENSOR_INFO_ACTIVE_ARRAY_SIZE */
    int32_t active_height;
    int32_t position;          /* com.meta.extra_metadata.position (0 left, 1 right), -1 unknown */
    int32_t timestamp_source;  /* SENSOR_INFO_TIMESTAMP_SOURCE */
} qx_camera_info;

/* One delivered frame. data stays valid until qx_camera_release or the next acquire. */
typedef struct {
    int64_t frame_number;        /* frames delivered since start, from 1 */
    int64_t sensor_timestamp_ns; /* AImage_getTimestamp: start of exposure */
    int64_t exposure_unix_ns;    /* the same instant on CLOCK_REALTIME */
    int64_t arrival_unix_ns;     /* when the image reached this library */
    int64_t exposure_time_ns;    /* SENSOR_EXPOSURE_TIME, or -1 if its result had not arrived */
    const uint8_t *data;         /* luma, top-left origin */
    int32_t width;
    int32_t height;
    int32_t stride;
    int32_t sensor_clock;        /* clockid_t the sensor timestamps were matched to */
} qx_frame;

typedef struct {
    int64_t delivered;   /* frames copied out of the image reader */
    int64_t consumed;    /* frames returned by qx_camera_acquire */
    int64_t overwritten; /* frames replaced before anyone acquired them */
    int64_t errors;      /* device or session errors */
    int32_t running;
    int32_t error;       /* last device error, 0 if none; see qx_camera_acquire */
} qx_camera_stats;

/* Finds the camera at a Meta position (0 left, 1 right). Writes its Camera2 id. Returns 0, or
 * a negative value if no such camera exists or the camera service is unavailable. */
QX_API int qx_camera_find(int position, char *id_out, int id_capacity, qx_camera_info *info);

/* Opens the camera and starts a repeating YUV_420_888 request at a fixed AE frame rate.
 * Returns 0 or a negative error. Only one camera may run at a time. */
QX_API int qx_camera_start(const char *camera_id, int width, int height, int fps);

/* Waits up to timeout_ms for a frame newer than the last one acquired. Returns 1 with a frame,
 * 0 on timeout, or a negative value if the camera stopped or failed (-2 disconnected, e.g.
 * another client took the camera; -3 device error; -1 not running). Releases the previous frame. */
QX_API int qx_camera_acquire(qx_frame *out, int timeout_ms);

/* Releases the frame from the last acquire so the producer may reuse its buffer. */
QX_API void qx_camera_release(void);

/* Stops capture and frees every resource. Safe to call when stopped. */
QX_API void qx_camera_stop(void);

QX_API void qx_camera_get_stats(qx_camera_stats *out);

#ifdef __cplusplus
}
#endif

#endif
