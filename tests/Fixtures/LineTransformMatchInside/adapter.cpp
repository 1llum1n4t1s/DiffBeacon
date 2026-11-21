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
#include "inside-ranges.inc"
#include "inside.inc"
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
void scan(const char* id,std::vector<Datum> begins,std::vector<Datum> ends,std::string mode){
 FilterExpression e;DIFFITEM di;FilterSharedContext shared, fresh;
 VectorLineDataProvider provider(std::vector<std::string>(begins.size(),"source"),FileTextEncoding{},TableProps{});
 Node b("begin",begins,true),a("end",ends,true),otherB("other-begin",begins,true),otherA("other-end",ends,true);
 FunctionNodeAdapter f(LineMatchInsideFunc,{&b,&a});
 Node ib("inner-begin",std::vector<Datum>(begins.size(),boolDatum(false)),true),ie("inner-end",std::vector<Datum>(begins.size(),boolDatum(false)),true);
 FunctionNodeAdapter inner(LineMatchInsideFunc,{&ib,&ie});
 if(mode=="inner-success-outer-throw"){b.nested=&inner;a.failAt=1;}
 if(mode=="cancel-at-begin")b.failAt=1;
 if(mode=="cancel-at-end")a.failAt=1;
 FilterEvalContext c{&e,&di,mode=="null-provider"?nullptr:&provider,&shared,0};
 std::vector<std::string> steps;
 int iterations=std::max(1,int(begins.size()));
 for(int pass=0;pass<2;++pass){
 if(pass==1&&mode=="new-context")c.sharedContext=&fresh;
 if(pass==1&&mode=="clear-cache")c.sharedContext->insideRanges.clear();
 if(pass==1&&mode=="different-begin")f.args[0]=&otherB;
 if(pass==1&&mode=="different-end")f.args[1]=&otherA;
 if(pass==1&&mode=="different-pair"){f.args[0]=&otherB;f.args[1]=&otherA;}
 if(pass==1&&mode=="swap-pair"){f.args[0]=&a;f.args[1]=&b;}
 if(pass==1&&mode=="same-pair-new-wrapper"){FunctionNodeAdapter replacement(LineMatchInsideFunc,{&b,&a});f=replacement;}
 if(pass==1&&mode=="mutate-cached"){b.rows=std::vector<Datum>(begins.size(),boolDatum(false));a.rows=std::vector<Datum>(ends.size(),boolDatum(false));}
 for(int row=0;row<iterations;++row){
 c.lineIndex=mode=="outside-lines"?(row==0?-1:(row==1?int(begins.size()):2147483647)):row;
 trace.clear();int cacheBefore=int(c.sharedContext->insideRanges.size());std::string v="null",key="null",error="";
 try{auto result=f.Evaluate(c);v=valueJson(result);key="\""+hex(ToStringValue(result))+"\"";}catch(const std::exception& ex){error=hex(ex.what());}
 std::string ranges="";for(auto& pair:c.sharedContext->insideRanges){if(!ranges.empty())ranges+=",";ranges+="[";bool first=true;for(auto r:pair.second){if(!first)ranges+=",";first=false;ranges+="["+std::to_string(r.first)+","+std::to_string(r.second)+"]";}ranges+="]";}
 steps.push_back("{\"pass\":"+std::to_string(pass)+",\"row\":"+std::to_string(row)+",\"value\":"+v+",\"serializedUtf8Hex\":"+key+",\"errorUtf8Hex\":\""+error+"\",\"cacheBefore\":"+std::to_string(cacheBefore)+",\"cacheAfter\":"+std::to_string(c.sharedContext->insideRanges.size())+",\"outerLineIndexAfter\":"+std::to_string(c.lineIndex)+",\"cachedRanges\":["+ranges+"],\"trace\":["+join(trace)+"]}");
 }
 }
 std::cout<<"{\"id\":\""<<id<<"\",\"steps\":["<<join(steps)<<"]}\n";
}
int main(){
#include "cases.inc"
}
