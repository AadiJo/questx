/* QuestX passthrough camera: NDK Camera2 capture straight into an 8-bit luma buffer.
 *
 * The image reader's listener thread copies each frame's Y plane into one of three buffers and
 * signals the consumer, so detection starts the moment a frame arrives instead of on the next
 * Unity frame, and nothing goes through the GPU. The consumer always gets the newest frame;
 * older unread frames are overwritten (counted in qx_camera_stats.overwritten). */
#include "qx_camera.h"

#include <android/log.h>
#include <android/native_window.h>
#include <camera/NdkCameraCaptureSession.h>
#include <camera/NdkCameraDevice.h>
#include <camera/NdkCameraManager.h>
#include <camera/NdkCameraMetadata.h>
#include <camera/NdkCaptureRequest.h>
#include <errno.h>
#include <media/NdkImage.h>
#include <media/NdkImageReader.h>
#include <pthread.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#define TAG "QuestXCamera"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, TAG, __VA_ARGS__)

#define SLOTS 3
#define MAX_IMAGES 4
#define EXPOSURE_RING 16
#define META_POSITION_TAG 0x80004d01u

typedef struct {
    int64_t sensor_timestamp_ns;
    int64_t exposure_time_ns;
} exposure_entry;

static pthread_mutex_t lock = PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t frame_ready = PTHREAD_COND_INITIALIZER;
static pthread_cond_t listener_done = PTHREAD_COND_INITIALIZER;
static int listeners; /* on_image calls between their running check and their publish */

static ACameraManager *manager;
static ACameraDevice *device;
static AImageReader *reader;
static ANativeWindow *window;
static ACaptureSessionOutputContainer *outputs;
static ACaptureSessionOutput *output;
static ACameraOutputTarget *target;
static ACaptureRequest *request;
static ACameraCaptureSession *session;

static int running;
static int device_error;
static uint8_t *buffers[SLOTS];
static size_t buffer_size;
static qx_frame frames[SLOTS];
static int latest = -1; /* slot holding the newest frame, or -1 */
static int locked = -1; /* slot the consumer is reading, or -1 */
static int64_t last_acquired;
static qx_camera_stats stats;
static exposure_entry exposures[EXPOSURE_RING];
static int exposure_next;
static int sensor_clock = -1; /* chosen on the first frame of each session */

static int64_t now_ns(clockid_t clock) {
    struct timespec ts;
    clock_gettime(clock, &ts);
    return (int64_t)ts.tv_sec * 1000000000LL + ts.tv_nsec;
}

static void fail(int error) {
    pthread_mutex_lock(&lock);
    device_error = error;
    stats.errors++;
    pthread_cond_broadcast(&frame_ready);
    pthread_mutex_unlock(&lock);
}

static void on_disconnected(void *context, ACameraDevice *camera) {
    (void)context;
    (void)camera;
    LOGE("camera disconnected");
    fail(-2);
}

static void on_device_error(void *context, ACameraDevice *camera, int error) {
    (void)context;
    (void)camera;
    LOGE("camera device error %d", error);
    fail(-3);
}

static void on_session_closed(void *context, ACameraCaptureSession *s) {
    (void)context;
    (void)s;
}

static void on_session_ready(void *context, ACameraCaptureSession *s) {
    (void)context;
    (void)s;
}

static void on_session_active(void *context, ACameraCaptureSession *s) {
    (void)context;
    (void)s;
}

static void on_capture_completed(void *context, ACameraCaptureSession *s, ACaptureRequest *r,
                                 const ACameraMetadata *result) {
    (void)context;
    (void)s;
    (void)r;
    ACameraMetadata_const_entry ts = {0}, exposure = {0};
    if (ACameraMetadata_getConstEntry(result, ACAMERA_SENSOR_TIMESTAMP, &ts) != ACAMERA_OK ||
        ts.count < 1)
        return;
    int64_t exposure_ns = -1;
    if (ACameraMetadata_getConstEntry(result, ACAMERA_SENSOR_EXPOSURE_TIME, &exposure) ==
            ACAMERA_OK &&
        exposure.count > 0)
        exposure_ns = exposure.data.i64[0];
    pthread_mutex_lock(&lock);
    exposures[exposure_next].sensor_timestamp_ns = ts.data.i64[0];
    exposures[exposure_next].exposure_time_ns = exposure_ns;
    exposure_next = (exposure_next + 1) % EXPOSURE_RING;
    pthread_mutex_unlock(&lock);
}

