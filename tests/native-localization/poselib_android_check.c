#include <stdio.h>
#include <math.h>
#include <stdint.h>
#include <dlfcn.h>
typedef struct { double q[4]; double t[3]; } Pose;
typedef int (*Solve)(double*,double*,uint64_t,int,int,int,double*,uint64_t,double,Pose*,uint64_t*);
int main(void) {
    void *lib=dlopen("/data/local/tmp/quest-test-libposelib.so",RTLD_NOW);
    if(!lib){ fprintf(stderr,"%s\n",dlerror());return 1; }
    Solve solve=(Solve)dlsym(lib,"poselib_estimate_absolute_pose_simple");
    if(!solve){ fprintf(stderr,"%s\n",dlerror());return 1; }
    printf("[\n");
    for(int tags=1;tags<=2;tags++) {
        double xyz[24],uv[16],intrinsics[4]={700,700,640,640};
        int signs[4][2]={{1,-1},{-1,-1},{-1,1},{1,1}};
        for(int i=0;i<tags*4;i++) {
            xyz[i*3]=4;
            xyz[i*3+1]=3+(i/4)*0.6+signs[i%4][0]*0.08255;
            xyz[i*3+2]=1.5+signs[i%4][1]*0.08255;
            uv[i*2]=640-700*(xyz[i*3+1]-3)/2;
            uv[i*2+1]=640-700*(xyz[i*3+2]-1.5)/2;
        }
        Pose p={0}; uint64_t inliers=0;
        int status=solve(uv,xyz,tags*4,1,1280,1280,intrinsics,4,12,&p,&inliers);
        double w=p.q[0],x=-p.q[1],y=-p.q[2],z=-p.q[3];
        double a=-p.t[0],b=-p.t[1],c=-p.t[2];
        double camera[3]={
            (1-2*(y*y+z*z))*a+2*(x*y-z*w)*b+2*(x*z+y*w)*c,
            2*(x*y+z*w)*a+(1-2*(x*x+z*z))*b+2*(y*z-x*w)*c,
            2*(x*z-y*w)*a+2*(y*z+x*w)*b+(1-2*(x*x+y*y))*c};
        double error=sqrt(pow(camera[0]-2,2)+pow(camera[1]-3,2)+pow(camera[2]-1.5,2));
        printf("%s{\"tags\":%d,\"status\":%d,\"inliers\":%llu,\"camera\":[%.8f,%.8f,%.8f],\"positionErrorMeters\":%.10f}",tags==2?",\n":"",tags,status,(unsigned long long)inliers,camera[0],camera[1],camera[2],error);
        if(status!=0 || inliers!=tags*4 || error>0.001) return 2;
    }
    printf("\n]\n");
    return 0;
}
