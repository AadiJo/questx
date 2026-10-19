/* On-headset check: Napa vs libapriltag on the Quest CPU with the shipped plugin binaries.
 *
 *   NDK=.../android-ndk-r27c/toolchains/llvm/prebuilt/linux-x86_64/bin
 *   $NDK/aarch64-linux-android32-clang -O2 -o napa_android_check tests/native-localization/napa_android_check.c -ldl -lm
 *   adb push unity/Assets/Plugins/napa/Android/arm64-v8a/libnapa.so /data/local/tmp/quest-test-libnapa.so
 *   adb push unity/Assets/Plugins/apriltag/Android/arm64-v8a/libapriltag.so /data/local/tmp/quest-test-libapriltag.so
 *   adb push napa_android_check /data/local/tmp/ && adb shell /data/local/tmp/napa_android_check
 *
 * Renders tags 1-6 (libapriltag's own bitmaps) at 24-144 px on a mid-grey 1280x1280 frame, the
 * case where a one-cell quiet zone meets a darker background. Prints one JSON line with each
 * detector's median time over 50 runs and Napa's worst corner offset from libapriltag after
 * QuestX's conversion (corner i = Napa corner {1,0,3,2}[i], +0.5 px). Exit 0 means Napa found
 * every tag, every libapriltag tag matched one, and the corners agree within 0.75 px. */
#include <dlfcn.h>
#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

typedef struct {int width, height, stride; uint8_t *buf;} Image;
typedef struct {size_t elementSize; int size, alloc; void *data;} Array;
typedef struct {void *family; int id, hamming; float margin; void *H; double center[2], corners[8];} Detection;
typedef struct {int min_tag_px, max_hamming, min_contrast, min_border_cells, sharpening_x100;} NapaParams;
typedef struct {int id, hamming; float margin; double corners[8];} NapaDetection;

static double now_ms(void) {
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    return t.tv_sec * 1e3 + t.tv_nsec / 1e6;
}

static int cmp(const void *a, const void *b) {
    double x = *(const double *)a, y = *(const double *)b;
    return (x > y) - (x < y);
}

int main(void) {
    /* NAPA_CHECK_APRILTAG / NAPA_CHECK_NAPA override the library paths for desktop runs. */
    const char *ap = getenv("NAPA_CHECK_APRILTAG"), *np = getenv("NAPA_CHECK_NAPA");
    void *a = dlopen(ap ? ap : "/data/local/tmp/quest-test-libapriltag.so", RTLD_NOW);
    if (!a) { fprintf(stderr, "%s\n", dlerror()); return 1; }
    void *n = dlopen(np ? np : "/data/local/tmp/quest-test-libnapa.so", RTLD_NOW);
    if (!n) { fprintf(stderr, "%s\n", dlerror()); return 1; }
    void *(*familyCreate)(void) = dlsym(a, "tag36h11_create");
    void *(*detectorCreate)(void) = dlsym(a, "apriltag_detector_create");
    void (*addFamily)(void *, void *, int) = dlsym(a, "apriltag_detector_add_family_bits");
    Image *(*render)(void *, int) = dlsym(a, "apriltag_to_image");
    Image *(*create)(int, int) = dlsym(a, "image_u8_create");
    Array *(*detect)(void *, Image *) = dlsym(a, "apriltag_detector_detect");
    void (*destroyDetections)(Array *) = dlsym(a, "apriltag_detections_destroy");
    void *(*napaCreate)(void) = dlsym(n, "napa_create");
    int (*napaDetect)(void *, const uint8_t *, int, int, int, const NapaParams *, NapaDetection *, int) = dlsym(n, "napa_detect");
    if (!familyCreate || !detectorCreate || !addFamily || !render || !create || !detect || !destroyDetections || !napaCreate || !napaDetect) return 2;

    void *family = familyCreate(), *detector = detectorCreate(), *napa = napaCreate();
    addFamily(detector, family, 2);
    const int W = 1280, H = 1280, ntags = 6;
    Image *frame = create(W, H);
    for (int y = 0; y < H; y++) memset(frame->buf + y * frame->stride, 120, W);
    const int scale[6] = {3, 4, 6, 9, 13, 18}; /* black square = 8 * scale px: 24..144 */
    for (int k = 0; k < ntags; k++) {
        Image *tag = render(family, k + 1);
        int ox = 60 + k * 200, oy = 300 + (k % 2) * 500;
        for (int y = 0; y < tag->height * scale[k]; y++)
            for (int x = 0; x < tag->width * scale[k]; x++)
                frame->buf[(oy + y) * frame->stride + ox + x] = tag->buf[(y / scale[k]) * tag->stride + x / scale[k]];
    }

    NapaParams params = {24, 3, 20, 22, 25};
    NapaDetection found[64];
    double napaMs[50], atMs[50], worst = 0;
    int napaCount = 0, atCount = 0, matched = 0;
    for (int run = 0; run < 50; run++) {
        double t0 = now_ms();
        napaCount = napaDetect(napa, frame->buf, W, H, frame->stride, &params, found, 64);
        double t1 = now_ms();
        Array *results = detect(detector, frame);
        double t2 = now_ms();
        napaMs[run] = t1 - t0;
        atMs[run] = t2 - t1;
        atCount = results->size;
        if (run == 49) {
            static const int order[4] = {1, 0, 3, 2};
            for (int i = 0; i < results->size; i++) {
                Detection *d = ((Detection **)results->data)[i];
                for (int j = 0; j < napaCount; j++) {
                    if (found[j].id != d->id) continue;
                    matched++;
                    for (int c = 0; c < 4; c++) {
                        double dx = found[j].corners[2 * order[c]] + 0.5 - d->corners[2 * c];
                        double dy = found[j].corners[2 * order[c] + 1] + 0.5 - d->corners[2 * c + 1];
                        double e = sqrt(dx * dx + dy * dy);
                        if (e > worst) worst = e;
                    }
                }
            }
        }
        destroyDetections(results);
    }
    qsort(napaMs, 50, sizeof(double), cmp);
    qsort(atMs, 50, sizeof(double), cmp);
    printf("{\"napaTags\":%d,\"apriltagTags\":%d,\"matched\":%d,\"worstCornerPx\":%.3f,"
           "\"napaMedianMs\":%.2f,\"apriltagMedianMs\":%.2f}\n",
           napaCount, atCount, matched, worst, napaMs[25], atMs[25]);
    return !(napaCount == ntags && matched == atCount && atCount > 0 && worst < 0.75);
}
