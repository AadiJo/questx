#include <stdio.h>
#include <stdint.h>
#include <string.h>
#include <math.h>
#include <dlfcn.h>
typedef struct {int width,height,stride; uint8_t *buf;} Image;
typedef struct {size_t elementSize; int size,alloc; void *data;} Array;
typedef struct {void *family; int id,hamming; float margin; void *H; double center[2],corners[8];} Detection;
typedef struct { double q[4]; double t[3]; } Pose;
typedef int (*Solve)(double*,double*,uint64_t,int,int,int,double*,uint64_t,double,Pose*,uint64_t*);
int main(void) {
    void *a=dlopen("/data/local/tmp/quest-test-libapriltag.so",RTLD_NOW);
    void *p=dlopen("/data/local/tmp/quest-test-libposelib.so",RTLD_NOW);
    if(!a||!p){fprintf(stderr,"%s\n",dlerror());return 1;}
    void* (*familyCreate)(void)=dlsym(a,"tag36h11_create");
    void* (*detectorCreate)(void)=dlsym(a,"apriltag_detector_create");
    void (*addFamily)(void*,void*,int)=dlsym(a,"apriltag_detector_add_family_bits");
    Image* (*render)(void*,int)=dlsym(a,"apriltag_to_image");
    Image* (*create)(int,int)=dlsym(a,"image_u8_create");
    Array* (*detect)(void*,Image*)=dlsym(a,"apriltag_detector_detect");
    Solve solve=dlsym(p,"poselib_estimate_absolute_pose_simple");
    if(!familyCreate||!detectorCreate||!addFamily||!render||!create||!detect||!solve) return 2;
    void *family=familyCreate(),*detector=detectorCreate();
    addFamily(detector,family,2);
    Image *small=render(family,1),*large=create(1280,1280);
    memset(large->buf,255,large->height*large->stride);
    for(int y=0;y<small->height*20;y++) for(int x=0;x<small->width*20;x++)
        large->buf[(540+y)*large->stride+540+x]=small->buf[(y/20)*small->stride+x/20];
    Array *results=detect(detector,large);
    if(results->size!=1){printf("detected %d tags\n",results->size);return 3;}
    Detection *tag=((Detection**)results->data)[0];
    double xyz[]={4,3.08255,1.41745, 4,2.91745,1.41745, 4,2.91745,1.58255, 4,3.08255,1.58255};
    double intrinsics[]={700,700,640,640}; Pose pose={0}; uint64_t inliers=0;
    int status=solve(tag->corners,xyz,4,1,1280,1280,intrinsics,4,12,&pose,&inliers);
    double w=pose.q[0],x=-pose.q[1],y=-pose.q[2],z=-pose.q[3];
    double tx=-pose.t[0],ty=-pose.t[1],tz=-pose.t[2];
    double camera[]={ (1-2*(y*y+z*z))*tx+2*(x*y-z*w)*ty+2*(x*z+y*w)*tz,
        2*(x*y+z*w)*tx+(1-2*(x*x+z*z))*ty+2*(y*z-x*w)*tz,
        2*(x*z-y*w)*tx+2*(y*z+x*w)*ty+(1-2*(x*x+y*y))*tz};
    double expected=4-700*.1651/160;
    double error=sqrt(pow(camera[0]-expected,2)+pow(camera[1]-3,2)+pow(camera[2]-1.5,2));
    printf("{\"tagId\":%d,\"hamming\":%d,\"status\":%d,\"inliers\":%llu,\"firstCorner\":[%.5f,%.5f],\"camera\":[%.6f,%.6f,%.6f],\"errorMeters\":%.6f}\n",tag->id,tag->hamming,status,(unsigned long long)inliers,tag->corners[0],tag->corners[1],camera[0],camera[1],camera[2],error);
    return !(tag->id==1 && status==0 && inliers==4 && error<.02);
}
