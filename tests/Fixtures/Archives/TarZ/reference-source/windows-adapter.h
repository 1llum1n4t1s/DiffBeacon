/* 研究用 Windows CRT/宣言adapter。圧縮アルゴリズムを変更しない。 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ctype.h>
#include <signal.h>
#include <sys/types.h>
#include <sys/stat.h>
#include <sys/utime.h>
#include <errno.h>
#include <io.h>
#include <fcntl.h>
#include <time.h>
typedef struct { int unused; } DIR;
struct dirent { unsigned long d_ino; char d_name[260]; };
static DIR *opendir(const char *path) { (void)path; errno = ENOSYS; return NULL; }
static struct dirent *readdir(DIR *dir) { (void)dir; errno = ENOSYS; return NULL; }
static int closedir(DIR *dir) { (void)dir; return 0; }
static int research_chown(const char *path, int uid, int gid) { (void)path; (void)uid; (void)gid; errno = ENOSYS; return -1; }
static int research_utime(const char *path, const time_t times[2]) { struct _utimbuf value; value.actime = times[0]; value.modtime = times[1]; return _utime(path, &value); }
#define chmod _chmod
#define chown research_chown
#define utime research_utime
#define unlink _unlink
#define isatty _isatty
#define read _read
#define write _write
char *rindex();
