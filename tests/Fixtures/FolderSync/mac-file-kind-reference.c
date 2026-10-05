#include <sys/stat.h>
#include <stddef.h>
#include <stdio.h>

/* 検証専用。製品のコンパイラー依存へ追加しない。 */
int main(void)
{
    printf("{\"statSize\":%zu,\"modeOffset\":%zu,\"modeSize\":%zu,\"typeMask\":%u,\"regular\":%u,\"directory\":%u,\"fifo\":%u,\"socket\":%u}\n",
        sizeof(struct stat), offsetof(struct stat, st_mode), sizeof(((struct stat *)0)->st_mode),
        (unsigned)S_IFMT, (unsigned)S_IFREG, (unsigned)S_IFDIR, (unsigned)S_IFIFO, (unsigned)S_IFSOCK);
    return sizeof(struct stat) <= 512 && offsetof(struct stat, st_mode) == 4
        && sizeof(((struct stat *)0)->st_mode) == 2 && S_IFMT == 0xF000
        && S_IFREG == 0x8000 && S_IFDIR == 0x4000 ? 0 : 1;
}
