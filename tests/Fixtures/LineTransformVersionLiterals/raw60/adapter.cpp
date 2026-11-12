// 検証用接続。三つのincは固定原本の無改変bytes。
#include <cstdint>
#include <cstdio>
#include <iostream>
#include <string>
#include <variant>
// 未使用の元ValueType armとFilterEvalContextを検証専用stubへ縮小。
using ValueType=std::variant<std::monostate,bool,double,int64_t>;
struct FilterEvalContext{};
#include "expr-node.inc"
#include "version-node.inc"
#include "version-ctor.inc"
struct Case {const char* id;const char* hex;};
static const Case cases[]={
#include "cases.inc"
};
int digit(char c){return c<='9'?c-'0':c-'a'+10;}
int main(){for(const auto& c:cases){std::string text;std::string hex=c.hex;for(size_t i=0;i<hex.size();i+=2)text.push_back(char(digit(hex[i])*16+digit(hex[i+1])));VersionLiteral v(text);auto value=v.Evaluate(FilterEvalContext{});if(value.index()!=3)return 2;std::cout<<"{\"id\":\""<<c.id<<"\",\"inputHex\":\""<<hex<<"\",\"variantIndex\":3,\"value\":\""<<std::get<int64_t>(value)<<"\"}\n";}}
