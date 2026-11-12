// 検証専用接続。inc は固定Git原本bytesの無改変抽出。parserは実行しない。
#define NOMINMAX
#define POCO_OS_FAMILY_WINDOWS
#include <Windows.h>
#include <algorithm>
#include <bit>
#include <cmath>
#include <cstdint>
#include <ctime>
#include <functional>
#include <iomanip>
#include <iostream>
#include <map>
#include <memory>
#include <optional>
#include <sstream>
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
#include "timestamp-epoch.inc"
#include "timestamp-default.inc"
#include "timestamp-value.inc"
#include "timestamp-copy.inc"
#include "timestamp-dtor.inc"
#include "timestamp-assign.inc"
#include "timestamp-update.inc"
#include "ascii-class.inc"
#include "ascii-properties.inc"
#include "ascii-isascii.inc"
#include "ascii-hasproperties.inc"
#include "ascii-isupper.inc"
#include "ascii-lower.inc"
#include "ascii-table.inc"
#include "icompare.inc"
struct Exception:std::runtime_error{using std::runtime_error::runtime_error;};
struct RegularExpressionException:Exception{using Exception::Exception;std::string message()const{return what();}};
// typed Timestamp のキー整形は未表現境界。実行しないので代替formatterをgoldenにしない。
struct LocalDateTime{explicit LocalDateTime(Timestamp){throw std::runtime_error("UNSUPPORTED Timestamp formatting reached");}};
struct DateTimeFormatter{static std::string format(LocalDateTime,const char*){throw std::runtime_error("UNSUPPORTED Timestamp formatting reached");}};
struct RegularExpression{};
}
using String=std::string;
namespace ucr{std::string toUTF8(const String& x){return x;}String toTString(const std::string& x){return x;}}
struct FileContentRef{std::string path;};
struct DIFFITEM{};
struct ValueType2;
using ValueType=std::variant<std::monostate,bool,double,int64_t,Poco::Timestamp,std::shared_ptr<Poco::RegularExpression>,std::string,std::shared_ptr<FileContentRef>,std::shared_ptr<std::vector<ValueType2>>>;
struct ValueType2{ValueType value;};
struct FilterExpression;
struct FilterSharedContext;
struct ExprNode;
#include "provider-interface.inc"
#include "context.inc"
#include "statistics-result.inc"
using Range=std::pair<int,int>;
using InsideKey=std::pair<ExprNode*,ExprNode*>;
#include "range-info.inc"
#include "shared-context.inc"
#include "ExprNode.inc"
#include "filter-error-code.inc"
struct FilterExpression{
 bool caseSensitive=true;
 FilterErrorCode errorCode=FILTER_ERROR_NO_ERROR;int errorPosition=-1;
 std::string errorMessage;
 ExprNode* rootNode=nullptr;
 inline static std::function<void(int,const std::string&)> logger;
 std::string TransformLine(const FilterEvalContext&);
};
#include "BoolLiteral.inc"
#include "NoneLiteral.inc"
#include "DoubleLiteral.inc"
#include "IntLiteral.inc"
#include "StringLiteral.inc"
#include "DateTimeLiteral.inc"
#include "ArrayLiteral.inc"
#include "eval-bool.inc"
#include "statistics-process.inc"
#include "statistics-cache.inc"
#include "average.inc"
#include "sum.inc"
#include "maximum.inc"
#include "minimum.inc"
#include "string.inc"
#include "convert-string.inc"
#include "transform-line.inc"
// このhost型は原Vector providerの保管先だけ。encoding/table parserは検証対象外。
struct FileTextEncoding{};
struct TableProps{char quote='"',delimiter=',';bool istable=false;};
#include "vector-provider.inc"
#include "apply-caller.inc"