static void on_capture_failed(void *context, ACameraCaptureSession *s, ACaptureRequest *r,
                              ACameraCaptureFailure *failure) {
    (void)context;
    (void)s;
    (void)r;
    (void)failure;
}

/* Sensor timestamps are on CLOCK_BOOTTIME or CLOCK_MONOTONIC (timestampSource is UNKNOWN on
 * Quest). They differ only after the headset has slept, so the first frame of a session picks
 * whichever clock puts its exposure in the recent past, and the session keeps that clock.
 * Returns the frame's age on it. Called with the lock held. */
static int64_t frame_age(int64_t sensor_ns, int64_t monotonic, int64_t boottime, int *clock) {
    int64_t mono_age = monotonic - sensor_ns;
    int64_t boot_age = boottime - sensor_ns;
    if (sensor_clock < 0) {
        const int64_t floor = -5000000LL, ceiling = 2000000000LL;
        int mono_ok = mono_age >= floor && mono_age <= ceiling;
        int boot_ok = boot_age >= floor && boot_age <= ceiling;
        sensor_clock = boot_ok && (!mono_ok || llabs(boot_age) < llabs(mono_age))
                           ? CLOCK_BOOTTIME
                           : CLOCK_MONOTONIC;
    }
    *clock = sensor_clock;
    return sensor_clock == CLOCK_BOOTTIME ? boot_age : mono_age;
}

static void leave_listener(void) {
    listeners--;
    pthread_cond_broadcast(&listener_done);
}

static void on_image(void *context, AImageReader *r) {
    (void)context;
    AImage *image = NULL;
    if (AImageReader_acquireLatestImage(r, &image) != AMEDIA_OK || image == NULL)
        return;
    /* Stopping waits for listeners that got past this point before it frees the buffers. */
    pthread_mutex_lock(&lock);
    if (!running) {
        pthread_mutex_unlock(&lock);
        AImage_delete(image);
        return;
    }
    listeners++;
    pthread_mutex_unlock(&lock);
    int64_t real_now = now_ns(CLOCK_REALTIME);
    int64_t mono_now = now_ns(CLOCK_MONOTONIC);
    int64_t boot_now = now_ns(CLOCK_BOOTTIME);

    int64_t sensor_ns = 0;
    int32_t width = 0, height = 0, row_stride = 0, pixel_stride = 1;
    uint8_t *luma = NULL;
    int luma_length = 0;
    if (AImage_getTimestamp(image, &sensor_ns) != AMEDIA_OK ||
        AImage_getWidth(image, &width) != AMEDIA_OK ||
        AImage_getHeight(image, &height) != AMEDIA_OK ||
        AImage_getPlaneData(image, 0, &luma, &luma_length) != AMEDIA_OK ||
        AImage_getPlaneRowStride(image, 0, &row_stride) != AMEDIA_OK ||
        AImage_getPlanePixelStride(image, 0, &pixel_stride) != AMEDIA_OK || luma == NULL ||
        pixel_stride != 1 || width <= 0 || height <= 0 ||
        (size_t)width * (size_t)height > buffer_size ||
        (int64_t)row_stride * (height - 1) + width > luma_length) {
        AImage_delete(image);
        pthread_mutex_lock(&lock);
        leave_listener();
        pthread_mutex_unlock(&lock);
        return;
    }

    /* Pick a buffer the consumer is not reading and that does not hold the newest frame. Only
     * this thread writes buffers, so the slot stays ours until it is published below. */
    pthread_mutex_lock(&lock);
    int slot = 0;
    while (slot == locked || slot == latest)
        slot++;
    pthread_mutex_unlock(&lock);

    uint8_t *dst = buffers[slot];
    if (row_stride == width) {
        memcpy(dst, luma, (size_t)width * height);
    } else {
        for (int y = 0; y < height; y++)
            memcpy(dst + (size_t)y * width, luma + (size_t)y * row_stride, (size_t)width);
    }
    AImage_delete(image);

    pthread_mutex_lock(&lock);
    int clock = CLOCK_MONOTONIC;
    int64_t age = frame_age(sensor_ns, mono_now, boot_now, &clock);
    int64_t exposure_ns = -1;
    for (int i = 0; i < EXPOSURE_RING; i++) {
        if (exposures[i].sensor_timestamp_ns == sensor_ns) {
            exposure_ns = exposures[i].exposure_time_ns;
            break;
        }
    }
    if (latest >= 0 && frames[latest].frame_number > last_acquired)
        stats.overwritten++;
    stats.delivered++;
    qx_frame *f = &frames[slot];
    f->frame_number = stats.delivered;
    f->sensor_timestamp_ns = sensor_ns;
    f->exposure_unix_ns = real_now - age;
    f->arrival_unix_ns = real_now;
    f->exposure_time_ns = exposure_ns;
    f->data = dst;
    f->width = width;
    f->height = height;
    f->stride = width;
    f->sensor_clock = clock;
    latest = slot;
    pthread_cond_broadcast(&frame_ready);
    leave_listener();
    pthread_mutex_unlock(&lock);
}

