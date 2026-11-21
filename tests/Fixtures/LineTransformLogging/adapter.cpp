// 検証専用host。原関数の抽出incは無改変。式parser/optimizerは実行しない。
#include <algorithm>
#include <cctype>
#include <cstdint>
#include <functional>
#include <iostream>
#include <memory>
#include <optional>
#include <stdexcept>
#include <string>
#include <variant>
#include <vector>
namespace Poco {
struct Timestamp{}; struct RegularExpression{};
struct Exception:std::runtime_error{using std::runtime_error::runtime_error;};
struct LocalDateTime{explicit LocalDateTime(Timestamp){throw std::runtime_error("unsupported timestamp");}};
struct DateTimeFormatter{static std::string format(LocalDateTime,const char*){throw std::runtime_error("unsupported timestamp");}};
}
struct FileContentRef{std::string path;};
namespace ucr {std::string toUTF8(const std::string& s){return s;}}
struct ValueType2;
using ValueType=std::variant<std::monostate,bool,double,int64_t,Poco::Timestamp,std::shared_ptr<Poco::RegularExpression>,std::string,std::shared_ptr<FileContentRef>,std::shared_ptr<std::vector<ValueType2>>>;
struct ValueType2{ValueType value;};
#include "errors.inc"
struct FilterExpression {
 std::string name,errorMessage; bool caseSensitive=false,optimize=true;
 FilterErrorCode errorCode=FILTER_ERROR_NO_ERROR; int errorPosition=-1;
 std::function<void(int,const std::string&)> logger;
 bool ParseDirective(const std::string&);bool ParseAllDirectives(const std::string&,std::string&);
};
struct FilterEvalContext{FilterExpression* expr;};
struct ExprNode {virtual ValueType Evaluate(const FilterEvalContext&)const=0;virtual ~ExprNode()=default;};
#include "string.inc"
#include "eval-bool.inc"
#include "log.inc"
#include "error.inc"
#include "warn.inc"
#include "info.inc"
#include "if.inc"
#include "directive.inc"
#include "directives.inc"
std::string bytes(std::initializer_list<unsigned> b){std::string s;for(auto x:b)s+=char(x);return s;}
std::string hex(const std::string& s){const char* d="0123456789abcdef";std::string o;for(unsigned char c:s){o+=d[c>>4];o+=d[c&15];}return o;}
using Ptr=std::unique_ptr<ExprNode>;
struct Node:ExprNode {
 std::string op;ValueType value;int level=0;std::vector<Ptr> owned;
 ValueType Evaluate(const FilterEvalContext& c)const override {
  if(op=="value")return value;
  if(op=="throw")throw std::runtime_error("fixture failure");
  std::vector<ExprNode*> args;for(auto& p:owned)args.push_back(p.get());
  if(op=="if")return IfFunc(c,&args);
  if(level==0)return LogErrorFunc(c,&args);
  if(level==1)return LogWarnFunc(c,&args);
  return LogInfoFunc(c,&args);
 }
};
Ptr literal(ValueType v){auto p=std::make_unique<Node>();p->op="value";p->value=std::move(v);return p;}
ValueType array(std::vector<ValueType> vs){auto a=std::make_shared<std::vector<ValueType2>>();for(auto& v:vs)a->push_back({v});return a;}
Ptr call(std::string op,int level,std::initializer_list<Ptr>); // move-only listは使わない
Ptr node(std::string op,int level,std::vector<Ptr> args){auto p=std::make_unique<Node>();p->op=op;p->level=level;p->owned=std::move(args);return p;}
template<class... Ts>std::vector<Ptr> children(Ts... x){std::vector<Ptr> a;(a.push_back(std::move(x)),...);return a;}
std::string kind(const ValueType& v){switch(v.index()){case 0:return "none";case 1:return "bool";case 2:return "double";case 3:return "integer";case 6:return "string";case 8:return "array";default:throw std::runtime_error("unexpected kind");}}
void run(std::string id,std::string directives,Ptr root){
 FilterExpression expr;std::vector<std::pair<int,std::string>> logs;
 expr.logger=[&](int level,const std::string& s){logs.emplace_back(level,s);};
 std::string actual;bool parsed=expr.ParseAllDirectives(directives,actual),success=false;ValueType result;std::string error;
 if(parsed)try{result=root->Evaluate({&expr});success=true;}catch(const std::exception& e){error=e.what();}
 std::cout<<"{\"id\":\""<<id<<"\",\"parsed\":"<<(parsed?"true":"false")<<",\"nameHex\":\""<<hex(expr.name)<<"\",\"actualExpressionHex\":\""<<hex(actual)<<"\",\"caseSensitive\":"<<(expr.caseSensitive?"true":"false")<<",\"optimize\":"<<(expr.optimize?"true":"false")<<",\"errorCode\":"<<expr.errorCode<<",\"errorPosition\":"<<expr.errorPosition<<",\"directiveErrorHex\":\""<<hex(expr.errorMessage)<<"\",\"success\":"<<(success?"true":"false")<<",\"exceptionHex\":\""<<hex(error)<<"\",\"resultKind\":\""<<(success?kind(result):"")<<"\",\"resultHex\":\""<<(success?hex(ToStringValue(result)):"")<<"\",\"logs\":[";
 bool first=true;for(auto& [level,msg]:logs){if(!first)std::cout<<",";first=false;std::cout<<"{\"level\":"<<level<<",\"messageHex\":\""<<hex(msg)<<"\"}";}std::cout<<"]}\n";
}
int main(){
#include "cases.inc"
}
