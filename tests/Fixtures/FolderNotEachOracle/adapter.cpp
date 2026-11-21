// AST spy・context・serializerのみ提供する。NotEach本体とValueType宣言は原典をincludeする。
#include <algorithm>
#include <cstdint>
#include <iostream>
#include <memory>
#include <optional>
#include <stdexcept>
#include <string>
#include <variant>
#include <vector>
namespace Poco {
class Timestamp {
public:
    using TimeVal = int64_t;
    Timestamp(TimeVal); Timestamp(const Timestamp&); ~Timestamp();
    bool operator == (const Timestamp&) const;
private: TimeVal _ts;
};
#include "timestamp.inc"
}
#include "value-type.inc"
struct FilterEvalContext {};
// FileContentRefは未対応case。誤って比較された場合に採取を失敗させる。
struct FileContentRef {
    bool operator==(const FileContentRef&) const { throw std::runtime_error("unsupported FileContentRef"); }
};
#include "expr-node.inc"
#include "eval-bool.inc"
#include "apply.inc"
#include "not-each.inc"
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
    for (char c : text) { if (c == '\\' || c == '"') result += '\\'; result += c; }
    return result + "\"";
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
void printResult(const ValueType& value) {
    if (std::holds_alternative<std::monostate>(value)) std::cout << "null";
    else if (auto b = std::get_if<bool>(&value)) std::cout << (*b ? "true" : "false");
    else if (auto items = std::get_if<std::shared_ptr<std::vector<ValueType2>>>(&value)) {
        std::cout << '[';
        for (size_t i = 0; i < (*items)->size(); ++i) { if (i) std::cout << ','; printResult((*items)->at(i).value); }
        std::cout << ']';
    } else throw std::runtime_error("unsupported result type");
}
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
            result = NotEachFunc(FilterEvalContext{}, &args);
        }
    }
    catch (const std::exception& exception) { error = exception.what(); }
    std::cout << "{\"id\":" << quoted(id) << ",\"mode\":" << quoted(mode) << ",\"result\":";
    if (error.empty()) printResult(result); else std::cout << "null";
    std::cout << ",\"error\":" << quoted(error) << ",\"evaluated\":[";
    for (size_t i = 0; i < trace.size(); ++i) { if (i) std::cout << ','; std::cout << trace[i]; }
    std::cout << "]}\n";
}
int main() {
    try {
#include "cases.inc"
    } catch (const std::exception& exception) { std::cerr << exception.what(); return 3; }
    return 0;
}