static ACameraDevice_StateCallbacks device_callbacks = {
    .context = NULL,
    .onDisconnected = on_disconnected,
    .onError = on_device_error,
};

static ACameraCaptureSession_stateCallbacks session_callbacks = {
    .context = NULL,
    .onClosed = on_session_closed,
    .onReady = on_session_ready,
    .onActive = on_session_active,
};

static ACameraCaptureSession_captureCallbacks capture_callbacks = {
    .context = NULL,
    .onCaptureStarted = NULL,
    .onCaptureProgressed = NULL,
    .onCaptureCompleted = on_capture_completed,
    .onCaptureFailed = on_capture_failed,
    .onCaptureSequenceCompleted = NULL,
    .onCaptureSequenceAborted = NULL,
    .onCaptureBufferLost = NULL,
};

static AImageReader_ImageListener image_listener = {
    .context = NULL,
    .onImageAvailable = on_image,
};

int qx_camera_find(int position, char *id_out, int id_capacity, qx_camera_info *info) {
    if (id_out == NULL || id_capacity <= 0 || info == NULL)
        return -1;
    ACameraManager *m = ACameraManager_create();
    if (m == NULL)
        return -2;
    ACameraIdList *ids = NULL;
    if (ACameraManager_getCameraIdList(m, &ids) != ACAMERA_OK || ids == NULL) {
        ACameraManager_delete(m);
        return -3;
    }
    int found = -4;
    for (int i = 0; i < ids->numCameras && found != 0; i++) {
        ACameraMetadata *chars = NULL;
        if (ACameraManager_getCameraCharacteristics(m, ids->cameraIds[i], &chars) != ACAMERA_OK)
            continue;
        ACameraMetadata_const_entry e = {0};
        int pos = -1;
        if (ACameraMetadata_getConstEntry(chars, META_POSITION_TAG, &e) == ACAMERA_OK &&
            e.count > 0)
            pos = e.data.u8[0];
        ACameraMetadata_const_entry intr = {0}, trans = {0}, rot = {0};
        int calibrated =
            ACameraMetadata_getConstEntry(chars, ACAMERA_LENS_INTRINSIC_CALIBRATION, &intr) ==
                ACAMERA_OK &&
            intr.count >= 5 &&
            ACameraMetadata_getConstEntry(chars, ACAMERA_LENS_POSE_TRANSLATION, &trans) ==
                ACAMERA_OK &&
            trans.count >= 3 &&
            ACameraMetadata_getConstEntry(chars, ACAMERA_LENS_POSE_ROTATION, &rot) == ACAMERA_OK &&
            rot.count >= 4;
        if (pos == position && calibrated) {
            memset(info, 0, sizeof(*info));
            memcpy(info->intrinsics, intr.data.f, 5 * sizeof(float));
            memcpy(info->pose_translation, trans.data.f, 3 * sizeof(float));
            memcpy(info->pose_rotation, rot.data.f, 4 * sizeof(float));
            info->position = pos;
            if (ACameraMetadata_getConstEntry(chars, ACAMERA_SENSOR_INFO_ACTIVE_ARRAY_SIZE, &e) ==
                    ACAMERA_OK &&
                e.count >= 4) {
                info->active_width = e.data.i32[2] - e.data.i32[0];
                info->active_height = e.data.i32[3] - e.data.i32[1];
            }
            info->timestamp_source = -1;
            if (ACameraMetadata_getConstEntry(chars, ACAMERA_SENSOR_INFO_TIMESTAMP_SOURCE, &e) ==
                    ACAMERA_OK &&
                e.count > 0)
                info->timestamp_source = e.data.u8[0];
            strncpy(id_out, ids->cameraIds[i], (size_t)id_capacity - 1);
            id_out[id_capacity - 1] = '\0';
            found = 0;
        }
        ACameraMetadata_free(chars);
    }
    ACameraManager_deleteCameraIdList(ids);
    ACameraManager_delete(m);
    return found;
}

