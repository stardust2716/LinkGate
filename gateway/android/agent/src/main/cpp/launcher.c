/* Verified Agent-owned child guard. No shell, path lookup or daemon. */
#include <errno.h>
#include <limits.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <sys/prctl.h>
#include <unistd.h>

int main(int argc,char **argv) {
    if(argc<3||argv[2][0]!='/')return 126;
    char *end=NULL;errno=0;long expected=strtol(argv[1],&end,10);
    if(errno||!end||*end||expected<=1||expected>INT_MAX)return 126;
    if(prctl(PR_SET_PDEATHSIG,SIGKILL)!=0||getppid()!=(pid_t)expected)return 126;
    if(printf("LINKGATE_CHILD %ld\n",(long)getpid())<0||fflush(stdout)!=0)return 126;
    execv(argv[2],&argv[2]);return 127;
}
