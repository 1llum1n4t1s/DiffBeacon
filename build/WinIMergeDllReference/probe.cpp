// GPL-2.0-or-later。固定公開headerと配布DLLだけで原画を採取する。
#include "WinIMergeLib.h"
#include <fstream>
#include <iostream>
#include <string>
#include <stdexcept>
#include <vector>

static void State(IImgMergeWindow* api, int index, int actionResult) {
    std::cout << "{\"stateIndex\":" << index << ",\"actionResult\":" << actionResult
        << ",\"differenceCount\":" << api->GetDiffCount() << ",\"conflictCount\":" << api->GetConflictCount()
        << ",\"undoable\":" << (api->IsUndoable() ? "true" : "false")
        << ",\"redoable\":" << (api->IsRedoable() ? "true" : "false") << ",\"panes\":[";
    for (int pane = 0; pane < api->GetPaneCount(); ++pane) {
        if (pane) std::cout << ',';
        const int w = api->GetImageWidth(pane), h = api->GetImageHeight(pane);
        if (w <= 0 || h <= 0 || static_cast<long long>(w) * h > 16000000) throw std::runtime_error("bad canvas");
        const std::string file = "state" + std::to_string(index) + "-pane" + std::to_string(pane) + ".bgra";
        std::ofstream raw(file, std::ios::binary);
        for (int y = 0; y < h; ++y) for (int x = 0; x < w; ++x) {
            const RGBQUAD pixel = api->GetPixelColor(pane, x, y);
            raw.write(reinterpret_cast<const char*>(&pixel), 4);
        }
        raw.close(); if (!raw) throw std::runtime_error("raw output failed");
        std::cout << "{\"width\":" << w << ",\"height\":" << h << ",\"bpp\":" << api->GetImageBitsPerPixel(pane)
            << ",\"modified\":" << (api->IsModified(pane) ? "true" : "false")
            << ",\"savepoint\":" << api->GetSavePoint(pane) << ",\"rawFile\":\"" << file << "\"}";
    }
    std::cout << "]}" << std::endl;
}

int wmain(int argc, wchar_t** argv) {
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
    SetThreadErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX, nullptr);
    if (argc != 2) return 2;
    HMODULE dll = LoadLibraryExW(argv[1], nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!dll) { std::cerr << "LoadLibrary " << GetLastError() << std::endl; return 3; }
    using Create = IImgMergeWindow* (*)(); using Destroy = bool (*)(IImgMergeWindow*);
    auto create = reinterpret_cast<Create>(GetProcAddress(dll, "WinIMerge_CreateWindowless"));
    auto destroy = reinterpret_cast<Destroy>(GetProcAddress(dll, "WinIMerge_DestroyWindow"));
    if (!create || !destroy) { FreeLibrary(dll); return 4; }
    IImgMergeWindow* api = nullptr;
    try {
        int n, block, actionCount; double threshold; std::cin >> n >> block >> threshold;
        if (n != 2 && n != 3) throw std::runtime_error("bad pane count");
        std::vector<int> readonly(n); for (int& flag : readonly) std::cin >> flag;
        std::cin >> actionCount;
        api = create(); if (!api) throw std::runtime_error("CreateWindowless failed");
        api->SetPreferWICDecoder(false);
        const bool loaded = n == 2 ? api->OpenImages(L"pane0.png", L"pane1.png") : api->OpenImages(L"pane0.png", L"pane1.png", L"pane2.png");
        if (!loaded) throw std::runtime_error("OpenImages failed");
        api->SetInsertionDeletionDetectionMode(IImgMergeWindow::INSERTION_DELETION_DETECTION_NONE);
        api->SetOverlayMode(IImgMergeWindow::OVERLAY_NONE); api->SetShowDifferences(false); api->SetBlinkDifferences(false);
        api->SetDiffBlockSize(block); api->SetColorDistanceThreshold(threshold);
        for (int pane = 0; pane < n; ++pane) {
            api->SetRotation(pane, 0); api->SetHorizontalFlip(pane, false); api->SetVerticalFlip(pane, false);
            const auto offset = api->GetImageOffset(pane); api->AddImageOffset(pane, -offset.x, -offset.y);
            api->SetReadOnly(pane, readonly[pane] != 0);
        }
        State(api, 0, -1);
        for (int i = 0; i < actionCount; ++i) {
            std::string kind; int src, dst, index; std::cin >> kind >> src >> dst >> index;
            if (!std::cin) throw std::runtime_error("bad action stream");
            int result = -1;
            if (kind == "copy") api->CopyDiff(index, src, dst);
            else if (kind == "all") api->CopyDiffAll(src, dst);
            else if (kind == "auto") result = api->CopyDiff3Way(dst);
            else if (kind == "undo") result = api->Undo();
            else if (kind == "redo") result = api->Redo();
            else if (kind == "set-savepoint") api->SetSavePoint(dst, index);
            else if (kind == "save") {
                // 診断markの代替に実保存を使用する。原本SaveImageAsにguardを追加しない。
                const auto path = L"mark-save-" + std::to_wstring(i) + L".png";
                if (!api->SaveImageAs(dst, path.c_str())) throw std::runtime_error("save-mark via actual PNG failed");
            } else throw std::runtime_error("unknown action");
            State(api, i + 1, result);
        }
        // 全state採取後だけ実PNGを保存する。保存結果はraw結果と独立に照合する。
        for (int pane = 0; pane < n; ++pane) {
            auto path = L"final-pane" + std::to_wstring(pane) + L".png";
            const bool saved = api->SaveImageAs(pane, path.c_str());
            std::cout << "{\"exportPane\":" << pane << ",\"success\":" << (saved ? "true" : "false") << "}" << std::endl;
        }
        api->CloseImages(); destroy(api); api = nullptr; FreeLibrary(dll); return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << std::endl;
        if (api) { api->CloseImages(); destroy(api); } FreeLibrary(dll); return 5;
    }
}