static void release_all(void) {
    if (session != NULL) {
        ACameraCaptureSession_stopRepeating(session);
        ACameraCaptureSession_close(session);
        session = NULL;
    }
    if (device != NULL) {
        ACameraDevice_close(device);
        device = NULL;
    }
    if (request != NULL) {
        ACaptureRequest_free(request);
        request = NULL;
    }
    if (target != NULL) {
        ACameraOutputTarget_free(target);
        target = NULL;
    }
    if (outputs != NULL && output != NULL)
        ACaptureSessionOutputContainer_remove(outputs, output);
    if (output != NULL) {
        ACaptureSessionOutput_free(output);
        output = NULL;
    }
    if (outputs != NULL) {
        ACaptureSessionOutputContainer_free(outputs);
        outputs = NULL;
    }
    if (window != NULL) {
        ANativeWindow_release(window);
        window = NULL;
    }
    /* Deleting the reader waits out an in-flight listener call, so the buffers outlive it. */
    if (reader != NULL) {
        AImageReader_delete(reader);
        reader = NULL;
    }
    if (manager != NULL) {
        ACameraManager_delete(manager);
        manager = NULL;
    }
    pthread_mutex_lock(&lock);
    running = 0;
    while (listeners > 0)
        pthread_cond_wait(&listener_done, &lock);
    for (int i = 0; i < SLOTS; i++) {
        free(buffers[i]);
        buffers[i] = NULL;
    }
    buffer_size = 0;
    latest = -1;
    locked = -1;
    pthread_mutex_unlock(&lock);
}

