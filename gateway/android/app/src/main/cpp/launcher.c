/* User-owned child lifetime guard. No shell, path lookup, daemon or PID scan. */
#include <errno.h>
#include <limits.h>
#include <signal.h>
#include <stdlib.h>
#include <sys/prctl.h>
#include <unistd.h>

int main(int argc, char **argv) {
    if (argc < 3 || argv[2][0] != '/') return 126;
    char *end = NULL;
    errno = 0;
    long expected = strtol(argv[1], &end, 10);
    if (errno || !end || *end || expected <= 1 || expected > INT_MAX) return 126;
    if (prctl(PR_SET_PDEATHSIG, SIGTERM) != 0) return 126;
    /* Setting the death signal after the Java parent already died would miss
       that death; validate the parent again before executing the real client. */
    if (getppid() != (pid_t)expected) return 126;
    execv(argv[2], &argv[2]);
    return 127;
}
