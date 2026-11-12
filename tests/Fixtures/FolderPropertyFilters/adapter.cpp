// 旧関数へ渡すAST・比較contextとJSON出力だけを提供する採取adapter。
#define UNICODE
#define _UNICODE
#define NOMINMAX
#include <windows.h>
#include <bcrypt.h>
#include <shobjidl.h>
#include <propsys.h>
#include <propvarutil.h>
#include <algorithm>
#include <cstdint>
#include <functional>
#include <iomanip>
#include <iostream>
#include <memory>
#include <optional>
#include <sstream>
#include <stdexcept>
#include <string>
#include <variant>
#include <vector>
using String=std::wstring;
namespace ucr {
std::string toUTF8(const wchar_t* s) { if(!s)return {}; int n=WideCharToMultiByte(CP_UTF8,0,s,-1,nullptr,0,nullptr,nullptr);std::string r(n,0);WideCharToMultiByte(CP_UTF8,0,s,-1,r.data(),n,nullptr,nullptr);r.pop_back();return r; }
String toTString(const std::string& s) {int n=MultiByteToWideChar(CP_UTF8,0,s.data(),(int)s.size(),nullptr,0);String r(n,0);MultiByteToWideChar(CP_UTF8,0,s.data(),(int)s.size(),r.data(),n);return r;}
}
namespace Poco {
using UInt32=unsigned long;
class Timestamp { public: using TimeVal=int64_t;Timestamp(TimeVal);Timestamp(const Timestamp&);~Timestamp();static Timestamp fromFileTimeNP(UInt32,UInt32);TimeVal epochMicroseconds()const{return _ts;} private:TimeVal _ts;};
#include "timestamp.inc"
}
struct ValueType2;
using ValueType=std::variant<std::monostate,bool,double,int64_t,Poco::Timestamp,std::string,std::shared_ptr<std::vector<ValueType2>>>;
struct ValueType2{ValueType value;};
#include "property-header.inc"
NTSTATUS CalculateHashValue(HANDLE,const wchar_t*,std::vector<uint8_t>&);
#include "hash.inc"
#include "property-system.inc"
struct DiffCode{unsigned mask=0;bool exists(int n)const{return n>=0&&n<3&&(mask&(1u<<n));}};
struct FileInfo{String path,filename;std::unique_ptr<PropertyValues> m_pAdditionalProperties;};
struct DIFFITEM{DiffCode diffcode;FileInfo diffFileInfo[3];};
struct DiffContext{int dirs=2;String roots[3];PropertySystem* m_pPropertySystem=nullptr;int GetCompareDirs()const{return dirs;}String GetPath(int i)const{return roots[i];}};
struct FilterExpression{DiffContext* ctxt;};
struct FilterEvalContext{const FilterExpression* expr;const DIFFITEM* di;};
namespace paths {String ConcatPath(const String&a,const String&b){if(a.empty())return b;if(b.empty())return a;return a+L"\\"+b;}}
struct ExprNode{virtual ~ExprNode()=default;};
struct StringLiteral:ExprNode{std::string value;explicit StringLiteral(std::string v):value(v){}};
struct InvalidArgumentCountError:std::runtime_error{using std::runtime_error::runtime_error;};
struct InvalidArgumentError:std::runtime_error{using std::runtime_error::runtime_error;};
struct InvalidPropertyNameError:std::runtime_error{using std::runtime_error::runtime_error;};
struct FunctionNode{std::string functionName;std::vector<ExprNode*>* args=nullptr;std::function<ValueType(const FilterEvalContext&,std::vector<ExprNode*>*)> func;void SetPropFunc(int,int,bool);};
struct AttrParseResult{int side,prefixlen;};
#include "prefix.inc"
#include "conversion.inc"
#include "prop.inc"
#include "propary.inc"
#include "set.inc"
#include "contains.inc"
#include "bool.inc"
std::string q(const std::string&s){std::ostringstream o;o<<'"';for(unsigned char c:s){if(c=='"'||c=='\\')o<<'\\'<<char(c);else if(c<32)o<<"\\u"<<std::hex<<std::setw(4)<<std::setfill('0')<<int(c);else o<<char(c);}o<<'"';return o.str();}
std::string typed(const ValueType&v){
 if(std::holds_alternative<std::monostate>(v))return "{\"type\":\"none\"}";
 if(auto p=std::get_if<bool>(&v))return std::string("{\"type\":\"bool\",\"value\":")+(*p?"true}":"false}");
 if(auto p=std::get_if<int64_t>(&v))return "{\"type\":\"integer\",\"value\":"+q(std::to_string(*p))+"}";
 if(auto p=std::get_if<double>(&v)){std::ostringstream o;o<<std::setprecision(17)<<*p;return "{\"type\":\"double\",\"value\":"+o.str()+"}";}
 if(auto p=std::get_if<Poco::Timestamp>(&v))return "{\"type\":\"timestamp-us\",\"value\":"+q(std::to_string(p->epochMicroseconds()))+"}";
 if(auto p=std::get_if<std::string>(&v))return "{\"type\":\"string\",\"value\":"+q(*p)+"}";
 auto a=std::get<std::shared_ptr<std::vector<ValueType2>>>(v);std::string s="{\"type\":\"array\",\"value\":[";bool first=true;for(auto&x:*a){if(!first)s+=",";first=false;s+=typed(x.value);}return s+"]}";
}
void emit(const std::string&id,const ValueType&v,const std::string&extra="") {auto b=evalAsBool(v);std::cout<<"{\"id\":"<<q(id)<<",\"value\":"<<typed(v)<<",\"containsTrue\":"<<(ContainsTrue(v)?"true":"false")<<",\"evalAsBool\":"<<(b?(*b?"true":"false"):"null")<<extra<<"}\n";}
ValueType array(std::initializer_list<ValueType> v){auto a=std::make_shared<std::vector<ValueType2>>();for(auto&x:v)a->push_back({x});return a;}
void cv(const char*id,PROPVARIANT&p){emit(id,ConvertPROPVARIANTToValueType(p),",\"vt\":"+std::to_string(p.vt));}
void conversions(){
 PROPVARIANT p{};cv("convert-empty",p);p.vt=VT_I4;p.lVal=INT32_MIN;cv("convert-i4",p);p.vt=VT_UI4;p.ulVal=UINT32_MAX;cv("convert-ui4",p);p.vt=VT_I8;p.hVal.QuadPart=INT64_MIN;cv("convert-i8",p);p.vt=VT_UI8;p.uhVal.QuadPart=UINT64_MAX;cv("convert-ui8-overflow",p);
 p.vt=VT_R8;p.dblVal=-1.25;cv("convert-r8",p);p.vt=VT_R4;p.fltVal=2.5f;cv("convert-r4-unsupported",p);
 p.vt=VT_LPWSTR;p.pwszVal=const_cast<wchar_t*>(L"日本語😀");cv("convert-wide",p);p.pwszVal=const_cast<wchar_t*>(L"");cv("convert-wide-empty",p);
 p.vt=VT_BOOL;for(short x:{short(0),short(-1),short(1),short(2)}){p.boolVal=x;cv(("convert-bool-"+std::to_string(x)).c_str(),p);}
 p.vt=VT_FILETIME;for(uint64_t ft:{uint64_t(116444736000000000),uint64_t(116444736012345679),uint64_t(0)}){p.filetime={DWORD(ft),DWORD(ft>>32)};cv(("convert-filetime-"+std::to_string(ft)).c_str(),p);}
 wchar_t*a[]={const_cast<wchar_t*>(L"a"),const_cast<wchar_t*>(L"日本語"),const_cast<wchar_t*>(L"")};p={};p.vt=VT_VECTOR|VT_LPWSTR;p.calpwstr={3,a};cv("convert-string-vector",p);p.calpwstr.cElems=0;cv("convert-string-vector-empty",p);
 BYTE bytes[]={0,1,15,16,127,128,254,255};p={};p.vt=VT_VECTOR|VT_UI1;p.caub={8,bytes};cv("convert-byte-vector",p);p.caub.cElems=0;cv("convert-byte-vector-empty",p);
 for(VARTYPE vt:std::initializer_list<VARTYPE>{VT_NULL,VT_I2,VT_BSTR,VARTYPE(VT_VECTOR|VT_I4)}){p={};p.vt=vt;cv(("convert-unsupported-"+std::to_string(vt)).c_str(),p);}
 emit("truth-scalar-integer",int64_t(1));emit("truth-scalar-string",std::string("true"));emit("truth-scalar-double",1.0);emit("truth-array-integer",array({int64_t(1)}));emit("truth-array-true",array({false,true}));emit("truth-nested-true",array({array({true})}));emit("truth-nested-string",array({array({std::string("true")})}));
}
void seed(DIFFITEM&di){for(int i=0;i<3;++i){auto p=std::make_unique<PropertyValues>();p->Resize(2);(*p)[0].vt=VT_I4;(*p)[0].lVal=(i+1)*10;const wchar_t*v[]={i==0?L"left":i==1?L"middle":L"right",L"日本語"};InitPropVariantFromStringVector(v,2,&(*p)[1]);di.diffFileInfo[i].m_pAdditionalProperties=std::move(p);}}
void sides(){
 PropertySystem ps({L"System.Size",L"System.Keywords"});
 for(int dirs=1;dirs<=3;++dirs)for(unsigned mask=0;mask<(1u<<dirs);++mask)for(auto name:{"prop","leftprop","middleprop","rightprop"}){
  DiffContext dc;dc.dirs=dirs;dc.m_pPropertySystem=&ps;FilterExpression ex{&dc};DIFFITEM di;di.diffcode.mask=mask;seed(di);FilterEvalContext ec{&ex,&di};StringLiteral lit("System.Size");std::vector<ExprNode*> args{&lit};FunctionNode f;f.functionName=name;f.args=&args;auto [side,prefix]=ParseAttributeName(name);f.SetPropFunc(side,prefix,dirs==1);emit("side-"+std::to_string(dirs)+"-"+std::to_string(mask)+"-"+name,f.func(ec,&args));
 }
 DiffContext dc;dc.dirs=3;dc.m_pPropertySystem=&ps;FilterExpression ex{&dc};DIFFITEM di;di.diffcode.mask=7;seed(di);FilterEvalContext ec{&ex,&di};emit("side-nested-keywords",propary(L"System.Keywords",ec));emit("side-unregistered",prop(0,L"System.Unregistered",ec));
}
void validation(){
 for(auto name:{"System.Size","system.size","System.FileName","System.NotARealProperty.20261017","Hash.MD5","Hash.SHA1","Hash.SHA256","hash.sha256","Hash.Sha256",""}){
  PropertySystem ps({ucr::toTString(name)});emit("canonical-"+std::string(name),int64_t(ps.GetPropertyIndex(ucr::toTString(name))));
 }
 for(auto mode:{"null","zero","two","nonliteral","invalid","valid"}){StringLiteral good("System.Size"),bad("Bogus.Property");ExprNode non;std::vector<ExprNode*> a;if(std::string(mode)=="two")a={&good,&good};if(std::string(mode)=="nonliteral")a={&non};if(std::string(mode)=="invalid")a={&bad};if(std::string(mode)=="valid")a={&good};FunctionNode f;f.functionName="prop";f.args=std::string(mode)=="null"?nullptr:&a;std::string error;try{f.SetPropFunc(0,0,false);}catch(const std::exception&e){error=e.what();}std::cout<<"{\"id\":"<<q("validation-"+std::string(mode))<<",\"error\":"<<q(error)<<"}\n";}
}
void realFiles(){
 auto absolute=[](const wchar_t* file){wchar_t p[32768];DWORD n=GetFullPathNameW(file,32768,p,nullptr);if(!n||n>=32768)throw std::runtime_error("absolute path");return String(p);};
 std::vector<String> names={L"System.Size",L"System.FileName",L"System.FileExtension",L"System.DateModified",L"Hash.MD5",L"Hash.SHA1",L"Hash.SHA256"};
 for(auto file:{L"empty.bin",L"unicode-日本語.bin",L"boundary.bin",L"absent.bin"})for(bool hashes:{false,true}){
  std::vector<String> selected=hashes?std::vector<String>{names[4],names[5],names[6]}:names;PropertySystem ps(selected);PropertyValues v;bool ok=ps.GetPropertyValues(absolute(file),v);for(size_t i=0;i<v.GetSize();++i)emit("file-"+ucr::toUTF8(file)+"-"+(hashes?"hashonly-":"mixed-")+ucr::toUTF8(selected[i].c_str()),ConvertPROPVARIANTToValueType(v[i]),",\"vt\":"+std::to_string(v[i].vt)+",\"serviceSuccess\":"+(ok?"true":"false"));
 }
 // 実サービスから読み、同じDIFFITEMの再呼出しでは旧propのcacheを観測する。
 PropertySystem ps({L"System.Size"});DiffContext dc;dc.dirs=1;dc.m_pPropertySystem=&ps;FilterExpression ex{&dc};DIFFITEM di;di.diffcode.mask=1;di.diffFileInfo[0].filename=absolute(L"cache.bin");FilterEvalContext ec{&ex,&di};emit("cache-before",prop(0,L"System.Size",ec));HANDLE h=CreateFileW(L"cache.bin",GENERIC_WRITE,0,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr);if(h==INVALID_HANDLE_VALUE)throw std::runtime_error("cache open");SetFilePointer(h,8,nullptr,FILE_BEGIN);if(!SetEndOfFile(h))throw std::runtime_error("cache resize");CloseHandle(h);emit("cache-after-file-change",prop(0,L"System.Size",ec));di.diffFileInfo[0].m_pAdditionalProperties.reset();emit("cache-after-reset",prop(0,L"System.Size",ec));
}
int main(){HRESULT hr=CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);if(FAILED(hr))return 2;try{conversions();sides();validation();realFiles();}catch(const std::exception&e){std::cerr<<e.what();CoUninitialize();return 3;}CoUninitialize();return 0;}