int qx_camera_start(const char *camera_id, int width, int height, int fps) {
    if (camera_id == NULL || width <= 0 || height <= 0 || fps <= 0)
        return -1;
    qx_camera_stop();

    pthread_mutex_lock(&lock);
    memset(&stats, 0, sizeof(stats));
    memset(exposures, 0, sizeof(exposures));
    memset(frames, 0, sizeof(frames));
    exposure_next = 0;
    sensor_clock = -1;
    device_error = 0;
    last_acquired = 0;
    latest = -1;
    locked = -1;
    buffer_size = (size_t)width * (size_t)height;
    int allocated = 1;
    for (int i = 0; i < SLOTS; i++) {
        buffers[i] = (uint8_t *)malloc(buffer_size);
        allocated = allocated && buffers[i] != NULL;
    }
    pthread_mutex_unlock(&lock);
    if (!allocated) {
        release_all();
        return -2;
    }

    int status = -3;
    camera_status_t cs;
    media_status_t ms;
    manager = ACameraManager_create();
    if (manager == NULL)
        goto fail;
    if ((cs = ACameraManager_openCamera(manager, camera_id, &device_callbacks, &device)) !=
        ACAMERA_OK) {
        LOGE("openCamera(%s) failed: %d", camera_id, cs);
        status = -4;
        goto fail;
    }
    if ((ms = AImageReader_new(width, height, AIMAGE_FORMAT_YUV_420_888, MAX_IMAGES, &reader)) !=
        AMEDIA_OK) {
        LOGE("AImageReader_new failed: %d", ms);
        status = -5;
        goto fail;
    }
    if (AImageReader_setImageListener(reader, &image_listener) != AMEDIA_OK ||
        AImageReader_getWindow(reader, &window) != AMEDIA_OK || window == NULL) {
        status = -5;
        window = NULL;
        goto fail;
    }
    ANativeWindow_acquire(window);
    if (ACaptureSessionOutputContainer_create(&outputs) != ACAMERA_OK ||
        ACaptureSessionOutput_create(window, &output) != ACAMERA_OK ||
        ACaptureSessionOutputContainer_add(outputs, output) != ACAMERA_OK ||
        ACameraOutputTarget_create(window, &target) != ACAMERA_OK ||
        ACameraDevice_createCaptureRequest(device, TEMPLATE_PREVIEW, &request) != ACAMERA_OK ||
        ACaptureRequest_addTarget(request, target) != ACAMERA_OK) {
        status = -6;
        goto fail;
    }
    /* A fixed range: the default [1, 60] lets auto exposure trade frame rate for brightness. */
    int32_t range[2] = {fps, fps};
    if ((cs = ACaptureRequest_setEntry_i32(request, ACAMERA_CONTROL_AE_TARGET_FPS_RANGE, 2,
                                           range)) != ACAMERA_OK)
        LOGE("AE target fps range [%d, %d] rejected: %d", fps, fps, cs);
    if ((cs = ACameraDevice_createCaptureSession(device, outputs, &session_callbacks, &session)) !=
        ACAMERA_OK) {
        LOGE("createCaptureSession failed: %d", cs);
        status = -7;
        goto fail;
    }
    pthread_mutex_lock(&lock);
    running = 1;
    pthread_mutex_unlock(&lock);
    if ((cs = ACameraCaptureSession_setRepeatingRequest(session, &capture_callbacks, 1, &request,
                                                        NULL)) != ACAMERA_OK) {
        LOGE("setRepeatingRequest failed: %d", cs);
        status = -8;
        goto fail;
    }
    LOGI("camera %s streaming %dx%d at %d fps", camera_id, width, height, fps);
    return 0;

fail:
    release_all();
    return status;
}

int qx_camera_acquire(qx_frame *out, int timeout_ms) {
    if (out == NULL)
        return -1;
    struct timespec deadline;
    clock_gettime(CLOCK_REALTIME, &deadline);
    deadline.tv_sec += timeout_ms / 1000;
    deadline.tv_nsec += (long)(timeout_ms % 1000) * 1000000L;
    if (deadline.tv_nsec >= 1000000000L) {
        deadline.tv_sec++;
        deadline.tv_nsec -= 1000000000L;
    }
    pthread_mutex_lock(&lock);
    locked = -1;
    int result = 0;
    for (;;) {
        if (device_error != 0) {
            result = device_error;
            break;
        }
        if (!running) {
            result = -1;
            break;
        }
        if (latest >= 0 && frames[latest].frame_number > last_acquired) {
            locked = latest;
            last_acquired = frames[latest].frame_number;
            stats.consumed++;
            *out = frames[latest];
            result = 1;
            break;
        }
        if (pthread_cond_timedwait(&frame_ready, &lock, &deadline) == ETIMEDOUT) {
            result = 0;
            break;
        }
    }
    pthread_mutex_unlock(&lock);
    return result;
}

void qx_camera_release(void) {
    pthread_mutex_lock(&lock);
    locked = -1;
    pthread_mutex_unlock(&lock);
}

void qx_camera_stop(void) {
    pthread_mutex_lock(&lock);
    int was_running = running;
    running = 0;
    pthread_cond_broadcast(&frame_ready);
    pthread_mutex_unlock(&lock);
    release_all();
    if (was_running)
        LOGI("camera stopped");
}

void qx_camera_get_stats(qx_camera_stats *out) {
    if (out == NULL)
        return;
    pthread_mutex_lock(&lock);
    *out = stats;
    out->running = running;
    out->error = device_error;
    pthread_mutex_unlock(&lock);
}
