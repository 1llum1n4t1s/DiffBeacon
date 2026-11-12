// AST spy・context・serializerのみ提供する。At / Array本体とValueType宣言は原典をincludeする。
#include <algorithm>
#include <optional>
#include <cstdint>
#include <iostream>
#include <iomanip>
#include <memory>
#include <stdexcept>
#include <string>
#include <variant>
#include <vector>
namespace Poco {
struct RegularExpression { std::string token; explicit RegularExpression(std::string t): token(t) {} };
class Timestamp {
public:
    using TimeVal = int64_t;
    Timestamp(TimeVal); Timestamp(const Timestamp&); ~Timestamp();
    bool operator == (const Timestamp&) const; TimeVal epochMicroseconds() const;
private: TimeVal _ts;
};
#include "timestamp.inc"
}
#include "value-type.inc"
struct FilterEvalContext {};
// Opaque native slots: adapter tokens only; no regex/content behavior is modeled.
struct FileContentRef { std::string token, path; explicit FileContentRef(std::string t): token(t), path(t) {} };
#include "expr-node.inc"
#include "array.inc"
namespace Poco {
struct Exception : std::runtime_error { using std::runtime_error::runtime_error; };
struct LocalDateTime { explicit LocalDateTime(Timestamp) { throw std::runtime_error("unsupported timestamp binding"); } };
struct DateTimeFormatter { static std::string format(LocalDateTime,const char*) { throw std::runtime_error("unsupported timestamp binding"); } };
}
namespace ucr { std::string toUTF8(const std::string& s) { return s; } }
#include "to-string.inc"
#include "convert-string.inc"
#include "scalar-array.inc"
#include "trim.inc"
#include "trimleft.inc"
#include "trimright.inc"
#include "logical-truth.inc"
#include "direct-truth.inc"
#include "function-info.inc"
static constexpr FunctionInfo registration[] = {
#include "registration.inc"
};
// constructor全体ではなく、その引数数検証ブロックだけを無変更で実行する。
using InvalidArgumentCountError = std::runtime_error;
void validateArity(const std::string& function, std::vector<ExprNode*>* args) {
    const FunctionInfo* info = &registration[function=="trim"?0:function=="trimleft"?1:2];
    const std::string functionName = info->name;
#include "arity-check.inc"
}
ValueType array(std::initializer_list<ValueType> values) {
    auto result = std::make_shared<std::vector<ValueType2>>();
    for (const auto& value : values) result->push_back(ValueType2{value});
    return result;
}
std::string quoted(const std::string& text) {
    static const char* hex = "0123456789abcdef";
    std::string result = "\"";
    for (unsigned char c : text) {
        if (c < 32 || c == '\\' || c == '"') {
            result += "\\u00"; result += hex[c>>4]; result += hex[c&15];
        } else result += c;
    }
    return result + "\"";
}
void shape(const ValueType& v) {
    switch(v.index()) {
    case 0: std::cout << "{\"type\":\"none\"}"; break;
    case 1: std::cout << "{\"type\":\"boolean\",\"value\":" << (std::get<bool>(v)?"true":"false") << '}'; break;
    case 2: std::cout << "{\"type\":\"double\",\"value\":" << std::setprecision(17) << std::get<double>(v) << '}'; break;
    case 3: std::cout << "{\"type\":\"integer\",\"value\":" << std::get<int64_t>(v) << '}'; break;
    case 4: std::cout << "{\"type\":\"timestamp\",\"value\":" << std::get<Poco::Timestamp>(v).epochMicroseconds() << '}'; break;
    case 5: std::cout << "{\"type\":\"regex\",\"value\":" << quoted(std::get<std::shared_ptr<Poco::RegularExpression>>(v)->token) << '}'; break;
    case 7: std::cout << "{\"type\":\"content\",\"value\":" << quoted(std::get<std::shared_ptr<FileContentRef>>(v)->token) << '}'; break;
    case 6: std::cout << "{\"type\":\"string\",\"value\":" << quoted(std::get<std::string>(v)) << '}'; break;
    case 8: {
        std::cout << "{\"type\":\"array\",\"value\":[";
        const auto& values = *std::get<std::shared_ptr<std::vector<ValueType2>>>(v);
        for (size_t i=0; i<values.size(); ++i) { if(i) std::cout << ','; shape(values[i].value); }
        std::cout << "]}"; break;
    }
    default: throw std::runtime_error("unsupported serializer slot");
    }
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
struct ArraySpy final : ExprNode {
    std::vector<ExprNode*> children; std::vector<int>* trace;
    ArraySpy(std::vector<ExprNode*> c, std::vector<int>* t): children(c),trace(t) {}
    ValueType Evaluate(const FilterEvalContext& ctx) const override {
        trace->push_back(0); auto args=children; return ArrayFunc(ctx,&args);
    }
};
void run(const std::string& id, const std::string& function, const std::string& mode, std::initializer_list<Input> inputs, std::initializer_list<Input> elements) {
    std::vector<int> trace;
    std::vector<std::unique_ptr<Spy>> owned;
    std::vector<ExprNode*> args;
    for (const auto& input : inputs) {
        owned.push_back(std::make_unique<Spy>(input.value, int(args.size()), input.throws, &trace));
        args.push_back(owned.back().get());
    }
    std::vector<std::unique_ptr<Spy>> elementOwned;
    std::vector<ExprNode*> elementArgs;
    for (const auto& input : elements) {
        elementOwned.push_back(std::make_unique<Spy>(input.value, 10+int(elementArgs.size()),input.throws,&trace));
        elementArgs.push_back(elementOwned.back().get());
    }
    ArraySpy arrayExpr(elementArgs,&trace);
    if (mode == "array-expr") args[0]=&arrayExpr;
    std::string error;
    ValueType result;
    try {
        if (mode == "arity") { validateArity(function, &args); result = true; }
        else {
            result = (function=="trim"?TrimFunc:function=="trimleft"?TrimLeftFunc:TrimRightFunc)(FilterEvalContext{}, &args);
        }
    }
    catch (const std::exception& exception) { error = exception.what(); }
    std::cout << "{\"id\":" << quoted(id) << ",\"function\":" << quoted(function) << ",\"mode\":" << quoted(mode) << ",\"result\":";
    if (error.empty() && mode == "arity") std::cout << "true";
    else if (error.empty()) shape(result); else std::cout << "null";
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
