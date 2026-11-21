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
#include "SizeLiteral.inc"
#include "DurationLiteral.inc"
#include "VersionLiteral.inc"
#include "NegateNode.inc"
#include "duration-ctor.inc"
#include "const-int.inc"
#include "negate-opt.inc"
#include "negate-eval.inc"
#include "array-func.inc"
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

struct ArrayConnectionNode : ExprNode { std::vector<ExprNode*> nodes; explicit ArrayConnectionNode(std::vector<ExprNode*> n):nodes(std::move(n)){} ~ArrayConnectionNode(){for(auto n:nodes)delete n;} ValueType Evaluate(const FilterEvalContext& c)const override{return ArrayFunc(c,const_cast<std::vector<ExprNode*>*>(&nodes));} };
std::string rawStodProbe(const std::string& input){size_t pos=0;while(pos<input.size()&&(isdigit(input[pos])||input[pos]=='.'))++pos;auto numberPart=input.substr(0,pos);try{size_t used=0;auto number=std::stod(numberPart,&used);return "{\"numberPartUtf8Hex\":\""+hex(numberPart)+"\",\"status\":\"returned\",\"consumedCharacters\":"+std::to_string(used)+",\"doubleBitsHex\":\""+bits(number)+"\"}";}catch(const std::invalid_argument&){return "{\"numberPartUtf8Hex\":\""+hex(numberPart)+"\",\"status\":\"invalid_argument\",\"consumedCharacters\":null,\"doubleBitsHex\":null}";}catch(const std::out_of_range&){return "{\"numberPartUtf8Hex\":\""+hex(numberPart)+"\",\"status\":\"out_of_range\",\"consumedCharacters\":null,\"doubleBitsHex\":null}";}}
void observe(const char* id,const char* mode,const char* category,ExprNode* node,const std::string& input,const char* aggregate,int providerCount,bool optimize){
 try{
  if(optimize)node=node->Optimize();
  FilterSharedContext shared;FilterExpression expression;
  VectorLineDataProvider provider(std::vector<String>(providerCount,""),FileTextEncoding{},TableProps{});
  FilterEvalContext context{};context.sharedContext=&shared;context.expr=&expression;context.provider=&provider;context.lineIndex=0;
  std::vector<ExprNode*> args{node};ValueType value;
  if(std::string(aggregate)=="Average")value=AverageFunc(context,&args);
  else if(std::string(aggregate)=="Sum")value=SumFunc(context,&args);
  else if(std::string(aggregate)=="Maximum")value=MaximumFunc(context,&args);
  else if(std::string(aggregate)=="Minimum")value=MinimumFunc(context,&args);
  else value=node->Evaluate(context);
  auto text=ToStringValue(value);
  std::cout<<"{\"id\":\""<<id<<"\",\"mode\":\""<<mode<<"\",\"classification\":\""<<category<<"\",\"inputUtf8Hex\":\""<<hex(input)<<"\",\"optimizerInvoked\":"<<(optimize?"true":"false")<<",\"rawStodProbe\":"<<(std::string(mode)=="array"?"null":rawStodProbe(input))<<",\"parserExecuted\":false,\"directiveExecuted\":false,\"returned\":true,\"value\":"<<valueJson(value)<<",\"serializedUtf8Hex\":\""<<hex(text)<<"\"}\n";
 }catch(const std::exception& e){std::cerr<<id<<":"<<e.what()<<"\n";throw;}
 delete node;
}
int main(){
#include "cases.inc"
}
