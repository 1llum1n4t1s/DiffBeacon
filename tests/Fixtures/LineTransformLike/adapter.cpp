// 検証専用接続。照合・Unicode処理の本体は固定原典を実行する。
#include <iostream>
#include <initializer_list>
#include <stdexcept>
#include "Poco/Glob.h"
#include "Poco/UTF8Encoding.h"
#include "Poco/Unicode.h"
namespace Poco {
class SyntaxException : public std::runtime_error {
public: using std::runtime_error::runtime_error;
};
#include "glob.inc"
}
static std::string bytes(std::initializer_list<unsigned int> values) {
    std::string result;
    for (auto value : values) result.push_back(static_cast<char>(value));
    return result;
}
static void run(const char* id, const std::string& subject, const std::string& pattern, bool sensitive) {
    try {
        Poco::Glob glob(pattern, sensitive ? 0 : Poco::Glob::GLOB_CASELESS);
        bool value = glob.match(subject);
        std::cout << "{\"id\":\"" << id << "\",\"status\":\"ok\",\"value\":"
                  << (value ? "true" : "false") << "}\n";
    } catch (const Poco::SyntaxException&) {
        std::cout << "{\"id\":\"" << id << "\",\"status\":\"error\",\"errorType\":\"SyntaxException\"}\n";
    }
}
int main() {
#include "cases.inc"
}
