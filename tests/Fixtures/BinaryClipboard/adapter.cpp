// Original-function harvesting adapter, 2026-10-04. GPLv2, see GPL.txt.
// Algorithm bodies are verbatim fixed-commit extracts in original-functions.inc.
#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <cstdio>
#include <cstring>
#include <clocale>
#include <fstream>
#include <iterator>
#include <vector>
#include <string>
#define ANSI_SET ANSI_FIXED_FONT
#define OEM_SET OEM_FIXED_FONT
class HexEditorWindow { public: enum { ENDIAN_LITTLE=0, ENDIAN_BIG=1 }; };
class Text2BinTranslator {
public:
    static size_t iFindBytePos(const char*, char);
    static size_t iBytes2BytecodeDestLen(const BYTE*, size_t);
    static int iIsBytecode(const char*, size_t);
    static int iTranslateOneBytecode(BYTE*, const char*, size_t, int);
    static size_t iLengthOfTransToBin(const char*, size_t);
    static size_t iCreateBcTranslation(BYTE*, const char*, size_t, int, int);
    static size_t iTranslateBytesToBC(char*, const BYTE*, size_t);
};
#include "original-functions.inc"
int main(int argc, char** argv) {
    if (argc != 5 || !setlocale(LC_ALL, "C")) return 2;
    std::ifstream in(argv[3], std::ios::binary);
    if (!in) return 3;
    std::vector<char> input((std::istreambuf_iterator<char>(in)), {});
    const size_t count = input.size();
    if (count > 8192) return 4;
    input.push_back(0); // iFindBytePos uses strchr; the caller's text is NUL terminated.
    const bool encode = std::string(argv[1]) == "encode";
    if (!encode && std::string(argv[1]) != "decode") return 5;
    const int endian = std::string(argv[2]) == "little" ? 0 : 1;
    const size_t capacity = encode ? Text2BinTranslator::iBytes2BytecodeDestLen((BYTE*)input.data(),count) : Text2BinTranslator::iLengthOfTransToBin(input.data(),count);
    BYTE* output = new BYTE[capacity ? capacity : 1];
    const size_t returned = encode ? Text2BinTranslator::iTranslateBytesToBC((char*)output,(BYTE*)input.data(),count) : Text2BinTranslator::iCreateBcTranslation(output,input.data(),count,ANSI_SET,endian);
    if (returned != capacity || (encode && output[returned-1] != 0)) return 6;
    const size_t length = encode ? returned-1 : returned;
    std::ofstream out(argv[4],std::ios::binary);
    if (!out) return 7;
    out.write((char*)output,length);
    out.close();
    if (!out) return 8;
    printf("{\"inputLength\":%zu,\"capacity\":%zu,\"returned\":%zu,\"outputLength\":%zu,\"encoderNulVerified\":%s,\"ansiConstant\":%d,\"oemConstant\":%d,\"locale\":\"C\"}\n",count,capacity,returned,length,encode ? "true" : "false",ANSI_SET,OEM_SET);
    delete[] output;
    return 0;
}
