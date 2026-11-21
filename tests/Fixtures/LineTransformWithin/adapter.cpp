// 外部検証専用。inc は固定原典bytesの無改変抽出。parserと全optimizerは対象外。
#include <algorithm>
#include <cstdint>
#include <ctime>
#include <iostream>
#include <memory>
#include <optional>
#include <stdexcept>
#include <string>
#include <variant>
#include <vector>
#define Foundation_API
namespace Poco {
using Int64=int64_t;using UInt32=uint32_t;
class Timespan;
#include "timestamp-class.inc"
#include "timestamp-opeq.inc"
#include "timestamp-opne.inc"
#include "timestamp-opgt.inc"
#include "timestamp-opge.inc"
#include "timestamp-oplt.inc"
#include "timestamp-ople.inc"
#include "timestamp-value.inc"
#include "timestamp-copy.inc"
#include "timestamp-dtor.inc"
#include "ascii-class.inc"
#include "ascii-properties.inc"
#include "ascii-isascii.inc"
#include "ascii-hasproperties.inc"
#include "ascii-isupper.inc"
#include "ascii-lower.inc"
#include "ascii-table.inc"
#include "icompare.inc"
// 到達禁止の接続。IsWithin出力はbool/none/arrayのみ。Timestamp formattingは採取しない。
struct Exception:std::runtime_error{using std::runtime_error::runtime_error;};
struct LocalDateTime{explicit LocalDateTime(Timestamp){throw std::runtime_error("UNREPRESENTABLE Timestamp formatting reached");}};
struct DateTimeFormatter{static std::string format(LocalDateTime,const char*){throw std::runtime_error("UNREPRESENTABLE Timestamp formatting reached");}};
struct RegularExpression{};
}
namespace ucr {std::string toUTF8(const std::string& x){return x;}}
struct FileContentRef {std::string path;};
struct ValueType2;
using ValueType=std::variant<std::monostate,bool,double,int64_t,Poco::Timestamp,std::shared_ptr<Poco::RegularExpression>,std::string,std::shared_ptr<FileContentRef>,std::shared_ptr<std::vector<ValueType2>>>;
struct ValueType2{ValueType value;};
struct FilterExpression{bool caseSensitive;};
struct FilterEvalContext{const FilterExpression* expr;};
#include "ExprNode.inc"
#include "BoolLiteral.inc"
#include "NoneLiteral.inc"
#include "DoubleLiteral.inc"
#include "IntLiteral.inc"
#include "StringLiteral.inc"
#include "DateTimeLiteral.inc"
#include "ArrayLiteral.inc"
#include "lifting.inc"
#include "iswithin.inc"
#include "string.inc"
std::vector<int> evaluated,optimized;
struct TraceNode:ExprNode{
 ExprNode* child;int index;TraceNode(ExprNode* n,int i):child(n),index(i){}
 ~TraceNode(){delete child;}
 ValueType Evaluate(const FilterEvalContext& c)const override{evaluated.push_back(index);return child->Evaluate(c);}
 ExprNode* Optimize()override{optimized.push_back(index);child=child->Optimize();return this;}
};
struct ThrowNode:ExprNode{std::string msg;explicit ThrowNode(std::string m):msg(m){}ValueType Evaluate(const FilterEvalContext&)const override{throw std::runtime_error(msg);}};
struct FunctionNode:ExprNode{
 std::vector<ExprNode*>* args;explicit FunctionNode(std::vector<ExprNode*>* a):args(a){}
 ~FunctionNode(){for(auto* n:*args)delete n;delete args;}
 ValueType Evaluate(const FilterEvalContext& c)const override{return IsWithinFunc(c,args);}
 ExprNode* Optimize()override{
#include "optimizer-visit.inc"
 // 原典全体にiswithin固有fold branchがないことをsource ranges/hashで別途確認。
 return this;
 }
};
std::string bytes(std::initializer_list<unsigned> b){std::string s;for(auto x:b)s+=char(x);return s;}
ExprNode* array(std::initializer_list<ExprNode*> nodes){auto a=std::make_shared<std::vector<ValueType2>>();FilterExpression e{true};FilterEvalContext c{&e};for(auto n:nodes){a->push_back({n->Evaluate(c)});delete n;}return new ArrayLiteral(a);}
std::string hex(const std::string& s){static const char* d="0123456789abcdef";std::string out;for(unsigned char c:s){out+=d[c>>4];out+=d[c&15];}return out;}
std::string valueJson(const ValueType& v){
 if(std::holds_alternative<std::monostate>(v))return "{\"kind\":\"none\"}";
 if(auto p=std::get_if<bool>(&v))return std::string("{\"kind\":\"bool\",\"value\":")+(*p?"true":"false")+"}";
 if(auto p=std::get_if<std::shared_ptr<std::vector<ValueType2>>>(&v)){std::string s="{\"kind\":\"array\",\"items\":[";bool first=true;for(auto& x:**p){if(!first)s+=",";first=false;s+=valueJson(x.value);}return s+"]}";}
 throw std::runtime_error("unexpected IsWithin output");
}
void run(const char* id,bool sensitive,bool opt,std::initializer_list<ExprNode*> nodes){
 evaluated.clear();optimized.clear();FilterExpression e{sensitive};FilterEvalContext c{&e};auto args=new std::vector<ExprNode*>(nodes);
 for(size_t i=0;i<args->size();++i)(*args)[i]=new TraceNode((*args)[i],int(i));
 ExprNode* f=new FunctionNode(args);std::string result="null",error,serialized;bool applied=false,folded=false;
 try{if(opt){auto old=f;f=f->Optimize();folded=f!=old;}auto v=f->Evaluate(c);result=valueJson(v);serialized=hex(ToStringValue(v));applied=true;}catch(const std::exception& x){error=x.what();}
 std::cout<<"{\"id\":\""<<id<<"\",\"value\":"<<result<<",\"errorUtf8Hex\":\""<<hex(error)<<"\",\"evaluated\":[";
 for(size_t i=0;i<evaluated.size();++i){if(i)std::cout<<",";std::cout<<evaluated[i];}
 std::cout<<"],\"optimized\":[";for(size_t i=0;i<optimized.size();++i){if(i)std::cout<<",";std::cout<<optimized[i];}
 std::cout<<"],\"folded\":"<<(folded?"true":"false")<<",\"serializedUtf8Hex\":"<<(applied?"\""+serialized+"\"":"null")<<",\"serializationApplied\":"<<(applied?"true":"false")<<"}\n";delete f;
}
int main(){
#include "cases.inc"
}
