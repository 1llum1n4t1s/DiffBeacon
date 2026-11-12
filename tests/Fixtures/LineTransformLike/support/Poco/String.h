#pragma once
#include <string>
#include <stdexcept>
namespace Poco {
// UTF8Encoding::isA is not used by Glob. Refuse unexpected routing.
inline int icompare(const std::string&, const char*) {
    throw std::runtime_error("unused encoding-name comparison");
}
}