std::string bytes(std::initializer_list<unsigned> b){std::string s;for(auto x:b)s+=char(x);return s;}
std::string hex(const std::string& s){static const char* d="0123456789abcdef";std::string o;for(unsigned char c:s){o+=d[c>>4];o+=d[c&15];}return o;}
std::string bits(double x){std::ostringstream s;s<<std::hex<<std::setw(16)<<std::setfill('0')<<std::bit_cast<uint64_t>(x);return s.str();}
std::string valueJson(const ValueType& v){
 if(std::holds_alternative<std::monostate>(v))return "{\"kind\":\"none\"}";
 if(auto p=std::get_if<bool>(&v))return std::string("{\"kind\":\"bool\",\"value\":")+(*p?"true":"false")+"}";
 if(auto p=std::get_if<int64_t>(&v))return "{\"kind\":\"integer\",\"decimal\":\""+std::to_string(*p)+"\"}";
 if(auto p=std::get_if<double>(&v))return "{\"kind\":\"double\",\"bitsHex\":\""+bits(*p)+"\"}";
 if(auto p=std::get_if<std::string>(&v))return "{\"kind\":\"string\",\"utf8Hex\":\""+hex(*p)+"\"}";
 if(auto p=std::get_if<Poco::Timestamp>(&v))return "{\"kind\":\"timestamp\",\"microseconds\":\""+std::to_string(p->epochMicroseconds())+"\"}";
 if(auto p=std::get_if<std::shared_ptr<std::vector<ValueType2>>>(&v)){std::string s="{\"kind\":\"array\",\"items\":[";bool first=true;for(auto& x:**p){if(!first)s+=",";first=false;s+=valueJson(x.value);}return s+"]}";}
 throw std::runtime_error("unexpected output alternative");
}
struct Datum{ValueType value;std::string error;};
Datum integer(int64_t x){return {ValueType(x),{}};}Datum number(double x){return {ValueType(x),{}};}
Datum str(std::string x){return {ValueType(x),{}};}Datum boolDatum(bool x){return {ValueType(x),{}};}
Datum none(){return {ValueType(std::monostate{}),{}};}Datum timestamp(int64_t x){return {ValueType(Poco::Timestamp(x)),{}};}
Datum fail(std::string x){return {ValueType(std::monostate{}),x};}
Datum array(std::initializer_list<Datum> d){auto v=std::make_shared<std::vector<ValueType2>>();for(auto& x:d){if(!x.error.empty())throw std::runtime_error("unsupported array throw datum");v->push_back({x.value});}return {ValueType(v),{}};}
#include "extract-ranges.inc"
#include "distances.inc"
#include "match-ranges.inc"
#include "matchcount.inc"
#include "matchnumber.inc"
#include "matchdistance-ex.inc"
#include "matchdistance.inc"
#include "matchdistance-before.inc"
#include "matchdistance-after.inc"
std::vector<std::string> trace;
std::string join(const std::vector<std::string>& xs){std::string s;for(auto& x:xs){if(!s.empty())s+=",";s+=x;}return s;}
struct Node:ExprNode{
 std::string name;std::vector<Datum> rows;bool sequence;int failAt=-1;const ExprNode* nested=nullptr;
 Node(std::string n,std::vector<Datum> r,bool q):name(n),rows(r),sequence(q){}
 ValueType Evaluate(const FilterEvalContext& c)const override{
 trace.push_back("{\"node\":\""+name+"\",\"lineIndex\":"+std::to_string(c.lineIndex)+"}");
 if(c.lineIndex==failAt)throw std::runtime_error("scan-error");
 if(nested)return nested->Evaluate(c);
 const auto& d=rows.at(sequence?c.lineIndex:0);if(!d.error.empty())throw std::runtime_error(d.error);return d.value;
 }
};
using Function=ValueType(*)(const FilterEvalContext&,std::vector<ExprNode*>*);
struct FunctionNodeAdapter:ExprNode{
 Function fn;std::vector<ExprNode*> args;
 FunctionNodeAdapter(Function f,std::vector<ExprNode*> a):fn(f),args(a){}
 ValueType Evaluate(const FilterEvalContext& c)const override{return fn(c,const_cast<std::vector<ExprNode*>*>(&args));}
};
void scan(const char* id,Function fn,std::vector<Datum> rows,Datum before,Datum after,std::string mode){
 FilterExpression e;DIFFITEM di;FilterSharedContext shared;
 VectorLineDataProvider provider(std::vector<std::string>(rows.size(),"source"),FileTextEncoding{},TableProps{});
 Node predicate("predicate",rows,true),b("before",{before},false),a("after",{after},false),other("other-predicate",rows,true);
 FunctionNodeAdapter f(fn,{&predicate});
 Node innerPredicate("inner-predicate",rows,true);
 FunctionNodeAdapter inner(MatchCountFunc,{&innerPredicate});
 FunctionNodeAdapter sum(SumFunc,{&innerPredicate});
 if(mode=="throw-scan")predicate.failAt=1;
 if(mode=="nested-match")predicate.nested=&inner;
 if(mode=="nested-aggregate")predicate.nested=&sum;
 if(mode=="outer-throw-after-inner"){predicate.nested=&inner;predicate.failAt=1;}
 FilterEvalContext c{&e,&di,mode=="null-provider"?nullptr:&provider,&shared,0};
 std::vector<std::string> steps;
 int iterations=std::max(1,int(rows.size()));
 for(int pass=0;pass<2;++pass){
 if(pass==1&&mode=="new-context")shared.matchRanges.clear();
 if(pass==1&&mode=="shared-functions")f.fn=fn==MatchCountFunc?LineMatchNumberFunc:MatchCountFunc;
 if(pass==1&&mode=="different-ast"){f.args[0]=&other;}
 if(pass==1&&mode=="mutate-cached")predicate.rows=std::vector<Datum>(rows.size(),boolDatum(false));
 for(int row=0;row<iterations;++row){
 c.lineIndex=mode=="outside-lines"?(row==0?-1:(row==1?int(rows.size()):2147483647)):row;trace.clear();int cacheBefore=int(shared.matchRanges.size());std::string v="null",key="null",error="";
 try{auto result=f.Evaluate(c);v=valueJson(result);key="\""+hex(ToStringValue(result))+"\"";}catch(const std::exception& ex){error=hex(ex.what());}
 steps.push_back("{\"pass\":"+std::to_string(pass)+",\"row\":"+std::to_string(row)+",\"value\":"+v+",\"serializedUtf8Hex\":"+key+",\"errorUtf8Hex\":\""+error+"\",\"cacheBefore\":"+std::to_string(cacheBefore)+",\"cacheAfter\":"+std::to_string(shared.matchRanges.size())+",\"outerLineIndexAfter\":"+std::to_string(c.lineIndex)+",\"trace\":["+join(trace)+"]}");
 }
 }
 std::cout<<"{\"id\":\""<<id<<"\",\"steps\":["<<join(steps)<<"]}\n";
}
int main(){
#include "cases.inc"
}
