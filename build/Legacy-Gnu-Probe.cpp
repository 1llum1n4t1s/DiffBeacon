// 入力 open/stat、設定 globals、JSON 出力のみ。比較は GNU 原本へ渡す。
#include <string>
#include <vector>
#include <fstream>
#include <iostream>
#include <iomanip>
#include <locale.h>
#include <io.h>
#include <fcntl.h>
#include "../Src/diffutils/src/diff.h"
#undef min
#undef max
extern "C" int legacy_probe_too_expensive(void);
extern "C" DECL_TLS int no_discards;

static void equivs_json(const file_data* fd)
{
    std::cout << '[';
    for (int side = 0; side < 2; ++side) {
        if (side) std::cout << ',';
        std::cout << '[';
        // io.c が作った比較区間だけを読む。prefix/suffix、discard 投影は追加しない。
        for (int line = 0; line < fd[side].buffered_lines; ++line) {
            if (line) std::cout << ',';
            std::cout << fd[side].equivs[line];
        }
        std::cout << ']';
    }
    std::cout << ']';
}

static void changes_json(change* script, file_data* fd, bool restored)
{
    std::cout << '[';
    bool first = true;
    for (auto* c = script; c; c = c->link) {
        if (!first) std::cout << ',';
        first = false;
        // 原本 translate_line_number は 1-origin。外部 script は 0-origin。
        int l0 = restored ? translate_line_number(&fd[0], c->line0) - 1 : c->line0;
        int l1 = restored ? translate_line_number(&fd[1], c->line1) - 1 : c->line1;
        std::cout << "{\"line0\":" << l0 << ",\"line1\":" << l1
                  << ",\"deleted\":" << c->deleted << ",\"inserted\":" << c->inserted << '}';
    }
    std::cout << ']';
}

int main(int argc, char** argv)
{
    if (argc != 4) { std::cerr << "usage: probe left-path right-path mode\n"; return 2; }
    setlocale(LC_ALL, "C");
    output_style = OUTPUT_NORMAL;
    context = 0; heuristic = 1; no_discards = 0; always_text_flag = 0;
    horizon_lines = 0; no_details_flag = 0; line_end_char = '\n';
    ignore_space_change_flag = ignore_all_space_flag = ignore_blank_lines_flag = 0;
    ignore_case_flag = ignore_numbers_flag = ignore_eol_diff = 0;
    ignore_some_changes = length_varies = 0;
    std::string mode = argv[3];
    if (mode == "case") ignore_case_flag = ignore_some_changes = 1;
    else if (mode == "space-change") ignore_space_change_flag = ignore_some_changes = length_varies = 1;
    else if (mode == "space-all") ignore_all_space_flag = ignore_some_changes = length_varies = 1;
    else if (mode == "numbers") ignore_numbers_flag = 1; // CompareOptions::SetToDiffUtils は numbers を length_varies に含めない。
    else if (mode == "eol") ignore_eol_diff = ignore_some_changes = 1;
    else if (mode != "default") { std::cerr << "unsupported flags\n"; return 3; }
    file_data fd[2] = {};
    for (int i = 0; i < 2; ++i) {
        fd[i].name = argv[1 + i];
        fd[i].desc = _open(fd[i].name, _O_RDONLY | _O_BINARY);
        if (fd[i].desc < 0 || _fstat64(fd[i].desc, &fd[i].stat) != 0 || !S_ISREG(fd[i].stat.st_mode)) {
            std::cerr << "input must be a regular file\n"; return 4;
        }
    }
    int binaryStatus = 0, binaryFiles = 0;
    change* script = diff_2_files(fd, 0, &binaryStatus, 0, &binaryFiles);
    std::cout << "{\"binaryStatus\":" << binaryStatus << ",\"binaryFiles\":" << binaryFiles
              << ",\"prefixLines\":[" << fd[0].prefix_lines << ',' << fd[1].prefix_lines << ']'
              << ",\"bufferedLines\":[" << fd[0].buffered_lines << ',' << fd[1].buffered_lines << ']'
              << ",\"validLines\":[" << fd[0].valid_lines << ',' << fd[1].valid_lines << ']'
              << ",\"nondiscardedLines\":[" << fd[0].nondiscarded_lines << ',' << fd[1].nondiscarded_lines << ']'
              << ",\"missingNewline\":[" << fd[0].missing_newline << ',' << fd[1].missing_newline << ']'
              << ",\"tooExpensive\":" << legacy_probe_too_expensive()
              << ",\"lengths\":[" << fd[0].buffered_lines << ',' << fd[1].buffered_lines << ']'
              << ",\"equivMax\":[" << fd[0].equiv_max << ',' << fd[1].equiv_max << ']'
              << ",\"classCount\":" << fd[0].equiv_max << ",\"equivs\":";
    equivs_json(fd);
    std::cout << ",\"rawChanges\":";
    changes_json(script, fd, false);
    std::cout << ",\"changes\":";
    changes_json(script, fd, true);
    std::cout << "}\n";
    for (int i = 0; i < 2; ++i) _close(fd[i].desc);
    // 原本 cleanup_file_buffers は text のみ。この採取は一実行一ケースで終了し OS が回収する。
    while (script) { auto* next = script->link; free(script); script = next; }
    return 0;
}
