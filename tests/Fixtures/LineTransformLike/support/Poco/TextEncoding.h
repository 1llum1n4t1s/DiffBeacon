#pragma once
#include "Poco/Foundation.h"
// 接続だけを提供し、呼ばれるvirtual処理は原典UTF8Encodingで実行する。
namespace Poco {
class TextEncoding {
public:
    enum { MAX_SEQUENCE_LENGTH = 4 };
    using CharacterMap = int[256];
    virtual ~TextEncoding() = default;
    virtual const char* canonicalName() const = 0;
    virtual bool isA(const std::string&) const = 0;
    virtual const CharacterMap& characterMap() const = 0;
    virtual int convert(const unsigned char*) const = 0;
    virtual int convert(int, unsigned char*, int) const = 0;
    virtual int queryConvert(const unsigned char*, int) const = 0;
    virtual int sequenceLength(const unsigned char*, int) const = 0;
};
}
