// 検証専用。関数本体は固定Git blobから無改変で切り出す。
#include <algorithm>
#include <cstdint>
#include <functional>
#include <iostream>
#include <map>
#include <memory>
#include <optional>
#include <sstream>
#include <stdexcept>
#include <string>
#include <type_traits>
#include <variant>
#include <vector>
#define PCRE2_CODE_UNIT_WIDTH 8
#include <pcre2.h>
namespace Poco {
// Exception型の接続のみ。regex compile/match/substは原典とPCRE2が実行する。
struct Exception: std::runtime_error {using std::runtime_error::runtime_error;};
struct RegularExpressionException:Exception {using Exception::Exception;};
class RegularExpression {
public:
#include "options.inc"
RegularExpression(const std::string&,int=0,bool=true);
~RegularExpression();
int subst(std::string&,const std::string&,int=0)const;
int subst(std::string&,std::string::size_type,const std::string&,int)const;
private:
std::string::size_type substOne(std::string&,std::string::size_type,const std::string&,int)const;
static int compileOptions(int),matchOptions(int);
void* _pcre;std::map<int,std::string> _groups;
};
struct Timestamp {};
struct LocalDateTime {explicit LocalDateTime(Timestamp){throw Exception("unsupported Timestamp");}};
struct DateTimeFormatter {static std::string format(LocalDateTime,const char*){throw Exception("unsupported Timestamp");}};
}
namespace {
#include "match-data.inc"
;
}
namespace Poco {
#include "poco-subst-forward.inc"
#include "poco-ctor.inc"
#include "poco-dtor.inc"
#include "poco-subst.inc"
#include "poco-one.inc"
#include "poco-compile-options.inc"
#include "poco-match-options.inc"
}
namespace ucr {std::string toUTF8(const std::string& x){return x;}}
struct FileContentRef {std::string path;};
struct ValueType2;
using ValueType=std::variant<std::monostate,bool,double,int64_t,Poco::Timestamp,std::shared_ptr<Poco::RegularExpression>,std::string,std::shared_ptr<FileContentRef>,std::shared_ptr<std::vector<ValueType2>>>;
struct ValueType2 {ValueType value;};
struct FilterExpression {bool caseSensitive;};
struct FilterEvalContext {const FilterExpression* expr;};
#include "ExprNode.inc"
#include "BoolLiteral.inc"
#include "NoneLiteral.inc"
#include "DoubleLiteral.inc"
#include "IntLiteral.inc"
#include "StringLiteral.inc"
#include "DateTimeLiteral.inc"
#include "RegularExpressionLiteral.inc"
#include "ArrayLiteral.inc"
#include "string.inc"
#include "get.inc"
#include "escape.inc"
#include "flags.inc"
#include "create.inc"
#include "subst-string.inc"
#include "subst-regex.inc"
#include "lifting.inc"
#include "replace.inc"
#include "regex-literal-ctor.inc"
struct FunctionNode:ExprNode {
std::string functionName="replace";std::vector<ExprNode*>* args;const FilterExpression* ctxt;
FunctionNode(std::vector<ExprNode*>* a,const FilterExpression* c):args(a),ctxt(c){}
~FunctionNode(){if(args){for(auto* a:*args)delete a;delete args;}}
ValueType Evaluate(const FilterEvalContext& c)const override{return ReplaceFunc(c,args);}
ExprNode* selectedOptimize();
};
#include "literal-result.inc"
#include "optimize-arg.inc"
ExprNode* FunctionNode::selectedOptimize(){
FilterEvalContext ectxt{ctxt};auto func=ReplaceFunc;
// このbranchとhelperだけを実行。旧parser／whole optimizerは対象外。
#include "optimizer-branch.inc"
return this;
}
std::vector<int> evaluated;
struct TraceNode:ExprNode {
ExprNode* child;int index;TraceNode(ExprNode* n,int i):child(n),index(i){}
~TraceNode(){delete child;}
ValueType Evaluate(const FilterEvalContext& c)const override {evaluated.push_back(index);return child->Evaluate(c);}
};
struct ThrowNode:ExprNode {std::string message;explicit ThrowNode(std::string s):message(s){} ValueType Evaluate(const FilterEvalContext&)const override {throw std::runtime_error(message);}};
struct LazyRegex:ExprNode {std::string pattern;explicit LazyRegex(std::string p):pattern(p){}ValueType Evaluate(const FilterEvalContext& c)const override{RegularExpressionLiteral r(pattern,true);return r.Evaluate(c);}};
struct DynamicNode:ExprNode {ExprNode* child;explicit DynamicNode(ExprNode* c):child(c){}~DynamicNode(){delete child;}ValueType Evaluate(const FilterEvalContext& c)const override{return child->Evaluate(c);}};
std::string bytes(std::initializer_list<unsigned> b){std::string s;for(auto x:b)s+=char(x);return s;}
ExprNode* array(std::initializer_list<ExprNode*> args){auto a=std::make_shared<std::vector<ValueType2>>();FilterExpression e{true};FilterEvalContext c{&e};for(auto n:args){a->push_back({n->Evaluate(c)});delete n;}return new ArrayLiteral(a);}
std::string hex(const std::string& s){static const char* d="0123456789abcdef";std::string out;for(unsigned char c:s){out+=d[c>>4];out+=d[c&15];}return out;}
std::string valueJson(const ValueType& v){
if(std::holds_alternative<std::monostate>(v))return "{\"kind\":\"none\"}";
if(auto p=std::get_if<std::string>(&v))return "{\"kind\":\"string\",\"utf8Hex\":\""+hex(*p)+"\"}";
if(auto p=std::get_if<bool>(&v))return std::string("{\"kind\":\"bool\",\"value\":")+(*p?"true":"false")+"}";
if(auto p=std::get_if<int64_t>(&v))return "{\"kind\":\"integer\",\"value\":"+std::to_string(*p)+"}";
if(auto p=std::get_if<std::shared_ptr<std::vector<ValueType2>>>(&v)){std::string s="{\"kind\":\"array\",\"items\":[";bool first=true;for(auto& a:**p){if(!first)s+=",";first=false;s+=valueJson(a.value);}return s+"]}";}
throw std::runtime_error("unexpected output type");
}
void run(const char* id,const char* mode,bool sensitive,bool nullArgs,std::initializer_list<ExprNode*> nodes){
evaluated.clear();FilterExpression e{sensitive};FilterEvalContext c{&e};
auto args=nullArgs?nullptr:new std::vector<ExprNode*>(nodes);bool optimized=std::string(mode)=="optimizer-selected-branch";
if(!optimized&&args){for(size_t i=0;i<args->size();++i)(*args)[i]=new TraceNode((*args)[i],int(i));}
ExprNode* f=new FunctionNode(args,&e);std::string result,error,serialized;bool serializationApplied=false;bool regexNeedle=false,folded=false;
try{
if(optimized){f=static_cast<FunctionNode*>(f)->selectedOptimize();folded=dynamic_cast<StringLiteral*>(f)!=nullptr;if(auto fn=dynamic_cast<FunctionNode*>(f))regexNeedle=fn->args&&fn->args->size()>1&&dynamic_cast<RegularExpressionLiteral*>((*fn->args)[1]);else regexNeedle=folded;}
auto val=f->Evaluate(c);result=valueJson(val);serialized=hex(ToStringValue(val));serializationApplied=true;
}catch(const std::exception& ex){error=ex.what();result="null";}
std::cout<<"{\"id\":\""<<id<<"\",\"mode\":\""<<mode<<"\",\"value\":"<<result<<",\"errorUtf8Hex\":\""<<hex(error)<<"\",\"evaluated\":[";
for(size_t i=0;i<evaluated.size();++i){if(i)std::cout<<",";std::cout<<evaluated[i];}
std::cout<<"],\"regexNeedle\":"<<(regexNeedle?"true":"false")<<",\"folded\":"<<(folded?"true":"false")<<",\"serializedUtf8Hex\":"<<(serializationApplied?"\""+serialized+"\"":"null")<<",\"serializationApplied\":"<<(serializationApplied?"true":"false")<<"}\n";
delete f;
}
int main(){
#include "cases.inc"
}
