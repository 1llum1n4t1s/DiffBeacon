/* 採取範囲外の呼出しは成功を偽装せず即座に失敗する。 */
#include "../../Src/diffutils/src/diff.h"
void moved_block_analysis(struct change **script, struct file_data fd[])
{
    (void)script; (void)fd;
    fputs("unsupported moved-block path\n", stderr);
    exit(90);
}
void print_context_header(struct file_data fd[], int unified)
{
    (void)fd; (void)unified;
    fputs("unsupported context-output path\n", stderr);
    exit(91);
}
