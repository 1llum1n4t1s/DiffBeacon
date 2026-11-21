// 検証用接続。incは固定原本の無改変bytes。
#include <cstdint>
#include <cstdlib>
#include <cstdio>
#include <iostream>
#include <memory>
#include <string>
#include <stdexcept>
#include <variant>
#include <vector>
namespace Poco {
struct Exception:std::runtime_error{using std::runtime_error::runtime_error;};
struct Timestamp {Timestamp(){throw std::logic_error("UNQUALIFIED Timestamp constructed");}};
struct LocalDateTime {explicit LocalDateTime(const Timestamp&){throw std::logic_error("UNQUALIFIED Timestamp serializer reached");}};
struct DateTimeFormatter {static std::string format(const LocalDateTime&,const char*){throw std::logic_error("UNQUALIFIED Timestamp formatter reached");}};
struct RegularExpression {RegularExpression(){throw std::logic_error("UNQUALIFIED regex constructed");}};
}
struct FileContentRef {FileContentRef(){throw std::logic_error("UNQUALIFIED FileContent constructed");}std::string path;};
namespace ucr {std::string toUTF8(const std::string&){throw std::logic_error("UNQUALIFIED FileContent serializer reached");}}
struct ValueType2;
#include "value-type.inc"
#include "value-type2.inc"
struct FilterEvalContext{};
#include "expr-node.inc"
#include "version-node.inc"
#include "version-ctor.inc"
#include "to-string-value.inc"
#include "filter-lexer-header.inc"
#include "unescape-quotes.inc"
#include "free-strings.inc"
struct Case {const char* id;const char* hex;const char* body;};
static const Case cases[]={
#include "cases.inc"
};
int digit(char c){return c<='9'?c-'0':c-'a'+10;}
std::string unhex(std::string s){std::string t;for(size_t i=0;i<s.size();i+=2)t.push_back(char(digit(s[i])*16+digit(s[i+1])));return t;}
std::string hex(const std::string& s){std::string t;const char* h="0123456789abcdef";for(unsigned char c:s){t+=h[c>>4];t+=h[c&15];}return t;}
void quote(const char* id,const char* body,const char* kind){auto text=unhex(body);FilterLexer lexer(text);char* p=text.data();int error=99;auto decoded=lexer.UnescapeQuotes(p,error);std::cout<<"{\"kind\":\""<<kind<<"\",\"id\":\""<<id<<"\",\"bodyHex\":\""<<body<<"\",\"decodedHex\":\""<<hex(decoded)<<"\",\"errorCode\":"<<error<<",\"consumedBytes\":"<<(p-text.data())<<"}\n";}
int main(){for(const auto& c:cases){auto text=unhex(c.hex);VersionLiteral v(text);auto value=v.Evaluate(FilterEvalContext{});if(value.index()!=3)return 2;auto serialized=ToStringValue(value);std::cout<<"{\"kind\":\"serialized\",\"id\":\""<<c.id<<"\",\"inputHex\":\""<<c.hex<<"\",\"variantIndex\":3,\"value\":\""<<std::get<int64_t>(value)<<"\",\"serializedUtf8Hex\":\""<<hex(serialized)<<"\"}\n";quote(c.id,c.body,"quote-case");}}
