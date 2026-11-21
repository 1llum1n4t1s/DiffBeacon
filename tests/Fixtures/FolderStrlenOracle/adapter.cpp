// AST spy・context・serializerのみ提供する。Strlen本体とValueType宣言は原典をincludeする。
#include <algorithm>
#include <cstdint>
#include <iostream>
#include <memory>
#include <stdexcept>
#include <string>
#include <variant>
#include <vector>
#include <Poco/Timestamp.h>
#include <Poco/Timezone.h>
#include <Poco/LocalDateTime.h>
#include <Poco/DateTimeFormatter.h>
#include <Poco/Exception.h>
#include <Poco/Format.h>
namespace Poco {
// 不正日時の例外整形は対象外。到達した場合は採取を失敗させる。
void format(std::string&, const char*, const std::vector<Any>&) { throw std::runtime_error("unsupported invalid timestamp diagnostic"); }
}
#include "value-type.inc"
struct FilterEvalContext {};
// FileContentRefは未対応case。誤って比較された場合に採取を失敗させる。
struct FileContentRef {
    std::wstring path;
    bool operator==(const FileContentRef&) const { throw std::runtime_error("unsupported FileContentRef"); }
};
#include "expr-node.inc"
namespace ucr {
#include "utf8.inc"
std::string toUTF8(const std::wstring&) { throw std::runtime_error("unsupported FileContentRef"); }
}
std::string ToStringValue(const ValueType&);
#include "to-string.inc"
#include "apply.inc"
#include "strlen.inc"
#include "function-info.inc"
static constexpr FunctionInfo registration[] = {
#include "registration.inc"
};
// constructor全体ではなく、その引数数検証ブロックだけを無変更で実行する。
using InvalidArgumentCountError = std::runtime_error;
void validateArity(std::vector<ExprNode*>* args) {
    const FunctionInfo* info = &registration[0];
    const std::string functionName = info->name;
#include "arity-check.inc"
}
ValueType array(std::initializer_list<ValueType> values) {
    auto result = std::make_shared<std::vector<ValueType2>>();
    for (const auto& value : values) result->push_back(ValueType2{value});
    return result;
}
std::string quoted(const std::string& text) {
    std::string result = "\"";
    for (unsigned char c : text) { if (c < 32) { const char* hex="0123456789abcdef"; result += "\\u00"; result += hex[c >> 4]; result += hex[c & 15]; continue; } if (c == '\\' || c == '"') result += '\\'; result += c; }
    return result + "\"";
}
void outputResult(const ValueType& result) {
    if (auto n=std::get_if<int64_t>(&result)) std::cout << *n;
    else if (auto b=std::get_if<bool>(&result)) std::cout << (*b ? "true" : "false");
    else if (auto a=std::get_if<std::shared_ptr<std::vector<ValueType2>>>(&result)) {
        std::cout << '['; bool first=true;
        for (auto& item:**a) { if (!first) std::cout << ','; first=false; outputResult(item.value); }
        std::cout << ']';
    } else if (std::holds_alternative<std::monostate>(result)) std::cout << "null";
    else throw std::runtime_error("unexpected native result type");
}
struct Spy final : ExprNode {
    ValueType value;
    int index;
    bool throws;
    std::vector<int>* trace;
    Spy(ValueType v, int i, bool t, std::vector<int>* log) : value(v), index(i), throws(t), trace(log) {}
    ValueType Evaluate(const FilterEvalContext&) const override {
        trace->push_back(index);
        if (throws) throw std::runtime_error("argument-" + std::to_string(index));
        return value;
    }
};
struct Input { ValueType value; bool throws = false; };
void run(const std::string& id, const std::string& mode, std::initializer_list<Input> inputs) {
    std::vector<int> trace;
    std::vector<std::unique_ptr<Spy>> owned;
    std::vector<ExprNode*> args;
    for (const auto& input : inputs) {
        owned.push_back(std::make_unique<Spy>(input.value, int(args.size()), input.throws, &trace));
        args.push_back(owned.back().get());
    }
    std::string error;
    ValueType result;
    try {
        if (mode == "arity") { validateArity(&args); result = true; }
        else {
            if (args.size() != 1) throw std::runtime_error("adapter requires one argument for evaluation");
            result = StrlenFunc(FilterEvalContext{}, &args);
        }
    }
    catch (const std::exception& exception) { error = exception.what(); }
    std::cout << "{\"id\":" << quoted(id) << ",\"mode\":" << quoted(mode) << ",\"result\":";
    if (error.empty()) outputResult(result); else std::cout << "null";
    std::cout << ",\"stringified\":";
    if (error.empty() && mode == "evaluate") std::cout << quoted(ToStringValue(owned[0]->value)); else std::cout << "null";
    std::cout << ",\"error\":" << quoted(error) << ",\"evaluated\":[";
    for (size_t i = 0; i < trace.size(); ++i) { if (i) std::cout << ','; std::cout << trace[i]; }
    std::cout << "]}\n";
}
int main() {
    try {
        if (Poco::Timezone::utcOffset()!=32400) throw std::runtime_error("fixture requires Windows UTC+09:00 timezone");
#include "cases.inc"
    } catch (const std::exception& exception) { std::cerr << exception.what(); return 3; }
    return 0;
}
